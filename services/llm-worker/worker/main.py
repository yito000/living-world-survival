"""LLM worker entrypoint（M4 / 07B 3.4）。

``ai.decision.request`` を購読し、**モック** ``ActionDecision`` を
``ai.decision.result.{server_id}`` へ返す（LLM 本体は M5）。生成した判断は
``ai_decisions``（status=produced）へ記録する（Postgres 未接続でも DS の fallback を
妨げないよう best-effort）。health は別スレッドの HTTP で返す（make smoke 用）。

重い/モデル処理はメッセージコールバック経路に置かない（BSD 4.3 / R7）。M4 の mock は
reason→テンプレの単純ルールのみで軽量。
"""

from __future__ import annotations

import asyncio
import json
import os
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from typing import Any

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]

try:  # pragma: no cover - import guard
    import asyncpg
except Exception:  # pragma: no cover
    asyncpg = None  # type: ignore[assignment]

DECISION_REQUEST_SUBJECT = "ai.decision.request"

# reason（再判断トリガ / urgency タグ）→ 選ぶ 1 テンプレ。フォールバックは安全待機。
# worldstate の候補絞り込み（app/candidates.py）と整合する主タグ対応（3.6）。
FALLBACK_TEMPLATE = "safety.idle_at_camp"
REASON_TEMPLATE: tuple[tuple[str, str], ...] = (
    ("hunger", "survival.eat_owned_food"),
    ("food", "survival.eat_owned_food"),
    ("cleanup", "cleaning.clean_nearby"),
    ("clean", "cleaning.clean_nearby"),
    ("earn", "mining.acquire_iron"),
    ("iron", "mining.acquire_iron"),
    ("sell", "economy.sell_surplus"),
    ("overflow", "economy.sell_surplus"),
    ("event", "worldevent.join"),
)


def decision_id_of(actor_id: str, state_version: int) -> str:
    """worldstate（requested）と一致する決定的 decision_id（単一行遷移用, 3.4）。"""
    return f"{actor_id}:{state_version}"


def _personal_state_version(request: dict[str, Any]) -> int:
    versions = request.get("state_versions")
    if isinstance(versions, dict) and versions:
        if "personal_state" in versions:
            return int(versions["personal_state"])
        return int(next(iter(versions.values())))
    return int(request.get("state_version", 0) or 0)


def _select_template(reason: str) -> str:
    reason = (reason or "").strip().lower()
    for key, template_id in REASON_TEMPLATE:
        if key in reason:
            return template_id
    return FALLBACK_TEMPLATE


def build_mock_decision(request: dict[str, Any]) -> dict[str, Any]:
    """判断要求に対する決定的なモック ActionDecision を返す（proto ActionDecision 形）。

    純関数（I/O 無し）でユニットテスト可能。reason に対応するテンプレを 1 件選ぶ。
    created_at_unix_ms は publish 時に付与する（ここでは決定的に保つため 0）。
    """
    actor_id = str(request.get("actor_id", "unknown"))
    state_version = _personal_state_version(request)
    template_id = _select_template(str(request.get("reason", "")))
    return {
        "decision_id": decision_id_of(actor_id, state_version),
        "actor_id": actor_id,
        "state_version": state_version,
        "template_id": template_id,
        "steps": [{"action_template_id": template_id, "params": {}}],
        "created_at_unix_ms": 0,
        "mock": True,
    }


def _result_subject(request: dict[str, Any]) -> str:
    """result subject に埋める server_id を request コンテキストから解決する（3.4）。"""
    server_id = request.get("server_id") or request.get("world_id") or "unknown"
    return f"ai.decision.result.{server_id}"


async def _record_produced(pool: Any, decision: dict[str, Any]) -> None:
    """生成した判断を ai_decisions（status=produced）へ記録する（Owner: LLM Worker, 3.4）。

    worldstate が先に requested を入れていれば単一行を produced へ遷移、無ければ produced で
    挿入する。冪等（同一 decision_id は上書き）。失敗は握って DS の成立を妨げない。
    """
    if pool is None:
        return
    try:
        await pool.execute(
            """
            INSERT INTO ai_decisions
                   (decision_id, actor_id, state_version, template_id, status, payload)
            VALUES ($1, $2, $3, $4, 'produced', $5::jsonb)
            ON CONFLICT (decision_id) DO UPDATE
               SET status = 'produced',
                   template_id = EXCLUDED.template_id,
                   state_version = EXCLUDED.state_version,
                   payload = EXCLUDED.payload
            """,
            decision["decision_id"],
            decision["actor_id"],
            int(decision["state_version"]),
            decision["template_id"],
            json.dumps(decision),
        )
    except Exception:  # pragma: no cover - infra dependent
        print("llm-worker: could not record produced decision", flush=True)  # noqa: T201


async def _build_pool() -> Any | None:
    """DATABASE_URL から asyncpg プールを張る。無ければ None（記録は best-effort）。"""
    if asyncpg is None:
        return None
    url = os.getenv("DATABASE_URL")
    if not url:
        return None
    try:
        return await asyncpg.create_pool(dsn=url, min_size=1, max_size=4)
    except Exception:  # pragma: no cover - infra dependent
        print("llm-worker: could not connect Postgres (record disabled)", flush=True)  # noqa: T201
        return None


class _HealthHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 (stdlib naming)
        if self.path in ("/healthz", "/readyz"):
            body = json.dumps({"status": "ok", "service": "llm-worker"}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, *_args: Any) -> None:  # silence per-request logging
        return


def start_health_server(port: int) -> HTTPServer:
    server = HTTPServer(("0.0.0.0", port), _HealthHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server


async def run() -> None:
    port = int(os.getenv("LLM_WORKER_PORT", "8084"))
    start_health_server(port)

    url = os.getenv("NATS_URL", "nats://localhost:4222")
    if nats is None:
        # No broker library available; keep the health server alive.
        while True:  # pragma: no cover
            await asyncio.sleep(3600)

    pool = await _build_pool()
    nc = await nats.connect(url, max_reconnect_attempts=-1, reconnect_time_wait=2)

    async def on_request(msg: Any) -> None:
        try:
            request = json.loads(msg.data.decode())
        except json.JSONDecodeError:
            return
        if not isinstance(request, dict):
            return
        decision = build_mock_decision(request)
        await nc.publish(_result_subject(request), json.dumps(decision).encode())
        await _record_produced(pool, decision)

    await nc.subscribe(DECISION_REQUEST_SUBJECT, cb=on_request)
    print(f"llm-worker: subscribed to {DECISION_REQUEST_SUBJECT} at {url}")  # noqa: T201

    stop = asyncio.Event()
    await stop.wait()  # run until cancelled


def main() -> None:
    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
