"""World Event Director のユニットテスト（08B 3.9 Unit(Director) / L-7）。

3 テンプレの enum 制約と ``requested_intensity ∈ [0,1]`` の検証、および提案の発行/非発行を
LLM 差し替えで確認する。
"""

from __future__ import annotations

import json
from typing import Any

import pytest
from pydantic import ValidationError

from worker.event_director import (
    EventDirector,
    build_mock_proposal,
    build_proposal,
    proposal_id_of,
    proposal_subject,
)
from worker.llm import LLMError
from worker.schemas import (
    WORLD_EVENT_TEMPLATES,
    AllowedIdError,
    EventProposalOutput,
    validate_proposal,
)

REQUEST = {"world_id": "w1", "server_id": "s1", "reason": "hunt pressure"}
# 候補は template_id 昇順で安定させる（プロンプトが決定的になり prompt caching が効く）。
CANDIDATES = sorted(WORLD_EVENT_TEMPLATES)


class FakeNats:
    def __init__(self) -> None:
        self.published: list[tuple[str, bytes]] = []

    async def publish(self, subject: str, data: bytes) -> None:
        self.published.append((subject, data))


class FakeRepo:
    templates = [
        {"template_id": "world_event.great_hunt", "version": 1, "tags": ["world_event"]},
        {"template_id": "world_event.rare_resource", "version": 1, "tags": ["world_event"]},
        {"template_id": "world_event.rare_buyer_rush", "version": 1, "tags": ["world_event"]},
        {"template_id": "safety.idle_at_camp", "version": 1, "tags": ["fallback"]},
    ]


class FakeClient:
    def __init__(self, output: Any = None, error: Exception | None = None) -> None:
        self._output = output
        self._error = error
        self.last_usage: dict[str, int] = {}
        self.calls: list[list[str]] = []

    async def propose_event(self, candidates: list[str], summary: str) -> Any:
        self.calls.append(candidates)
        if self._error is not None:
            raise self._error
        return self._output


def proposal_output(template_id: str = "world_event.great_hunt", intensity: float = 0.7):
    return EventProposalOutput(
        event_template_id=template_id,  # type: ignore[arg-type]
        region_id="region-a",
        region_tags=["forest"],
        reason_tags=["low_food_supply"],
        requested_intensity=intensity,
        start_after_sec=0,
        start_before_sec=300,
    )


# ---------------------------------------------------------------------------
# スキーマ制約
# ---------------------------------------------------------------------------


def test_event_template_enum_is_the_three_mvp_templates() -> None:
    assert WORLD_EVENT_TEMPLATES == (
        "world_event.great_hunt",
        "world_event.rare_resource",
        "world_event.rare_buyer_rush",
    )


def test_event_template_outside_enum_is_rejected() -> None:
    # enum 制約は Literal 型で構造化出力の段階から弾かれる。
    with pytest.raises(ValidationError):
        EventProposalOutput(event_template_id="world_event.meteor_strike", requested_intensity=0.5)  # type: ignore[arg-type]


@pytest.mark.parametrize("intensity", [-0.1, 1.1, 42.0])
def test_intensity_outside_unit_range_is_rejected(intensity: float) -> None:
    with pytest.raises(AllowedIdError):
        validate_proposal(proposal_output(intensity=intensity))


@pytest.mark.parametrize("intensity", [0.0, 0.5, 1.0])
def test_intensity_within_unit_range_is_accepted(intensity: float) -> None:
    assert validate_proposal(proposal_output(intensity=intensity)).requested_intensity == intensity


def test_start_window_must_not_be_inverted() -> None:
    output = proposal_output()
    output.start_after_sec, output.start_before_sec = 300, 10
    with pytest.raises(AllowedIdError):
        validate_proposal(output)


# ---------------------------------------------------------------------------
# proto EventProposal へのマップ
# ---------------------------------------------------------------------------


def test_build_proposal_maps_to_proto_shape() -> None:
    p = build_proposal(REQUEST, proposal_output(), proposal_id="P1")
    assert p == {
        "proposal_id": "P1",
        "template_id": "world_event.great_hunt",
        "world_id": "w1",
        "region_id": "region-a",
        "params": {
            "region_tags": ["forest"],
            "reason_tags": ["low_food_supply"],
            "requested_intensity": 0.7,
            "start_after_sec": 0,
            "start_before_sec": 300,
        },
        # score はルール評価値（= requested_intensity）を入れる。
        "score": 0.7,
    }


def test_build_proposal_carries_no_concrete_values() -> None:
    # 基本設計 8.2: スポーン数・供給予算・座標を LLM に決めさせない（提案に含めない）。
    params = build_proposal(REQUEST, proposal_output(), proposal_id="P1")["params"]
    for banned in ("spawn_count", "alive_cap", "total_cap", "budget", "position", "coordinates"):
        assert banned not in params


def test_proposal_id_is_unique_and_sortable() -> None:
    ids = [proposal_id_of() for _ in range(50)]
    assert len(set(ids)) == 50
    assert all(len(i) == 26 for i in ids)


def test_proposal_subject_resolves_server_id() -> None:
    assert proposal_subject({"server_id": "s9"}) == "worldevent.proposal.s9"
    assert proposal_subject({"world_id": "w3"}) == "worldevent.proposal.w3"
    assert proposal_subject({}) == "worldevent.proposal.unknown"


# ---------------------------------------------------------------------------
# Director
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_director_publishes_proposal() -> None:
    nc, client = FakeNats(), FakeClient(output=proposal_output())
    director = EventDirector(nc, FakeRepo(), client, mock=False)

    proposal = await director.on_request(json.dumps(REQUEST).encode())

    assert proposal is not None
    assert proposal["template_id"] == "world_event.great_hunt"
    subject, data = nc.published[0]
    assert subject == "worldevent.proposal.s1"
    assert json.loads(data)["score"] == 0.7
    # 候補は World Event テンプレのみ（Actor 行動テンプレは混ぜない）。
    assert client.calls[0] == CANDIDATES


@pytest.mark.asyncio
async def test_director_discards_out_of_range_intensity() -> None:
    nc = FakeNats()
    director = EventDirector(
        nc, FakeRepo(), FakeClient(output=proposal_output(intensity=9.9)), False
    )

    assert await director.on_request(json.dumps(REQUEST).encode()) is None
    assert nc.published == []


@pytest.mark.asyncio
async def test_director_publishes_nothing_when_llm_fails() -> None:
    # 10.4 と同じ原則: 出せないときは次の評価窓まで待つ（勝手な代替を出さない）。
    nc = FakeNats()
    director = EventDirector(nc, FakeRepo(), FakeClient(error=LLMError("timeout")), False)

    assert await director.on_request(json.dumps(REQUEST).encode()) is None
    assert nc.published == []


@pytest.mark.asyncio
async def test_director_mock_mode_selects_by_reason() -> None:
    nc = FakeNats()
    director = EventDirector(nc, FakeRepo(), None, mock=True)

    proposal = await director.on_request(json.dumps(REQUEST).encode())

    assert proposal is not None
    # reason="hunt pressure" → great_hunt。
    assert proposal["template_id"] == "world_event.great_hunt"


@pytest.mark.asyncio
async def test_director_ignores_malformed_request() -> None:
    nc = FakeNats()
    director = EventDirector(nc, FakeRepo(), FakeClient(output=proposal_output()), False)

    assert await director.on_request(b"{{{") is None
    assert nc.published == []


def test_mock_proposal_always_produces_an_allowed_template() -> None:
    for reason in ("hunt", "rare ore", "buyer rush", "", "something unrelated"):
        output = build_mock_proposal({"reason": reason}, CANDIDATES)
        assert output.event_template_id in WORLD_EVENT_TEMPLATES
        validate_proposal(output)
