"""実 LLM 経路のゴールデンテスト（08B 3.9 Unit / L-6）。

実 LLM は CI で呼ばない（``LLM_MOCK=1`` が CI 既定）。ここでは LLM クライアントを
**モッククライアントで差し替え**、決定的入力 → 期待 ActionDecision を突き合わせる。
Allowed ID 検証（許可外 template_id / action_template_id の破棄）もここで担保する。
"""

from __future__ import annotations

import json
from typing import Any

import pytest

from worker.decision import build_decision, build_mock_decision
from worker.main import DecisionWorker
from worker.schemas import ActionDecisionOutput, ActionStepOutput, AllowedIdError

CANDIDATES = ["mining.acquire_iron", "safety.idle_at_camp"]

REQUEST = {
    "actor_id": "a1",
    "world_id": "w1",
    "server_id": "s1",
    "state_versions": {"personal_state": 7, "world": 42},
    "reason": "earn",
}


def decision_output(
    template_id: str = "mining.acquire_iron", action: str = "MoveTo"
) -> ActionDecisionOutput:
    return ActionDecisionOutput(
        template_id=template_id,
        steps=[ActionStepOutput(action_template_id=action, params={"target": "ore-1"})],
        parameters={"urgency": "high"},
        reason="hunger is fine; iron is needed",
    )


class FakeClient:
    """LLM クライアントのスタブ。決定的な構造化出力を返すか、例外を投げる。"""

    def __init__(self, output: Any = None, error: Exception | None = None) -> None:
        self._output = output
        self._error = error
        self.last_usage = {"input_tokens": 120, "output_tokens": 45}
        self.calls: list[tuple[list[str], str]] = []

    async def decide(self, candidates: list[str], summary: str) -> Any:
        self.calls.append((candidates, summary))
        if self._error is not None:
            raise self._error
        return self._output


class FakeNats:
    def __init__(self) -> None:
        self.published: list[tuple[str, bytes]] = []

    async def publish(self, subject: str, data: bytes) -> None:
        self.published.append((subject, data))


class FakeRepo:
    templates = [
        {"template_id": "mining.acquire_iron", "version": 1, "tags": ["earn", "iron_needed"]},
        {"template_id": "safety.idle_at_camp", "version": 1, "tags": ["fallback"]},
        {"template_id": "world_event.great_hunt", "version": 1, "tags": ["world_event"]},
    ]

    def template_version(self, template_id: str) -> int | None:
        for t in self.templates:
            if t["template_id"] == template_id:
                return int(t["version"])
        return None

    async def load_projection(self, actor_id: str) -> dict[str, Any] | None:
        return {"personal_state": {"hunger": 20}, "projection_version": 3}


# ---------------------------------------------------------------------------
# build_decision（純関数・ゴールデン）
# ---------------------------------------------------------------------------


def test_build_decision_golden() -> None:
    d = build_decision(REQUEST, decision_output(), CANDIDATES, template_version=1, now_ms=1000)
    assert d == {
        "decision_id": "a1:7",
        "actor_id": "a1",
        "state_version": 7,
        "template_id": "mining.acquire_iron",
        "steps": [{"action_template_id": "MoveTo", "params": {"target": "ore-1"}}],
        "created_at_unix_ms": 1000,
        "world_version": 42,
        "personal_state_version": 7,
        "template_version": 1,
        "parameters": {"urgency": "high"},
        "lease_until": 1000 + 30_000,
        "reason": "hunger is fine; iron is needed",
    }


def test_build_decision_rejects_template_outside_candidates() -> None:
    # 候補集合に無い template_id は破棄する（LLM が active な別テンプレを持ち出しても不可）。
    with pytest.raises(AllowedIdError):
        build_decision(REQUEST, decision_output(template_id="economy.sell_surplus"), CANDIDATES)


def test_build_decision_rejects_unknown_primitive_action() -> None:
    # PrimitiveActionRegistry に無い action は破棄する（9.1 / MVP-SEC-008）。
    with pytest.raises(AllowedIdError):
        build_decision(REQUEST, decision_output(action="TeleportToVictory"), CANDIDATES)


def test_build_decision_rejects_empty_steps() -> None:
    output = ActionDecisionOutput(template_id="mining.acquire_iron", steps=[])
    with pytest.raises(AllowedIdError):
        build_decision(REQUEST, output, CANDIDATES)


# ---------------------------------------------------------------------------
# DecisionWorker（LLM 差し替え）
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_worker_publishes_validated_decision() -> None:
    nc, client = FakeNats(), FakeClient(output=decision_output())
    worker = DecisionWorker(nc, None, FakeRepo(), client, mock=False)

    decision = await worker.on_request(json.dumps(REQUEST).encode())

    assert decision is not None
    assert decision["template_id"] == "mining.acquire_iron"
    assert decision["template_version"] == 1  # LLM の自己申告ではなく候補テンプレの実 version
    subject, data = nc.published[0]
    assert subject == "ai.decision.result.s1"
    assert json.loads(data)["decision_id"] == "a1:7"


@pytest.mark.asyncio
async def test_worker_narrows_candidates_by_reason() -> None:
    nc, client = FakeNats(), FakeClient(output=decision_output())
    worker = DecisionWorker(nc, None, FakeRepo(), client, mock=False)
    await worker.on_request(json.dumps(REQUEST).encode())

    candidates, summary = client.calls[0]
    # reason=earn → 該当タグのテンプレ + フォールバック終端。World Event は候補に混ぜない。
    assert candidates == ["mining.acquire_iron", "safety.idle_at_camp"]
    assert "world_event.great_hunt" not in candidates
    # 状態要約は投影から作る（個人情報は載せない）。
    assert "personal_state" in summary


@pytest.mark.asyncio
async def test_worker_publishes_nothing_when_llm_times_out() -> None:
    # AT-014: LLM タイムアウト時は結果を発行しない（DS は Utility Fallback へ）。
    from worker.llm import LLMError

    nc = FakeNats()
    worker = DecisionWorker(nc, None, FakeRepo(), FakeClient(error=LLMError("timeout")), mock=False)

    assert await worker.on_request(json.dumps(REQUEST).encode()) is None
    assert nc.published == []


@pytest.mark.asyncio
async def test_worker_publishes_nothing_when_llm_refuses() -> None:
    from worker.llm import DecisionRefused

    nc = FakeNats()
    worker = DecisionWorker(
        nc, None, FakeRepo(), FakeClient(error=DecisionRefused("refusal")), mock=False
    )

    assert await worker.on_request(json.dumps(REQUEST).encode()) is None
    assert nc.published == []


@pytest.mark.asyncio
async def test_worker_discards_decision_with_disallowed_ids() -> None:
    nc = FakeNats()
    client = FakeClient(output=decision_output(action="TeleportToVictory"))
    worker = DecisionWorker(nc, None, FakeRepo(), client, mock=False)

    assert await worker.on_request(json.dumps(REQUEST).encode()) is None
    assert nc.published == []


@pytest.mark.asyncio
async def test_worker_mock_mode_never_calls_llm() -> None:
    # LLM_MOCK=1 のゴールデン経路: クライアントがあっても呼ばれない。
    nc, client = FakeNats(), FakeClient(output=decision_output())
    worker = DecisionWorker(nc, None, FakeRepo(), client, mock=True)

    decision = await worker.on_request(json.dumps(REQUEST).encode())

    assert decision is not None and decision["mock"] is True
    assert client.calls == []
    assert nc.published[0][0] == "ai.decision.result.s1"


@pytest.mark.asyncio
async def test_worker_stamps_mock_decision_for_freshness_checks() -> None:
    # build_mock_decision は決定的（時刻 0）だが、発行する Decision は実時刻と lease を持つ。
    # 0 のまま出すと DS の鮮度検証（9.4）が常に期限切れと見なし、LLM_MOCK=1 の DS 開発が壊れる。
    nc = FakeNats()
    worker = DecisionWorker(nc, None, FakeRepo(), None, mock=True)

    decision = await worker.on_request(json.dumps(REQUEST).encode())

    assert decision is not None
    assert decision["created_at_unix_ms"] > 0
    assert decision["lease_until"] > decision["created_at_unix_ms"]
    # template_version も候補テンプレの実 version で埋める（実 LLM 経路と同じ形にする）。
    assert decision["template_version"] == 1


def test_build_mock_decision_stays_deterministic() -> None:
    # 純関数側は時刻 0 のまま（ゴールデンテストが時刻で揺れない）。
    assert build_mock_decision(REQUEST)["created_at_unix_ms"] == 0
    assert build_mock_decision(REQUEST) == build_mock_decision(REQUEST)


@pytest.mark.asyncio
async def test_worker_ignores_malformed_request() -> None:
    nc = FakeNats()
    worker = DecisionWorker(nc, None, FakeRepo(), FakeClient(output=decision_output()), mock=False)

    assert await worker.on_request(b"not json") is None
    assert await worker.on_request(b'"a string"') is None
    assert nc.published == []


# --- MVP-SEC-008: LLM 入力から個人認証情報を除外（17章 / 10B 3.4）------------


def test_state_summary_excludes_pii() -> None:
    """投影に個人認証情報が紛れても、LLM へ渡す要約には出さない。

    state_summary は許可キー（personal_state / inventory_summary / active_template /
    wallet）だけを載せる allowlist 方式。email / account_id / password のような
    PII キーが projection に入っていても、要約に混ざらないことを固定する。
    """
    from worker.candidates import state_summary

    projection = {
        "personal_state": {"hunger": 40, "stamina": 80},
        "inventory_summary": {"iron_ore": 3},
        "active_template": "mining.acquire_iron",
        "wallet": {"coins": 120},
        # 以下は投影に紛れ込み得る PII。要約に出てはならない。
        "email": "player@example.com",
        "account_id": "acc-secret-123",
        "password_hash": "$argon2id$v=19$m=...",
        "refresh_token": "rt-should-never-appear",
    }
    summary = state_summary(projection, reason="hungry")

    for leaked in (
        "player@example.com",
        "acc-secret-123",
        "$argon2id$",
        "rt-should-never-appear",
        "email",
        "account_id",
        "password",
        "refresh_token",
    ):
        assert leaked not in summary, f"PII leaked into LLM input: {leaked!r}"

    # ゲーム状態は載る（要約が空にならないこと）。
    assert "personal_state" in summary
    assert "hunger" in summary


def test_state_summary_survives_without_projection() -> None:
    """投影が無くても reason だけで要約が成立する（PII 経路を通らない）。"""
    from worker.candidates import state_summary

    summary = state_summary(None, reason="periodic")
    assert "reason: periodic" in summary
    assert "unavailable" in summary
