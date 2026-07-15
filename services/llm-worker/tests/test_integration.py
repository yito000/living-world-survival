"""llm-worker の統合テスト（実 PostgreSQL / 実 NATS）。

Go 側と同じ流儀で、インフラが未接続なら self-skip して `make ci`（pytest）がインフラ無しでも
緑のままになるようにする。実行するには先に `make up migrate`（PG）や `make smoke`（llm-worker
コンテナ）を起動しておく。

M4 DoD の統合項目を実接続で検証する（07B 5章 Integration）:
- ai_decisions が requested→produced へ単一行遷移する（best-effort 記録の実 SQL）。
- ai.decision.request→result の実 NATS 往復（llm-worker コンテナがモック応答を返す）。
"""

from __future__ import annotations

import asyncio
import json
import os
import uuid
from typing import Any

import pytest

from worker.main import _build_pool, _record_produced, build_mock_decision

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]


async def _pool_or_skip() -> Any:
    os.environ.setdefault(
        "DATABASE_URL",
        os.getenv("TEST_DATABASE_URL")
        or os.getenv(
            "DATABASE_URL", "postgres://survival:survival@localhost:5432/survival?sslmode=disable"
        ),
    )
    # インフラ未接続時に SYN ドロップ等で長時間ブロックしないよう、短いタイムアウトで打ち切る。
    try:
        pool = await asyncio.wait_for(_build_pool(), timeout=5)
    except Exception:  # pragma: no cover - infra dependent (includes TimeoutError)
        pool = None
    if pool is None:
        pytest.skip("PostgreSQL 未接続（make up migrate で起動すると統合テストが走る）")
    try:
        await asyncio.wait_for(pool.execute("SELECT 1"), timeout=5)
    except Exception:  # pragma: no cover - infra dependent (includes TimeoutError)
        await pool.close()
        pytest.skip("PostgreSQL に到達できない")
    return pool


@pytest.mark.asyncio
async def test_produced_transitions_requested_row() -> None:
    pool = await _pool_or_skip()
    actor_id = f"it-actor-{uuid.uuid4().hex[:8]}"
    version = 7
    decision_id = f"{actor_id}:{version}"
    try:
        # worldstate が先に requested を入れた状態を作る。
        await pool.execute(
            """
            INSERT INTO ai_decisions (decision_id, actor_id, state_version, status, payload)
            VALUES ($1, $2, $3, 'requested', $4::jsonb)
            """,
            decision_id,
            actor_id,
            version,
            json.dumps({"candidates": ["safety.idle_at_camp"]}),
        )
        decision = build_mock_decision(
            {"actor_id": actor_id, "state_versions": {"personal_state": version}, "reason": "earn"}
        )
        await _record_produced(pool, decision)

        row = await pool.fetchrow(
            "SELECT status, template_id FROM ai_decisions WHERE decision_id=$1", decision_id
        )
        assert row is not None
        # 単一行が produced へ遷移し、template_id が入る（earn→mining.acquire_iron）。
        assert row["status"] == "produced"
        assert row["template_id"] == "mining.acquire_iron"
    finally:
        await pool.execute("DELETE FROM ai_decisions WHERE decision_id=$1", decision_id)
        await pool.close()


@pytest.mark.asyncio
async def test_produced_inserts_without_prior_requested() -> None:
    pool = await _pool_or_skip()
    actor_id = f"it-actor-{uuid.uuid4().hex[:8]}"
    decision_id = f"{actor_id}:0"
    try:
        # worldstate が未起動でも produced 単独で挿入できる（DS fallback を妨げない骨格）。
        decision = build_mock_decision({"actor_id": actor_id})
        await _record_produced(pool, decision)
        row = await pool.fetchrow(
            "SELECT status FROM ai_decisions WHERE decision_id=$1", decision_id
        )
        assert row is not None
        assert row["status"] == "produced"
    finally:
        await pool.execute("DELETE FROM ai_decisions WHERE decision_id=$1", decision_id)
        await pool.close()


@pytest.mark.asyncio
async def test_decision_roundtrip_via_nats() -> None:
    if nats is None:
        pytest.skip("nats-py 未導入")
    url = os.getenv("TEST_NATS_URL") or os.getenv("NATS_URL", "nats://localhost:4222")
    try:
        nc = await asyncio.wait_for(
            nats.connect(url, connect_timeout=2, max_reconnect_attempts=0), timeout=5
        )
    except Exception:
        pytest.skip("NATS 未接続（make up で起動すると統合テストが走る）")

    # ユニークな server_id で result subject を分け、running llm-worker の応答だけを受ける
    # （fan-out 安全・他テストや常駐サービスと干渉しない）。
    server_id = f"it-{uuid.uuid4().hex[:8]}"
    actor_id = f"it-actor-{uuid.uuid4().hex[:8]}"
    got: list[dict[str, Any]] = []

    async def on_result(msg: Any) -> None:
        got.append(json.loads(msg.data.decode()))

    try:
        await nc.subscribe(f"ai.decision.result.{server_id}", cb=on_result)
        request = {
            "actor_id": actor_id,
            "world_id": "it-world",
            "server_id": server_id,
            "state_versions": {"personal_state": 3},
            "reason": "cleanup",
        }
        await nc.publish("ai.decision.request", json.dumps(request).encode())
        await nc.flush()

        # llm-worker コンテナ稼働時はモック result が返る。未稼働なら self-skip。
        for _ in range(20):
            if got:
                break
            await asyncio.sleep(0.1)
        if not got:
            pytest.skip("llm-worker が応答しない（make smoke でコンテナ起動時に検証される）")

        decision = got[0]
        assert decision["actor_id"] == actor_id
        assert decision["decision_id"] == f"{actor_id}:3"
        # cleanup reason → cleaning.clean_nearby のモック選択。
        assert decision["template_id"] == "cleaning.clean_nearby"
    finally:
        await nc.drain()
