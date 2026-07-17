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
import time
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
from worker.obs import (
    DECISION_SECONDS,
    DECISIONS,
    ERRORS,
    UP,
    classify_error,
    observe_usage,
    render_metrics,
    setup_logging,
)
from worker.schemas import AllowedIdError

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]

try:  # pragma: no cover - import guard
    import asyncpg
except Exception:  # pragma: no cover
    asyncpg = None  # type: ignore[assignment]

setup_logging("llm-worker")
logger = logging.getLogger("llm-worker")

DECISION_REQUEST_SUBJECT = "ai.decision.request"

# M4 では判断の組み立てが本モジュールにあった。M5 で worker.decision（3.2 のファイル構成）へ
# 移し、ここでは再エクスポートだけ残す（既存の呼び出し側/テストの import を壊さない）。
__all__ = [
    "DECISION_REQUEST_SUBJECT",
    "FALLBACK_TEMPLATE",
    "READINESS",
    "DecisionWorker",
    "Readiness",
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
    # metrics は Postgres の有無に依らず数える（記録は best-effort だが、判断そのものは
    # 起きているため）。status は上の有限集合のみでラベル爆発しない。
    DECISIONS.labels(status=status).inc()
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
        logger.warning(
            "could not record decision",
            extra={"status": status, "actor_id": decision.get("actor_id")},
            exc_info=True,
        )


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
        logger.info("subscribed", extra={"subject": DECISION_REQUEST_SUBJECT, "mock": self._mock})

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
                    "projection lookup failed", extra={"actor_id": actor_id}, exc_info=True
                )

        # 3.1 の「通常 30 秒以内 / Hard timeout 60 秒」を観測する区間。例外時も計測して
        # 「遅くて落ちた」のか「即座に拒否された」のかを区別できるようにする。
        started = time.perf_counter()
        try:
            output = await self._client.decide(candidates, state_summary(projection, reason))
        except LLMError as exc:
            # タイムアウト/拒否: **結果を発行しない**（AT-014）。DS は現行行動継続 →
            # Utility Fallback。履歴に rejected を残して観測可能にする。
            DECISION_SECONDS.observe(time.perf_counter() - started)
            reason_label = classify_error(exc)
            ERRORS.labels(reason=reason_label).inc()
            logger.warning(
                "decision skipped",
                extra={"actor_id": actor_id, "reason": reason_label},
                exc_info=True,
            )
            await self._record_rejected(request, str(exc))
            return None
        DECISION_SECONDS.observe(time.perf_counter() - started)
        observe_usage(getattr(self._client, "last_usage", {}) or {})

        version = self._repo.template_version(output.template_id) if self._repo else None
        try:
            return build_decision(request, output, candidates, template_version=version)
        except AllowedIdError as exc:
            # 許可外 template_id / action_template_id は破棄する（17章 MVP-SEC-008）。
            ERRORS.labels(reason=classify_error(exc)).inc()
            logger.warning(
                "decision rejected",
                extra={
                    "actor_id": actor_id,
                    "template_id": output.template_id,
                    "reason": "allowed_id",
                },
            )
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
        # Postgres は best-effort。落ちていても ready のまま（/readyz は NATS のみを見る）。
        logger.warning("could not connect Postgres (record disabled)")
        return None


class Readiness:
    """/readyz が見る依存の状態（10B 3.5）。

    HTTP ハンドラは asyncio ループとは**別スレッド**で動くので、ここで持つ参照は lock 越しに
    受け渡す。ハンドラからコルーチンを呼んではならない（ループを跨げない）ので、判定は
    ``nc.is_connected`` のような同期的に読める属性だけで行う。

    Postgres は含めない: ``record_decision`` は best-effort であり（3.2）、DB が落ちても
    判断の発行と DS の fallback は成立する。ここで un-ready にすると本来動けるワーカーが
    トラフィックから外れてしまう。
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._nc: Any = None

    def set_nats(self, nc: Any) -> None:
        with self._lock:
            self._nc = nc

    def nats_connected(self) -> bool:
        with self._lock:
            nc = self._nc
        # 未接続（起動途中）と切断中（再接続待ち）を同じ「not ready」として扱う。
        return nc is not None and bool(getattr(nc, "is_connected", False))


READINESS = Readiness()


class _HealthHandler(BaseHTTPRequestHandler):
    def _send_json(self, code: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802 (stdlib naming)
        if self.path == "/healthz":
            # Liveness: プロセスが生きていれば 200。依存は見ない（NATS が瞬断しただけで
            # 再起動されると再接続の機会そのものを奪う）。
            self._send_json(200, {"status": "ok", "service": "llm-worker"})
        elif self.path == "/readyz":
            # Readiness: 仕事の全ては NATS の購読なので、切れていれば ready ではない。
            ready = READINESS.nats_connected()
            UP.set(1 if ready else 0)
            if ready:
                self._send_json(200, {"status": "ok", "service": "llm-worker"})
            else:
                self._send_json(503, {"status": "unavailable", "dependency": "nats"})
        elif self.path == "/metrics":
            body, content_type = render_metrics()
            self.send_response(200)
            self.send_header("Content-Type", content_type)
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
        logger.warning("LLM クライアントを作れないためモックへフォールバックします")
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
            logger.info("loaded active templates", extra={"templates": len(loaded)})
        except Exception:  # pragma: no cover - infra dependent
            logger.warning("could not load templates; candidates limited", exc_info=True)

    mock = use_mock()
    client = _build_client(mock)
    if client is None:
        mock = True
    logger.info(
        "llm mode selected",
        extra={"mode": "mock" if mock else "live", "model": getattr(client, "model", "-")},
    )

    nc = await nats.connect(url, max_reconnect_attempts=-1, reconnect_time_wait=2)
    # ここから /readyz が NATS の実状態（再接続中は False）を見る。
    READINESS.set_nats(nc)

    decisions = DecisionWorker(nc, pool, repo, client, mock)
    await decisions.start()
    director = EventDirector(nc, repo, client, mock)
    await director.start()
    logger.info("ready", extra={"nats_url": url})

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
