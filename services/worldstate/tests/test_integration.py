"""worldstate の統合テスト（実 PostgreSQL / 実 NATS）。

Go 側（integration_test.go / m3_nats_test.go）と同じ流儀で、インフラが未接続なら
self-skip して `make ci`（pytest）がインフラ無しでも緑のままになるようにする。実行するには
先に `make up migrate`（もしくは `make smoke`）で postgres/nats を起動しておく。

M4 DoD の統合項目を実接続で検証する（07B 5章 Integration）:
- Actor イベント適用で actor_state_projections が単調増加し、同一 event_id は二重適用されない。
- action_templates（status=active）が配信集合として取得できる。
- ai.decision.request の候補絞り込みと ai_decisions(requested) 記録。
"""

from __future__ import annotations

import asyncio
import json
import os
import uuid
from typing import Any

import pytest

from app.consumer import (
    DecisionRequestConsumer,
    PgDedup,
    handle_message,
)
from app.repo import DecisionStore, ProjectionStore, TemplateRepo, create_pool


def _pg_url() -> str:
    return os.getenv("TEST_DATABASE_URL") or os.getenv(
        "DATABASE_URL", "postgres://survival:survival@localhost:5432/survival?sslmode=disable"
    )


async def _pool_or_skip() -> Any:
    """asyncpg プールを張る。接続できなければ self-skip（インフラ無しで緑を保つ）。"""
    os.environ.setdefault("DATABASE_URL", _pg_url())
    # インフラ未接続時に SYN ドロップ等で長時間ブロックしないよう、短いタイムアウトで打ち切る。
    try:
        pool = await asyncio.wait_for(create_pool(), timeout=5)
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


def _actor_envelope(event_id: str, actor_id: str, world_id: str, seq: int, hunger: int) -> bytes:
    return json.dumps(
        {
            "event_id": event_id,
            "world_id": world_id,
            "aggregate_id": actor_id,
            "type": "actor.needs_updated",
            "sequence": seq,
            "payload": {"actor_id": actor_id, "personal_state": {"hunger": hunger}},
        }
    ).encode()


@pytest.mark.asyncio
async def test_projection_monotonic_and_dedup() -> None:
    pool = await _pool_or_skip()
    consumer_id = f"test-{uuid.uuid4().hex[:8]}"
    actor_id = f"it-actor-{uuid.uuid4().hex[:8]}"
    world_id = f"it-world-{uuid.uuid4().hex[:8]}"
    ev1, ev2 = str(uuid.uuid4()), str(uuid.uuid4())
    dedup = PgDedup(pool)
    projection = ProjectionStore(pool)
    try:
        # 1発目=新規投影(v=0)、2発目=新規投影(v=1)、2発目再送=dedup で二重適用なし(v据置)。
        first = await handle_message(
            _actor_envelope(ev1, actor_id, world_id, 1, 41),
            dedup,
            consumer_id,
            projection=projection,
            category="actor",
        )
        second = await handle_message(
            _actor_envelope(ev2, actor_id, world_id, 2, 42),
            dedup,
            consumer_id,
            projection=projection,
            category="actor",
        )
        dup = await handle_message(
            _actor_envelope(ev2, actor_id, world_id, 2, 99),
            dedup,
            consumer_id,
            projection=projection,
            category="actor",
        )
        assert first is True
        assert second is True
        assert dup is False  # 重複は弾かれる

        row = await pool.fetchrow(
            "SELECT projection_version, payload FROM actor_state_projections WHERE actor_id=$1",
            actor_id,
        )
        assert row is not None
        # 2 イベント適用で 0→1 の単調増加。重複は版を進めない。
        assert row["projection_version"] == 1
        payload = row["payload"]
        payload = json.loads(payload) if isinstance(payload, str) else payload
        # 最後に適用された ev2(hunger=42) が反映され、重複の hunger=99 は入らない。
        assert payload["personal_state"]["hunger"] == 42
        assert payload["last_event"]["event_id"] == ev2
    finally:
        await pool.execute("DELETE FROM actor_state_projections WHERE actor_id=$1", actor_id)
        await pool.execute("DELETE FROM inbox_dedup WHERE consumer_id=$1", consumer_id)
        await pool.close()


@pytest.mark.asyncio
async def test_template_delivery_active() -> None:
    pool = await _pool_or_skip()
    try:
        items = await TemplateRepo(pool).list_active()
        # 9.3 の 13 テンプレが seed され、配信されるのは status=active のみ。
        assert len(items) >= 13
        ids = {t["template_id"] for t in items}
        assert "safety.idle_at_camp" in ids
        assert "survival.eat_owned_food" in ids
        # M5: World Event Template（10.3）は同じテーブルに同居するが、Action Template では
        # ないので配信しない（DS はこれを行動テンプレとして解釈するため, 3.8）。
        for world_event_id in (
            "world_event.great_hunt",
            "world_event.rare_resource",
            "world_event.rare_buyer_rush",
        ):
            assert world_event_id not in ids
        for t in items:
            assert t["status"] == "active"
            # definition JSONB は基本 7.3 スキーマ（steps を持つ）。
            assert "steps" in t["definition"]
    finally:
        await pool.close()


@pytest.mark.asyncio
async def test_decision_request_records_requested() -> None:
    pool = await _pool_or_skip()
    actor_id = f"it-actor-{uuid.uuid4().hex[:8]}"
    version = 4
    decision_id = f"{actor_id}:{version}"
    consumer = DecisionRequestConsumer(None, TemplateRepo(pool), DecisionStore(pool))
    try:
        # 起動時スナップショット（実テンプレ集合）を読み込む。NATS 無しで _on_request を直接叩く
        # ので、running コンテナと干渉しない（decision_id はユニーク）。
        consumer._active = await TemplateRepo(pool).list_active()
        request = {
            "actor_id": actor_id,
            "world_id": "it-world",
            "state_versions": {"personal_state": version},
            "reason": "hunger_high",
        }
        await consumer._on_request(json.dumps(request).encode())

        row = await pool.fetchrow(
            "SELECT status, payload FROM ai_decisions WHERE decision_id=$1", decision_id
        )
        assert row is not None
        assert row["status"] == "requested"
        payload = row["payload"]
        payload = json.loads(payload) if isinstance(payload, str) else payload
        candidates = payload.get("candidates", [])
        # hunger_high の候補に food テンプレ + 終端フォールバックが含まれる。
        assert "survival.eat_owned_food" in candidates
        assert candidates[-1] == "safety.idle_at_camp"
    finally:
        await pool.execute("DELETE FROM ai_decisions WHERE decision_id=$1", decision_id)
        await pool.close()
