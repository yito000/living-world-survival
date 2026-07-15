"""WorldState FastAPI application (M0 skeleton).

Exposes liveness (/healthz) and readiness (/readyz) endpoints. On startup it
connects to NATS in the background; connectivity is reflected in /readyz.

Per BSD 4.3 / R7, no heavy work is placed in the request handler path.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
from collections.abc import AsyncIterator
from typing import Any

from fastapi import FastAPI
from fastapi.responses import JSONResponse

from app.consumer import Consumer, InMemoryDedup, build_pg_dedup

# 購読土台の受信/重複ログ（INFO）が既定の WARNING で消えないよう明示的に設定する。
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("worldstate")

# nats-py is optional at import time so that `python -c "import app.main"` works
# even before dependencies for the broker are installed/available.
try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]


class _State:
    """Holds the live NATS connection and the event-subscription consumer (if any)."""

    nc: Any | None = None
    consumer: Any | None = None


state = _State()


async def _connect_nats() -> None:
    url = os.getenv("NATS_URL", "nats://localhost:4222")
    if nats is None:
        return
    # 起動時に NATS がまだ未起動でも lifespan を無限ブロックしないよう、短い試行を数回
    # 繰り返す（起動順の競合を吸収）。一度接続できれば、以降の切断は
    # max_reconnect_attempts=-1 で自動再接続する。全て失敗しても nc=None のままで
    # health は 200 を返し続ける（readyz が 503 で依存不足を示す）。
    for attempt in range(5):
        try:
            state.nc = await asyncio.wait_for(
                nats.connect(
                    url,
                    max_reconnect_attempts=-1,
                    reconnect_time_wait=2,
                    connect_timeout=2,
                ),
                timeout=6,
            )
            logger.info("worldstate: connected to NATS at %s", url)
            return
        except Exception:
            logger.info("worldstate: NATS connect failed (attempt %d), retrying...", attempt + 1)
            await asyncio.sleep(1)


async def _start_consumer() -> None:
    """NATS 接続後に購読土台を起動する（M3 / 06B 3.3）。dedup は Postgres、無ければ
    in-memory にフォールバックする。インフラ未起動でも lifespan は失敗させない。失敗は
    ログに残す（黙って握り潰すと購読土台が動かない原因が見えないため）。"""
    if state.nc is None:
        return
    try:
        dedup = await build_pg_dedup() or InMemoryDedup()
        logger.info("worldstate: dedup backend = %s", type(dedup).__name__)
        consumer = Consumer(state.nc, dedup)
        await consumer.start()
        state.consumer = consumer
    except Exception:
        logger.exception("worldstate: consumer failed to start (health stays up)")


@contextlib.asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    await _connect_nats()
    await _start_consumer()
    try:
        yield
    finally:
        if state.consumer is not None:
            with contextlib.suppress(Exception):
                await state.consumer.stop()
        if state.nc is not None:
            with contextlib.suppress(Exception):
                await state.nc.drain()


app = FastAPI(title="worldstate", version="0.1.0", lifespan=lifespan)


@app.get("/healthz")
async def healthz() -> dict[str, str]:
    return {"status": "ok", "service": "worldstate"}


@app.get("/readyz")
async def readyz() -> JSONResponse:
    connected = state.nc is not None and state.nc.is_connected
    if not connected:
        return JSONResponse(
            status_code=503,
            content={"status": "unavailable", "dependency": "nats"},
        )
    return JSONResponse(
        status_code=200,
        content={"status": "ready", "dependency": "nats"},
    )
