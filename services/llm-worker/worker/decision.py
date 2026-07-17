"""ActionDecision の組み立て（08B 3.2 / 付録B.1）。

``build_mock_decision``（M0 から保持・決定的）と ``build_decision``（実 LLM の構造化出力）の
両方をここに置く。どちらも最終形は同じ proto ``ActionDecision`` 形 + 付録B.1 の拡張フィールドで、
DS の 9.4 検証（Version / Precondition / Target / 鮮度 / 重複）に必要な値を必ず含める。

**ID とバージョンは Worker が確定する**（LLM に生成させない・3.3）。``decision_id`` は
``actor_id:personal_state_version`` の決定的合成: ULID にすると再試行のたびに別 ID になり
「``decision_id`` による冪等」（落とし穴4）が成立しないため、同一要求が必ず同一 ID になる
合成規則を使う。worldstate（requested）と同じ規則なので、単一行を requested→produced へ
遷移させられる（M4 から継承）。
"""

from __future__ import annotations

import os
import time
from typing import Any

from worker.schemas import ActionDecisionOutput, validate_decision

# 判断の鮮度（9.4 の lease/鮮度検証）。これを過ぎた Decision を DS は適用しない。
DEFAULT_LEASE_SEC = 30

# reason（再判断トリガ / urgency タグ）→ 選ぶ 1 テンプレ。フォールバックは安全待機。
# worldstate の候補絞り込み（app/candidates.py）と整合する主タグ対応（3.6）。
FALLBACK_TEMPLATE = "safety.idle_at_camp"
REASON_TEMPLATE: tuple[tuple[str, str], ...] = (
    ("hunger", "survival.eat_owned_food"),
    ("food", "survival.eat_owned_food"),
    ("cleanup", "cleaning.clean_nearby"),
    ("clean", "cleaning.clean_nearby"),
    ("earn", "mining.acquire_iron"),
    ("iron", "mining.acquire_iron"),
    ("sell", "economy.sell_surplus"),
    ("overflow", "economy.sell_surplus"),
    ("event", "worldevent.join"),
)


def decision_id_of(actor_id: str, state_version: int) -> str:
    """worldstate（requested）と一致する決定的 decision_id（単一行遷移・冪等用, 3.2）。"""
    return f"{actor_id}:{state_version}"


def personal_state_version(request: dict[str, Any]) -> int:
    """DecisionRequest から personal_state_version を解決する（proto state_versions マップ）。"""
    versions = request.get("state_versions")
    if isinstance(versions, dict) and versions:
        if "personal_state" in versions:
            return int(versions["personal_state"])
        return int(next(iter(versions.values())))
    return int(request.get("state_version", 0) or 0)


def world_version(request: dict[str, Any]) -> int:
    """DecisionRequest から world_version を解決する（B.1 の DS 側 Version 検証用）。"""
    versions = request.get("state_versions")
    if isinstance(versions, dict) and "world" in versions:
        return int(versions["world"])
    return int(request.get("world_version", 0) or 0)


def _select_template(reason: str) -> str:
    reason = (reason or "").strip().lower()
    for key, template_id in REASON_TEMPLATE:
        if key in reason:
            return template_id
    return FALLBACK_TEMPLATE


def _lease_sec() -> int:
    try:
        return int(os.environ["LLM_DECISION_LEASE_SEC"])
    except (KeyError, ValueError):
        return DEFAULT_LEASE_SEC


def _envelope(
    request: dict[str, Any],
    template_id: str,
    steps: list[dict[str, Any]],
    *,
    now_ms: int,
    template_version: int | None = None,
    parameters: dict[str, str] | None = None,
    reason: str | None = None,
) -> dict[str, Any]:
    """proto ActionDecision + 付録B.1 拡張の共通組み立て（Worker が ID/version/時刻を確定）。

    proto に無い B.1 フィールド（world_version / personal_state_version / template_version /
    parameters / lease_until）は JSON ペイロード側で運ぶ — proto の破壊的変更を避けるため
    （0.3 / 落とし穴9）。DS は JSON を読んで 9.4 検証に使う。
    """
    actor_id = str(request.get("actor_id", "unknown"))
    state_version = personal_state_version(request)
    decision: dict[str, Any] = {
        "decision_id": decision_id_of(actor_id, state_version),
        "actor_id": actor_id,
        "state_version": state_version,
        "template_id": template_id,
        "steps": steps,
        "created_at_unix_ms": now_ms,
        # 付録B.1 拡張（JSON 側で保持）。
        "world_version": world_version(request),
        "personal_state_version": state_version,
        "template_version": template_version,
        "parameters": parameters or {},
        "lease_until": now_ms + _lease_sec() * 1000 if now_ms else 0,
    }
    if reason:
        decision["reason"] = reason
    return decision


def build_mock_decision(request: dict[str, Any]) -> dict[str, Any]:
    """判断要求に対する決定的なモック ActionDecision を返す（proto ActionDecision 形）。

    純関数（I/O 無し）でユニットテスト可能。reason に対応するテンプレを 1 件選ぶ。
    created_at_unix_ms は決定的に保つため 0（ゴールデンテストが時刻に揺れない）。
    ``LLM_MOCK=1`` の経路と、実 LLM が使えない環境の安全側フォールバックで使う。
    """
    template_id = _select_template(str(request.get("reason", "")))
    decision = _envelope(
        request,
        template_id,
        [{"action_template_id": template_id, "params": {}}],
        now_ms=0,
    )
    decision["mock"] = True
    return decision


def stamp(
    decision: dict[str, Any],
    *,
    template_version: int | None = None,
    now_ms: int | None = None,
) -> dict[str, Any]:
    """発行直前に時刻と template_version を確定する（モック経路用）。

    ``build_mock_decision`` は時刻 0 の決定的な形を返す（ゴールデンテストが揺れないように）。
    だが 0 のまま発行すると DS の鮮度検証（9.4）が常に期限切れと判定してしまう — LLM_MOCK=1 は
    DS 側のオフライン開発の既定なので、**発行時にここで実時刻を入れる**。
    """
    stamped = dict(decision)
    stamped["created_at_unix_ms"] = int(time.time() * 1000) if now_ms is None else now_ms
    stamped["lease_until"] = stamped["created_at_unix_ms"] + _lease_sec() * 1000
    if template_version is not None:
        stamped["template_version"] = template_version
    return stamped


def build_decision(
    request: dict[str, Any],
    output: ActionDecisionOutput,
    candidates: list[str],
    *,
    template_version: int | None = None,
    now_ms: int | None = None,
) -> dict[str, Any]:
    """検証済みの LLM 構造化出力から発行用 ActionDecision を組む（3.2 手順4）。

    Allowed ID 検証（候補集合 / PrimitiveActionRegistry）をここで必ず通す。逸脱していれば
    ``AllowedIdError`` が上がり、呼び出し側は Decision を破棄して ``ai_decisions.status`` を
    'rejected' にする（DS は現行行動継続 → Utility Fallback）。
    """
    validate_decision(output, candidates)
    stamp = int(time.time() * 1000) if now_ms is None else now_ms
    steps = [
        {"action_template_id": s.action_template_id, "params": dict(s.params)} for s in output.steps
    ]
    # template_version は候補テンプレの実 version を正とする（LLM の自己申告は採用しない）。
    return _envelope(
        request,
        output.template_id,
        steps,
        now_ms=stamp,
        template_version=template_version,
        parameters=dict(output.parameters),
        reason=output.reason,
    )


def result_subject(request: dict[str, Any]) -> str:
    """result subject に埋める server_id を request コンテキストから解決する（3.2）。"""
    server_id = request.get("server_id") or request.get("world_id") or "unknown"
    return f"ai.decision.result.{server_id}"
