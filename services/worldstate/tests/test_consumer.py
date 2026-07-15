"""購読土台の冪等ロジックのユニットテスト（実 NATS / DB 不要）。"""

from __future__ import annotations

import json

import pytest

from app.consumer import InMemoryDedup, handle_message, message_id_of


def _envelope(event_id: str, event_type: str = "resource.mined", world_id: str = "w1") -> bytes:
    return json.dumps(
        {
            "event_id": event_id,
            "world_id": world_id,
            "type": event_type,
            "sequence": 1,
            "payload": {"grants": [{"item_definition_id": "stone", "quantity": 1}]},
        }
    ).encode()


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


@pytest.mark.asyncio
async def test_distinct_events_both_accepted() -> None:
    dedup = InMemoryDedup()
    assert await handle_message(_envelope("ev-1"), dedup) is True
    assert await handle_message(_envelope("ev-2"), dedup) is True


@pytest.mark.asyncio
async def test_malformed_message_is_dropped() -> None:
    dedup = InMemoryDedup()
    assert await handle_message(b"not-json", dedup) is False


def test_message_id_prefers_event_id() -> None:
    assert message_id_of({"event_id": "ev-9", "world_id": "w", "sequence": 3}) == "ev-9"


def test_message_id_falls_back_to_world_sequence() -> None:
    assert message_id_of({"world_id": "w", "sequence": 3}) == "w:3"
