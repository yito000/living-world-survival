"""WorldState FastAPI application (M0 skeleton).

Exposes liveness (/healthz) and readiness (/readyz) endpoints. On startup it
connects to NATS in the background; connectivity is reflected in /readyz.

Per BSD 4.3 / R7, no heavy work is placed in the request handler path.
"""

from __future__ import annotations

import asyncio
import contextlib
import os
from collections.abc import AsyncIterator
from typing import Any

from fastapi import FastAPI
from fastapi.responses import JSONResponse

# nats-py is optional at import time so that `python -c "import app.main"` works
# even before dependencies for the broker are installed/available.
try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]


class _State:
    """Holds the live NATS connection (if any)."""

    nc: Any | None = None


state = _State()


async def _connect_nats() -> None:
    url = os.getenv("NATS_URL", "nats://localhost:4222")
    if nats is None:
        return
    # 起動時に NATS が未起動でも lifespan を無限ブロックしないよう、初回接続は
    # 失敗即時（retry_on_failed_connect=False）+ 全体タイムアウトで打ち切る。
    # 一度接続できれば、以降の切断は max_reconnect_attempts=-1 で自動再接続する。
    with contextlib.suppress(Exception):
        state.nc = await asyncio.wait_for(
            nats.connect(
                url,
                max_reconnect_attempts=-1,
                reconnect_time_wait=2,
                connect_timeout=2,
                retry_on_failed_connect=False,
            ),
            timeout=6,
        )


@contextlib.asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    await _connect_nats()
    try:
        yield
    finally:
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
