"""repo.py の純関数と Store のクエリ組み立てのユニットテスト（実 DB 不要）。"""

from __future__ import annotations

import json
from typing import Any

import pytest

from app.repo import (
    DecisionStore,
    ProjectionStore,
    TemplateRepo,
    _actor_world_of,
    _projection_payload,
    _template_row,
    decision_id_of,
)


def test_decision_id_is_stable() -> None:
    # worldstate（requested）と llm-worker（produced）が同一行を遷移させる決定的 id。
    assert decision_id_of("a1", 7) == "a1:7"


def test_actor_world_prefers_payload_actor_id() -> None:
    env = {"world_id": "w1", "aggregate_id": "agg-1", "payload": {"actor_id": "actor-9"}}
    assert _actor_world_of(env) == ("actor-9", "w1")


def test_actor_world_falls_back_to_aggregate_id() -> None:
    # payload に actor_id が無ければ aggregate_id で投影を進める（契約ギャップの吸収）。
    env = {"world_id": "w1", "aggregate_id": "agg-1", "payload": {"grants": []}}
    assert _actor_world_of(env) == ("agg-1", "w1")


def test_actor_world_missing_returns_empty() -> None:
    assert _actor_world_of({"payload": {}}) == ("", "")


def test_projection_payload_lifts_known_keys() -> None:
    env = {
        "event_id": "ev-1",
        "type": "actor.needs_updated",
        "sequence": 5,
        "payload": {"actor_id": "a1", "personal_state": {"hunger": 40}, "irrelevant": 1},
    }
    proj = _projection_payload(env)
    assert proj["last_event"] == {"event_id": "ev-1", "type": "actor.needs_updated", "sequence": 5}
    assert proj["personal_state"] == {"hunger": 40}
    # 未知フィールドは投影へ引き上げない。
    assert "irrelevant" not in proj


def test_template_row_parses_jsonb_string() -> None:
    row = {
        "template_id": "t1",
        "version": 1,
        "status": "active",
        "tags": ["food"],
        "definition": json.dumps({"template_id": "t1", "steps": ["A"]}),
    }
    got = _template_row(row)
    assert got["definition"]["steps"] == ["A"]
    assert got["tags"] == ["food"]


class _FakePool:
    """asyncpg プールの代役。実行された SQL/引数を記録する。"""

    def __init__(self, fetchrow_result: Any = None, fetch_result: Any = None) -> None:
        self.executed: list[tuple[str, tuple[Any, ...]]] = []
        self._fetchrow_result = fetchrow_result
        self._fetch_result = fetch_result or []

    async def execute(self, sql: str, *args: Any) -> None:
        self.executed.append((sql, args))

    async def fetchrow(self, sql: str, *args: Any) -> Any:
        self.executed.append((sql, args))
        return self._fetchrow_result

    async def fetch(self, sql: str, *args: Any) -> Any:
        self.executed.append((sql, args))
        return self._fetch_result


@pytest.mark.asyncio
async def test_projection_apply_skips_when_actor_missing() -> None:
    pool = _FakePool()
    store = ProjectionStore(pool)
    version = await store.apply({"world_id": "w1", "payload": {}})
    assert version is None
    assert pool.executed == []  # DB へは触れない


@pytest.mark.asyncio
async def test_projection_apply_passes_actor_and_world() -> None:
    pool = _FakePool(fetchrow_result={"projection_version": 3})
    store = ProjectionStore(pool)
    env = {"world_id": "w1", "type": "actor.x", "payload": {"actor_id": "a1"}}
    version = await store.apply(env)
    assert version == 3
    sql, args = pool.executed[0]
    assert "actor_state_projections" in sql
    assert args[0] == "a1"
    assert args[1] == "w1"


@pytest.mark.asyncio
async def test_record_requested_uses_requested_status() -> None:
    pool = _FakePool()
    store = DecisionStore(pool)
    await store.record_requested("a1:4", "a1", 4, ["safety.idle_at_camp"])
    sql, args = pool.executed[0]
    assert "'requested'" in sql
    assert args[0] == "a1:4"
    assert args[1] == "a1"
    assert args[2] == 4


@pytest.mark.asyncio
async def test_list_active_filters_active() -> None:
    pool = _FakePool(
        fetch_result=[
            {
                "template_id": "t1",
                "version": 1,
                "status": "active",
                "tags": ["food"],
                "definition": {"template_id": "t1"},
            }
        ]
    )
    repo = TemplateRepo(pool)
    items = await repo.list_active()
    assert len(items) == 1
    sql, args = pool.executed[0]
    assert "status = 'active'" in sql
    # M5: World Event Template は同居しているが Action Template ではないので配信しない
    # （DS は配信結果を行動テンプレとして解釈する, 3.8）。
    assert "NOT (tags @> ARRAY[$1]::text[])" in sql
    assert args == ("world_event",)
