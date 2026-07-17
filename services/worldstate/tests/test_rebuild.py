"""replay 再構築のユニットテスト（08B 3.9 Unit(worldstate) / L-4）。

DoD の「イベントを正として**再構築可能**であること」を、実 NATS なしで担保する:
replay（rebuild.apply_events）の結果が逐次適用（consumer.handle_message）の結果と一致する。
"""

from __future__ import annotations

import json
from typing import Any

import pytest

from app.consumer import InMemoryDedup, handle_message
from app.rebuild import apply_events, subject_for


class _FakeProjection:
    """ProjectionStore の代役。actor ごとの現在値と projection_version を持つ。

    本物と同じく「payload をマージし、版は適用ごとに +1（新規は 0 起点）」で振る舞う。
    """

    def __init__(self) -> None:
        self.state: dict[str, dict[str, Any]] = {}
        self.versions: dict[str, int] = {}

    async def apply(
        self, envelope: dict[str, Any], allow_aggregate_fallback: bool = True
    ) -> int | None:
        payload = envelope.get("payload") or {}
        actor_id = str(payload.get("actor_id") or "")
        if not actor_id and allow_aggregate_fallback:
            actor_id = str(envelope.get("aggregate_id") or "")
        if not actor_id or not envelope.get("world_id"):
            return None
        proj = self.state.setdefault(actor_id, {})
        for key in ("personal_state", "inventory_summary", "active_template", "wallet"):
            if key in payload:
                proj[key] = payload[key]
        proj["last_event"] = {"event_id": envelope.get("event_id"), "type": envelope.get("type")}
        version = self.versions.get(actor_id)
        self.versions[actor_id] = 0 if version is None else version + 1
        return self.versions[actor_id]

    async def reset(self, world_id: str | None = None) -> int:
        n = len(self.state)
        self.state.clear()
        self.versions.clear()
        return n


def _event(
    event_id: str, category: str, actor_id: str | None, payload: dict[str, Any], seq: int
) -> tuple[str, dict[str, Any]]:
    body = dict(payload)
    if actor_id:
        body["actor_id"] = actor_id
    return (
        f"world.w1.event.{category}",
        {
            "event_id": event_id,
            "world_id": "w1",
            "type": f"{category}.changed",
            "aggregate_id": actor_id or "agg-1",
            "sequence": seq,
            "payload": body,
        },
    )


# 逐次適用と replay の両方に流す、決定的なイベント履歴。
HISTORY = [
    _event("ev-1", "actor", "a1", {"personal_state": {"hunger": 10}}, 1),
    _event("ev-2", "resource", None, {"grants": [{"item_definition_id": "stone"}]}, 2),
    _event("ev-3", "actor", "a1", {"personal_state": {"hunger": 55}}, 3),
    _event("ev-4", "actor", "a2", {"inventory_summary": {"free_slots": 2}}, 4),
    _event("ev-5", "economy", "a1", {"wallet": {"cash": 120}}, 5),
]


def test_subject_for_scopes_to_world_or_all() -> None:
    assert subject_for("w1") == "world.w1.event.*"
    assert subject_for(None) == "world.*.event.*"


@pytest.mark.asyncio
async def test_replay_matches_incremental_application() -> None:
    # 逐次適用（常駐 Consumer 経路）。
    live = _FakeProjection()
    dedup = InMemoryDedup()
    for subject, envelope in HISTORY:
        await handle_message(
            json.dumps(envelope).encode(),
            dedup,
            projection=live,
            category=subject.rsplit(".", 1)[-1],
        )

    # replay（rebuild 経路）。
    replayed = _FakeProjection()
    applied = await apply_events(list(HISTORY), replayed)

    # 投影の中身（現在値）は完全一致する。
    assert replayed.state == live.state
    # 版もリプレイ順で同じ採番になる（履歴ではなく現在値なので、逐次と同じ最終版）。
    assert replayed.versions == live.versions
    # actor 3 件 + economy 1 件が適用され、resource は投影されない。
    assert applied == 4


@pytest.mark.asyncio
async def test_replay_skips_resource_events() -> None:
    proj = _FakeProjection()
    applied = await apply_events([HISTORY[1]], proj)
    assert applied == 0
    assert proj.state == {}


@pytest.mark.asyncio
async def test_replay_is_repeatable_after_reset() -> None:
    # 再構築は何度でも実行できる（捨てて replay すれば同じ状態に戻る）。
    proj = _FakeProjection()
    await apply_events(list(HISTORY), proj)
    first = {k: dict(v) for k, v in proj.state.items()}
    first_versions = dict(proj.versions)

    await proj.reset("w1")
    await apply_events(list(HISTORY), proj)

    assert proj.state == first
    assert proj.versions == first_versions


@pytest.mark.asyncio
async def test_replay_ignores_economy_without_actor_id() -> None:
    # economy は payload.actor_id が明示された場合のみ投影する（aggregate_id へ落とさない）。
    subject, envelope = _event("ev-x", "economy", None, {"stock": 3}, 9)
    proj = _FakeProjection()
    assert await apply_events([(subject, envelope)], proj) == 0
    assert proj.state == {}
