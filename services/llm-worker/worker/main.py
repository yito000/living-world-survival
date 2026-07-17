"""LLM worker entrypoint（M5 / 08B 3.2・3.4・3.7）。

``ai.decision.request`` を購読し、**実 LLM の構造化出力**（``LLM_MOCK=1`` なら決定的モック）で
型安全な ``ActionDecision`` を生成して ``ai.decision.result.{server_id}`` へ返す。併せて
``worldevent.evaluation.request`` を購読し ``EventProposal`` を発行する（Director / 3.4）。
health は別スレッドの HTTP で返す（make smoke 用）。

R7: LLM 呼び出しは NATS コールバックから起動する専用タスクで行い、health ハンドラは軽量に保つ。

判断履歴は ``ai_decisions`` へ best-effort で記録する（Postgres 未接続でも DS の fallback を
妨げない）。status ∈ {requested(worldstate) / produced / mock / rejected}。
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from typing import Any

from worker.candidates import CandidateRepo, select_candidates, state_summary
from worker.decision import (
    FALLBACK_TEMPLATE,
    build_decision,
    build_mock_decision,
    decision_id_of,
    personal_state_version,
    result_subject,
    stamp,
)
from worker.event_director import EventDirector
from worker.llm import LLMClient, LLMError, use_mock
from worker.schemas import AllowedIdError

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]

try:  # pragma: no cover - import guard
    import asyncpg
except Exception:  # pragma: no cover
    asyncpg = None  # type: ignore[assignment]

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("llm-worker")

DECISION_REQUEST_SUBJECT = "ai.decision.request"

# M4 では判断の組み立てが本モジュールにあった。M5 で worker.decision（3.2 のファイル構成）へ
# 移し、ここでは再エクスポートだけ残す（既存の呼び出し側/テストの import を壊さない）。
__all__ = [
    "DECISION_REQUEST_SUBJECT",
    "FALLBACK_TEMPLATE",
    "DecisionWorker",
    "build_mock_decision",
    "decision_id_of",
    "main",
    "record_decision",
    "result_subject",
    "run",
]


async def record_decision(pool: Any, decision: dict[str, Any], status: str) -> None:
    """判断履歴を ai_decisions へ記録する（Owner: LLM Worker, 3.2）。

    worldstate が先に requested を入れていれば単一行を遷移、無ければ挿入する（decision_id で
    冪等）。status ∈ {produced, mock, rejected}。失敗は握って DS の成立を妨げない。
    """
    if pool is None:
        return
    try:
        await pool.execute(
            """
            INSERT INTO ai_decisions
                   (decision_id, actor_id, state_version, template_id, template_version,
                    status, payload)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb)
            ON CONFLICT (decision_id) DO UPDATE
               SET status = EXCLUDED.status,
                   template_id = EXCLUDED.template_id,
                   template_version = EXCLUDED.template_version,
                   state_version = EXCLUDED.state_version,
                   payload = EXCLUDED.payload
            """,
            decision["decision_id"],
            decision["actor_id"],
            int(decision["state_version"]),
            decision.get("template_id", ""),
            decision.get("template_version"),
            status,
            json.dumps(decision),
        )
    except Exception:  # pragma: no cover - infra dependent
        logger.warning("llm-worker: could not record %s decision", status, exc_info=True)


async def _record_produced(pool: Any, decision: dict[str, Any]) -> None:
    """M4 互換の薄いラッパ（produced 記録）。実体は record_decision。"""
    await record_decision(pool, decision, "produced")


class DecisionWorker:
    """``ai.decision.request`` → ActionDecision → ``ai.decision.result.{server_id}``（3.2）。

    フロー（10.2）: 要求受信 → 投影取得＋候補テンプレをルールで絞る → LLM 構造化出力 →
    Schema / Allowed ID 検証・Token/Cost 記録 → result 発行（DS が最終検証・08A 3.1）。
    """

    def __init__(self, nc: Any, pool: Any, repo: CandidateRepo | None, client: Any, mock: bool):
        self._nc = nc
        self._pool = pool
        self._repo = repo
        self._client = client
        self._mock = mock
        self._sub: Any = None

    async def start(self) -> None:
        async def _cb(msg: Any) -> None:
            await self.on_request(msg.data)

        self._sub = await self._nc.subscribe(DECISION_REQUEST_SUBJECT, cb=_cb)
        logger.info("llm-worker: subscribed %s (mock=%s)", DECISION_REQUEST_SUBJECT, self._mock)

    async def stop(self) -> None:
        if self._sub is not None:
            try:
                await self._sub.unsubscribe()
            except Exception:  # pragma: no cover
                pass
            self._sub = None

    async def on_request(self, data: bytes) -> dict[str, Any] | None:
        """1 判断要求を処理し、発行した Decision（あれば）を返す。テスト用に戻り値を持つ。"""
        try:
            request = json.loads(data)
        except (ValueError, TypeError):
            return None
        if not isinstance(request, dict):
            return None

        decision = await self._produce(request)
        if decision is None:
            return None
        await self._nc.publish(result_subject(request), json.dumps(decision).encode())
        await record_decision(self._pool, decision, "mock" if self._mock else "produced")
        return decision

    async def _produce(self, request: dict[str, Any]) -> dict[str, Any] | None:
        """Decision を作る。作れなければ None（= 発行しない → DS は Utility Fallback）。"""
        if self._mock or self._client is None:
            decision = build_mock_decision(request)
            # 実時刻/lease と template_version を入れてから発行する（DS の 9.4 検証用）。
            version = self._repo.template_version(decision["template_id"]) if self._repo else None
            return stamp(decision, template_version=version)

        actor_id = str(request.get("actor_id", "unknown"))
        reason = str(request.get("reason", ""))
        templates = getattr(self._repo, "templates", []) or []
        candidates = select_candidates(templates, reason)

        projection = None
        if self._repo is not None:
            try:
                projection = await self._repo.load_projection(actor_id)
            except Exception:  # pragma: no cover - infra dependent
                logger.warning(
                    "llm-worker: projection lookup failed for %s", actor_id, exc_info=True
                )

        try:
            output = await self._client.decide(candidates, state_summary(projection, reason))
        except LLMError as exc:
            # タイムアウト/拒否: **結果を発行しない**（AT-014）。DS は現行行動継続 →
            # Utility Fallback。履歴に rejected を残して観測可能にする。
            logger.warning("llm-worker: decision skipped for %s: %s", actor_id, exc)
            await self._record_rejected(request, str(exc))
            return None

        version = self._repo.template_version(output.template_id) if self._repo else None
        try:
            return build_decision(request, output, candidates, template_version=version)
        except AllowedIdError as exc:
            # 許可外 template_id / action_template_id は破棄する（17章 MVP-SEC-008）。
            logger.warning("llm-worker: decision rejected for %s: %s", actor_id, exc)
            await self._record_rejected(request, str(exc))
            return None

    async def _record_rejected(self, request: dict[str, Any], reason: str) -> None:
        """破棄した判断を rejected として残す（3.2 / AT-014 の確認点）。"""
        actor_id = str(request.get("actor_id", "unknown"))
        version = personal_state_version(request)
        await record_decision(
            self._pool,
            {
                "decision_id": decision_id_of(actor_id, version),
                "actor_id": actor_id,
                "state_version": version,
                "template_id": "",
                "template_version": None,
                "rejected_reason": reason,
                "usage": getattr(self._client, "last_usage", {}) or {},
            },
            "rejected",
        )


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
        logger.warning("llm-worker: could not connect Postgres (record disabled)")
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


def _build_client(mock: bool) -> Any | None:
    """実 LLM クライアントを組む。モック時や SDK 不在時は None（モック経路で成立させる）。"""
    if mock:
        return None
    try:
        return LLMClient()
    except LLMError:
        logger.warning("llm-worker: LLM クライアントを作れないためモックへフォールバックします")
        return None


async def run() -> None:
    port = int(os.getenv("LLM_WORKER_PORT", "8084"))
    start_health_server(port)

    url = os.getenv("NATS_URL", "nats://localhost:4222")
    if nats is None:
        # No broker library available; keep the health server alive.
        while True:  # pragma: no cover
            await asyncio.sleep(3600)

    pool = await _build_pool()
    repo: CandidateRepo | None = None
    if pool is not None:
        repo = CandidateRepo(pool)
        try:
            # テンプレ集合は起動時スナップショット（R7: コールバックで DB を舐めない）。
            loaded = await repo.load_templates()
            logger.info("llm-worker: loaded %d active templates", len(loaded))
        except Exception:  # pragma: no cover - infra dependent
            logger.warning(
                "llm-worker: could not load templates; candidates limited", exc_info=True
            )

    mock = use_mock()
    client = _build_client(mock)
    if client is None:
        mock = True
    logger.info(
        "llm-worker: mode=%s model=%s", "mock" if mock else "live", getattr(client, "model", "-")
    )

    nc = await nats.connect(url, max_reconnect_attempts=-1, reconnect_time_wait=2)

    decisions = DecisionWorker(nc, pool, repo, client, mock)
    await decisions.start()
    director = EventDirector(nc, repo, client, mock)
    await director.start()
    logger.info("llm-worker: ready at %s", url)

    stop = asyncio.Event()
    try:
        await stop.wait()  # run until cancelled
    finally:  # pragma: no cover - shutdown path
        await director.stop()
        await decisions.stop()


def main() -> None:
    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
