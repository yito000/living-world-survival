"""候補 Template 絞り込みルール（07B 3.4 / MVP 10.2 手順2）。

`ai.decision.request` の ``reason`` と（あれば）投影から、``action_templates`` の中で
LLM/Fallback に渡す **Allowed Template のみ**へ絞る土台。M4 は LLM 本体を呼ばず、ここで
入力（候補集合）を整形するところまで（本体は M5）。純関数にしてユニットテスト可能にする。
"""

from __future__ import annotations

from typing import Any

# reason（再判断トリガ / urgency タグ）→ 候補として残す tag 群のマップ。
# Utility Fallback の tag→テンプレ対応（3.6: food→eat / cleanup→clean / earn→mine）に整合。
REASON_TAGS: dict[str, tuple[str, ...]] = {
    "food": ("food", "hunger_high"),
    "hunger": ("food", "hunger_high"),
    "hunger_high": ("food", "hunger_high"),
    "cleanup": ("cleanup", "cleanliness_high", "waste_nearby"),
    "cleanliness": ("cleanup", "cleanliness_high", "waste_nearby"),
    "earn": ("earn", "iron_needed"),
    "sell": ("sell", "inventory_overflow", "sellable_item"),
    "inventory_overflow": ("sell", "inventory_overflow", "cleanup"),
    "event": ("event_available", "risk_acceptable"),
}

# どの reason にも当たらないとき／終端に常に残すフォールバック（安全待機）。
FALLBACK_TEMPLATE = "safety.idle_at_camp"
FALLBACK_TAG = "fallback"


def _wanted_tags(reason: str) -> set[str]:
    """reason 文字列から候補として残したい tag 集合を導く（部分一致で寛容に解決）。"""
    reason = (reason or "").strip().lower()
    wanted: set[str] = set()
    for key, tags in REASON_TAGS.items():
        if key and key in reason:
            wanted.update(tags)
    return wanted


def select_candidates(templates: list[dict[str, Any]], reason: str) -> list[str]:
    """active テンプレ集合から reason に対応するタグを持つ候補 template_id を返す。

    - reason のタグを持つテンプレを Allowed として残す（候補外は除外）。
    - フォールバック（fallback タグ / safety.idle_at_camp）は終端として常に含める。
    - reason が未知でヒット無しなら、フォールバックのみ（安全に成立させる）。
    順序は template_id 昇順で安定。
    """
    wanted = _wanted_tags(reason)
    picked: list[str] = []
    fallback_ids: list[str] = []
    for tpl in sorted(templates, key=lambda t: str(t.get("template_id", ""))):
        template_id = str(tpl.get("template_id", ""))
        tags = set(tpl.get("tags", []) or [])
        if FALLBACK_TAG in tags or template_id == FALLBACK_TEMPLATE:
            fallback_ids.append(template_id)
            continue
        if wanted and (tags & wanted):
            picked.append(template_id)
    # フォールバックは終端として必ず末尾に足す（重複は避ける）。
    for fid in fallback_ids:
        if fid not in picked:
            picked.append(fid)
    if not picked:
        picked = [FALLBACK_TEMPLATE]
    return picked
