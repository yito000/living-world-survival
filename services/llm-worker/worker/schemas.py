"""構造化出力スキーマと Allowed ID 検証（08B 3.3 / 17章 MVP-SEC-008）。

LLM に生成させるのは **選択だけ**（template_id / steps / parameters、または event_template_id /
タグ / 強度 / 開始 Window）。``decision_id``・各種 version・座標・スポーン数・供給予算は Worker と
ルールエンジンと DS が確定する — ID/バージョンの偽装と、基本設計 8.2 が禁じる実値の LLM 決定を
どちらも防ぐため。

Pydantic モデルを 1 つの正とし、JSON Schema はそこから導出する（``messages.parse`` の
``output_format`` に渡す）。生成結果は必ず本モジュールの検証を通してから発行する。未検証の
出力を DS へ渡すのは 17章 MVP-SEC-008 違反。
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field

# ---------------------------------------------------------------------------
# Allowed ID 集合
# ---------------------------------------------------------------------------

# PrimitiveActionRegistry の許可 ID（9.1）。steps[].action_template_id はここに限る。
# 0004 seed の action_templates.definition.steps が実際に使う Primitive を網羅する。
PRIMITIVE_ACTIONS: frozenset[str] = frozenset(
    {
        # 汎用（9.1 の基本 Primitive）。
        "MoveTo",
        "Interact",
        "UseItem",
        "Craft",
        "Purchase",
        "Idle",
        # 9.3 の Action Template が組み立てに使う Primitive。
        "SelectFood",
        "ConsumeFood",
        "FindAnimal",
        "Hunt",
        "Butcher",
        "MoveToStation",
        "Cook",
        "FindOreNode",
        "Mine",
        "ReserveMaterials",
        "MoveToForge",
        "Research",
        "FindBuyer",
        "SelectSellableItem",
        "RequestSale",
        "SelectLowValueItem",
        "DropItem",
        "FindWaste",
        "CleanWaste",
        "PrepareEquipment",
        "MoveToRegion",
        "JoinEvent",
        "MoveToCamp",
    }
)

# World Event Template の Allowed ID（10.3）。api 側の承認検査（AllowedTemplates）と一致させる。
WORLD_EVENT_TEMPLATES: tuple[str, ...] = (
    "world_event.great_hunt",
    "world_event.rare_resource",
    "world_event.rare_buyer_rush",
)


class AllowedIdError(ValueError):
    """LLM 出力が Allowed ID 集合から逸脱したときに送出する（Decision は破棄する）。"""


# ---------------------------------------------------------------------------
# ActionDecision（付録B.1 / proto ActionDecision）
# ---------------------------------------------------------------------------


class ActionStepOutput(BaseModel):
    """1 行動ステップ。params は文字列マップ（proto ActionStep.params と同型）。"""

    model_config = ConfigDict(extra="forbid")

    action_template_id: str
    params: dict[str, str] = Field(default_factory=dict)


class ActionDecisionOutput(BaseModel):
    """LLM が選ぶ範囲だけを持つ ActionDecision。ID/version/時刻は Worker 側で確定する。"""

    model_config = ConfigDict(extra="forbid")

    template_id: str
    template_version: int | None = None
    steps: list[ActionStepOutput] = Field(default_factory=list)
    parameters: dict[str, str] = Field(default_factory=dict)
    reason: str | None = None


def validate_decision(
    decision: ActionDecisionOutput, allowed_template_ids: list[str] | set[str]
) -> ActionDecisionOutput:
    """Allowed ID 検証（10.2）。逸脱があれば AllowedIdError を送出する。

    - ``template_id`` は候補集合（status=active から絞った Allowed Template）に限る。
    - ``steps[].action_template_id`` は PrimitiveActionRegistry の許可 ID に限る。
    - steps は最低 1 件（空の Decision は DS で適用できない）。
    """
    allowed = set(allowed_template_ids)
    if decision.template_id not in allowed:
        raise AllowedIdError(
            f"template_id {decision.template_id!r} is not in the candidate set {sorted(allowed)}"
        )
    if not decision.steps:
        raise AllowedIdError("steps must not be empty")
    for step in decision.steps:
        if step.action_template_id not in PRIMITIVE_ACTIONS:
            raise AllowedIdError(
                f"action_template_id {step.action_template_id!r} is not an allowed primitive action"
            )
    return decision


# ---------------------------------------------------------------------------
# EventProposal（付録B.2 / proto EventProposal）
# ---------------------------------------------------------------------------


class EventProposalOutput(BaseModel):
    """LLM が選ぶ範囲だけを持つ EventProposal。

    テンプレ/地域タグ/理由タグ/強度/開始 Window のみ。**具体スポーン数・供給予算・座標は
    含めない**（基本設計 8.2: 実値はルールエンジンと DS が決める）。
    """

    model_config = ConfigDict(extra="forbid")

    event_template_id: Literal[
        "world_event.great_hunt",
        "world_event.rare_resource",
        "world_event.rare_buyer_rush",
    ]
    region_id: str | None = None
    region_tags: list[str] = Field(default_factory=list)
    reason_tags: list[str] = Field(default_factory=list)
    requested_intensity: float
    start_after_sec: int | None = None
    start_before_sec: int | None = None


def validate_proposal(proposal: EventProposalOutput) -> EventProposalOutput:
    """EventProposal の値域検証（付録B.2）。逸脱があれば AllowedIdError を送出する。

    Literal 型で enum は既に弾かれるが、``requested_intensity`` の値域は Pydantic の
    制約に載せず**ここで**見る: 構造化出力は数値制約（minimum/maximum）を保証しないため、
    スキーマ任せにすると範囲外がすり抜ける（api 側 3.6 でも二重に弾く）。
    """
    if proposal.event_template_id not in WORLD_EVENT_TEMPLATES:  # pragma: no cover - Literal で担保
        raise AllowedIdError(f"event_template_id {proposal.event_template_id!r} is not allowed")
    if not 0.0 <= proposal.requested_intensity <= 1.0:
        raise AllowedIdError(
            f"requested_intensity {proposal.requested_intensity} is outside [0, 1]"
        )
    for key in ("start_after_sec", "start_before_sec"):
        value = getattr(proposal, key)
        if value is not None and value < 0:
            raise AllowedIdError(f"{key} must not be negative (got {value})")
    after, before = proposal.start_after_sec, proposal.start_before_sec
    if after is not None and before is not None and before < after:
        raise AllowedIdError(f"start_before_sec {before} precedes start_after_sec {after}")
    return proposal


def action_decision_schema() -> dict[str, Any]:
    """ActionDecision の JSON Schema（Pydantic から導出・3.3 の形と整合）。"""
    return ActionDecisionOutput.model_json_schema()


def event_proposal_schema() -> dict[str, Any]:
    """EventProposal の JSON Schema（Pydantic から導出・3.3 の形と整合）。"""
    return EventProposalOutput.model_json_schema()
