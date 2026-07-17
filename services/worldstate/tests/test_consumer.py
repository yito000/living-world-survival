"""購読ロジックのユニットテスト（実 NATS / DB 不要）。"""

from __future__ import annotations

import json
from typing import Any

import pytest

from app.consumer import (
    CONSUMER_ID,
    SUBJECTS,
    InMemoryDedup,
    build_requested,
    category_of,
    handle_message,
    message_id_of,
)


def _envelope(
    event_id: str,
    event_type: str = "resource.mined",
    world_id: str = "w1",
    payload: dict[str, Any] | None = None,
) -> bytes:
    return json.dumps(
        {
            "event_id": event_id,
            "world_id": world_id,
            "type": event_type,
            "sequence": 1,
            "payload": payload or {"grants": [{"item_definition_id": "stone", "quantity": 1}]},
        }
    ).encode()


class _FakeProjection:
    """ProjectionStore の代役。apply 呼び出しを記録し、擬似 projection_version を返す。"""

    def __init__(self) -> None:
        self.calls: list[dict[str, Any]] = []
        self._versions: dict[str, int] = {}

    async def apply(
        self, envelope: dict[str, Any], allow_aggregate_fallback: bool = True
    ) -> int | None:
        self.calls.append(envelope)
        payload = envelope.get("payload") or {}
        actor_id = str(payload.get("actor_id") or "")
        if not actor_id and allow_aggregate_fallback:
            actor_id = str(envelope.get("aggregate_id") or "")
        if not actor_id or not envelope.get("world_id"):
            return None
        version = self._versions.get(actor_id)
        version = 0 if version is None else version + 1
        self._versions[actor_id] = version
        return version


@pytest.mark.asyncio
async def test_first_delivery_is_accepted() -> None:
    dedup = InMemoryDedup()
    accepted = await handle_message(_envelope("ev-1"), dedup)
    assert accepted is True


@pytest.mark.asyncio
async def test_redelivery_is_deduped() -> None:
    # At-least-once: the same event_id delivered twice is processed once (R9).
    dedup = InMemoryDedup()
    first = await handle_message(_envelope("ev-1"), dedup)
    second = await handle_message(_envelope("ev-1"), dedup)
    assert first is True
    assert second is False


def test_message_id_prefers_event_id() -> None:
    assert message_id_of({"event_id": "ulid-1", "world_id": "w", "sequence": 3}) == "ulid-1"
    assert message_id_of({"world_id": "w", "sequence": 3}) == "w:3"


def test_category_of_extracts_tail() -> None:
    assert category_of("world.abc.event.actor") == "actor"
    assert category_of("world.abc.event.resource") == "resource"
    assert category_of("") == ""


@pytest.mark.asyncio
async def test_actor_event_is_projected() -> None:
    # 新規かつ actor カテゴリなら projection.apply が呼ばれ、版が返る。
    dedup = InMemoryDedup()
    proj = _FakeProjection()
    env = _envelope("ev-a", "actor.needs_updated", payload={"actor_id": "actor-1"})
    accepted = await handle_message(env, dedup, projection=proj, category="actor")
    assert accepted is True
    assert len(proj.calls) == 1


@pytest.mark.asyncio
async def test_duplicate_actor_event_is_not_reprojected() -> None:
    # 二重適用なし: 同一 event_id は dedup で弾かれ projection は 1 回だけ。
    dedup = InMemoryDedup()
    proj = _FakeProjection()
    env = _envelope("ev-a", "actor.needs_updated", payload={"actor_id": "actor-1"})
    await handle_message(env, dedup, projection=proj, category="actor")
    await handle_message(env, dedup, projection=proj, category="actor")
    assert len(proj.calls) == 1


@pytest.mark.asyncio
async def test_resource_event_is_not_projected() -> None:
    # resource カテゴリは投影対象外（dedup+ログのみ）。
    dedup = InMemoryDedup()
    proj = _FakeProjection()
    await handle_message(_envelope("ev-r"), dedup, projection=proj, category="resource")
    assert proj.calls == []


def test_subjects_cover_actor_resource_economy() -> None:
    # M5: economy を追加して 3 系統を購読する（14.3 / 3.1）。
    assert SUBJECTS == (
        "world.*.event.resource",
        "world.*.event.actor",
        "world.*.event.economy",
    )
    assert CONSUMER_ID == "worldstate-projection"


@pytest.mark.asyncio
async def test_economy_event_is_projected_when_actor_is_explicit() -> None:
    dedup = InMemoryDedup()
    proj = _FakeProjection()
    env = _envelope(
        "ev-e", "economy.purchased", payload={"actor_id": "actor-1", "wallet": {"cash": 5}}
    )
    accepted = await handle_message(env, dedup, projection=proj, category="economy")
    assert accepted is True
    assert len(proj.calls) == 1


@pytest.mark.asyncio
async def test_economy_event_without_actor_id_is_not_projected() -> None:
    # economy の aggregate_id は Buyer 等 Actor でない集約を指し得るので、
    # actor_id が payload に無ければ投影しない（actor へフォールバックさせない）。
    dedup = InMemoryDedup()
    proj = _FakeProjection()
    env = json.dumps(
        {
            "event_id": "ev-b",
            "world_id": "w1",
            "type": "economy.buyer_spawned",
            "aggregate_id": "buyer-7",
            "sequence": 1,
            "payload": {"stock": 3},
        }
    ).encode()
    accepted = await handle_message(env, dedup, projection=proj, category="economy")
    # dedup 上は新規として受理するが、投影は起こさない（版が返らない）。
    assert accepted is True
    assert proj.calls[0]["aggregate_id"] == "buyer-7"
    assert proj._versions == {}


@pytest.mark.asyncio
async def test_actor_event_without_actor_id_falls_back_to_aggregate_id() -> None:
    # actor カテゴリは契約ギャップ（payload の actor_id 欠落）を aggregate_id で吸収する。
    dedup = InMemoryDedup()
    proj = _FakeProjection()
    env = json.dumps(
        {
            "event_id": "ev-c",
            "world_id": "w1",
            "type": "actor.moved",
            "aggregate_id": "actor-9",
            "sequence": 1,
            "payload": {},
        }
    ).encode()
    await handle_message(env, dedup, projection=proj, category="actor")
    assert proj._versions == {"actor-9": 0}


def test_build_requested_selects_candidates() -> None:
    templates = [
        {"template_id": "survival.eat_owned_food", "tags": ["food", "hunger_high"]},
        {"template_id": "cleaning.clean_nearby", "tags": ["cleanup"]},
        {"template_id": "safety.idle_at_camp", "tags": ["fallback"]},
    ]
    rec = build_requested(
        {"actor_id": "a1", "state_versions": {"personal_state": 4}, "reason": "hunger_high"},
        templates,
    )
    assert rec["decision_id"] == "a1:4"
    assert rec["actor_id"] == "a1"
    assert rec["state_version"] == 4
    # food タグのテンプレ + 終端フォールバックが候補、cleanup は除外。
    assert "survival.eat_owned_food" in rec["candidates"]
    assert "cleaning.clean_nearby" not in rec["candidates"]
    assert rec["candidates"][-1] == "safety.idle_at_camp"
