"""WorldState FastAPI application（M4 / 07B 3.2-3.4）。

liveness (/healthz) / readiness (/readyz) に加え、DS 起動時取得用のテンプレ配信
(GET /internal/action_templates) を公開する。起動時に NATS/Postgres へ接続し、Actor
イベント投影と ai.decision.request 購読を開始する。R7 に従い request handler に重い処理は
置かない（投影/判断購読は独立の購読タスクで処理する）。
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
from collections.abc import AsyncIterator
from typing import Any

from fastapi import FastAPI, Query
from fastapi.responses import JSONResponse

from app.consumer import (
    Consumer,
    DecisionRequestConsumer,
    InMemoryDedup,
    PgDedup,
)
from app.repo import DecisionStore, ProjectionStore, TemplateRepo, create_pool

# 購読の受信/投影ログ（INFO）が既定の WARNING で消えないよう明示的に設定する。
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("worldstate")

# nats-py is optional at import time so that `python -c "import app.main"` works
# even before dependencies for the broker are installed/available.
try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]


class _State:
    """Holds the live NATS connection, the Postgres pool and the consumers (if any)."""

    nc: Any | None = None
    pool: Any | None = None
    consumer: Any | None = None
    decision_consumer: Any | None = None
    templates: TemplateRepo | None = None


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


async def _start_consumers() -> None:
    """NATS 接続後に投影購読と判断要求購読を起動する（3.3/3.4）。dedup は Postgres、
    無ければ in-memory へフォールバックする。インフラ未起動でも lifespan は失敗させない。"""
    if state.nc is None:
        return
    try:
        pool = state.pool
        dedup = PgDedup(pool) if pool is not None else InMemoryDedup()
        logger.info("worldstate: dedup backend = %s", type(dedup).__name__)

        projection = ProjectionStore(pool) if pool is not None else None
        consumer = Consumer(state.nc, dedup, projection=projection)
        await consumer.start()
        state.consumer = consumer

        if pool is not None:
            templates = state.templates or TemplateRepo(pool)
            decisions = DecisionStore(pool)
            decision_consumer = DecisionRequestConsumer(state.nc, templates, decisions)
            await decision_consumer.start()
            state.decision_consumer = decision_consumer
        else:
            logger.info("worldstate: decision request consumer disabled (no Postgres)")
    except Exception:
        logger.exception("worldstate: consumers failed to start (health stays up)")


@contextlib.asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    state.pool = await create_pool()
    if state.pool is not None:
        state.templates = TemplateRepo(state.pool)
        logger.info("worldstate: Postgres pool ready")
    await _connect_nats()
    await _start_consumers()
    try:
        yield
    finally:
        if state.decision_consumer is not None:
            with contextlib.suppress(Exception):
                await state.decision_consumer.stop()
        if state.consumer is not None:
            with contextlib.suppress(Exception):
                await state.consumer.stop()
        if state.nc is not None:
            with contextlib.suppress(Exception):
                await state.nc.drain()
        if state.pool is not None:
            with contextlib.suppress(Exception):
                await state.pool.close()


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


@app.get("/internal/action_templates")
async def action_templates(status: str = Query(default="active")) -> JSONResponse:
    """DS 起動時取得用のテンプレ配信（3.2）。M4 は status=active のみ配信する。

    Utility Fallback 用にも同じ集合を供給する（07A のタグ→テンプレ解決に使う）。DB 未接続なら
    503（DS はキャッシュ/フォールバックで成立するため health は 200 のまま）。
    """
    if status != "active":
        # 配信するのは status=active のみ（retired/draft を混ぜない, 落とし穴8）。
        return JSONResponse(status_code=400, content={"error": "only status=active is served"})
    if state.templates is None:
        return JSONResponse(status_code=503, content={"error": "database unavailable"})
    try:
        items = await state.templates.list_active()
    except Exception:
        logger.exception("worldstate: list_active failed")
        return JSONResponse(status_code=503, content={"error": "database error"})
    return JSONResponse(status_code=200, content={"templates": items, "count": len(items)})
