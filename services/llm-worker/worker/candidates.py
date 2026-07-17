"""Projection 取得と候補テンプレのルール絞り込み（08B 3.2 手順2 / MVP 9.3・10.2）。

**自由形式のゲーム命令を LLM に生成させない**ための土台。LLM に渡すのは、ここでルールが
選び抜いた Allowed Template の ID 集合と、短い状態要約だけ（9.3）。逸脱出力は
``worker.schemas.validate_decision`` が破棄する。

> 注: タグ規則は ``services/worldstate/app/candidates.py`` と意図的に同じ内容を持つ。両サービスは
> 独立にデプロイされ共有 Python パッケージを持たないため、共有の代わりに**同じ規則の二重実装**に
> なっている。worldstate 側は requested の候補記録用、こちらは LLM 入力用。**片方だけ変えない**
> こと（候補集合がずれると、worldstate が記録した候補と LLM が選べる候補が食い違う）。
"""

from __future__ import annotations

import json
import logging
from typing import Any

logger = logging.getLogger("llm-worker.candidates")

# reason（再判断トリガ / urgency タグ）→ 候補として残す tag 群。
# worldstate/app/candidates.py の REASON_TAGS と同一に保つこと。
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

# World Event Template は Actor の行動候補ではない（Director 用・10.3）。Decision の候補から除く。
WORLD_EVENT_TAG = "world_event"


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
    - World Event テンプレは Actor の候補から除く。
    - reason が未知でヒット無しなら、フォールバックのみ（安全に成立させる）。
    順序は template_id 昇順で安定（プロンプトも決定的になり prompt caching が効く）。
    """
    wanted = _wanted_tags(reason)
    picked: list[str] = []
    fallback_ids: list[str] = []
    for tpl in sorted(templates, key=lambda t: str(t.get("template_id", ""))):
        template_id = str(tpl.get("template_id", ""))
        tags = set(tpl.get("tags", []) or [])
        if WORLD_EVENT_TAG in tags:
            continue
        if FALLBACK_TAG in tags or template_id == FALLBACK_TEMPLATE:
            fallback_ids.append(template_id)
            continue
        if wanted and (tags & wanted):
            picked.append(template_id)
    for fid in fallback_ids:
        if fid not in picked:
            picked.append(fid)
    if not picked:
        picked = [FALLBACK_TEMPLATE]
    return picked


def select_event_candidates(templates: list[dict[str, Any]]) -> list[str]:
    """Director 用に World Event テンプレ（10.3 の 3 種）だけを template_id 昇順で返す。"""
    return sorted(
        str(t.get("template_id", ""))
        for t in templates
        if WORLD_EVENT_TAG in set(t.get("tags", []) or [])
    )


def state_summary(projection: dict[str, Any] | None, reason: str) -> str:
    """LLM に渡す**短い**状態要約を組む（10.2 手順3）。

    個人認証情報は入れない（17章 MVP-SEC-008）: 投影の PersonalState / Inventory Summary /
    現在行動という、ゲーム状態のみを載せる。投影が無ければ reason だけで成立させる。
    """
    lines = [f"reason: {reason or 'periodic'}"]
    if projection:
        for key in ("personal_state", "inventory_summary", "active_template", "wallet"):
            if key in projection:
                lines.append(f"{key}: {json.dumps(projection[key], separators=(',', ':'))}")
    else:
        lines.append("projection: unavailable (decide from reason alone)")
    return "\n".join(lines)


def world_summary(active_events: int, regions: list[str]) -> str:
    """Director に渡す World Summary（10.1 / 10.3）。実値ではなく状況の粗い要約のみ。"""
    lines = [f"active_world_events: {active_events}"]
    if regions:
        lines.append(f"known_regions: {', '.join(sorted(regions))}")
    return "\n".join(lines)


class CandidateRepo:
    """候補絞り込みの入力（active テンプレ・投影）を Postgres から読む Reader。

    テンプレ集合は起動時にスナップショットして in-memory で選ぶ（R7: コールバックを軽く保つ）。
    投影は actor 単位で 1 行読むだけの軽量クエリ。
    """

    def __init__(self, pool: Any) -> None:
        self._pool = pool
        self._templates: list[dict[str, Any]] = []

    async def load_templates(self) -> list[dict[str, Any]]:
        """status=active のテンプレを template_id ごとの最新 version で読み、キャッシュする。"""
        rows = await self._pool.fetch(
            """
            SELECT DISTINCT ON (template_id)
                   template_id, version, status, tags, definition
              FROM action_templates
             WHERE status = 'active'
             ORDER BY template_id, version DESC
            """
        )
        self._templates = [_template_row(r) for r in rows]
        return self._templates

    @property
    def templates(self) -> list[dict[str, Any]]:
        return self._templates

    def template_version(self, template_id: str) -> int | None:
        """候補テンプレの version を返す（B.1 の template_version を Worker が確定するため）。"""
        for tpl in self._templates:
            if tpl.get("template_id") == template_id:
                version = tpl.get("version")
                return int(version) if version is not None else None
        return None

    async def load_projection(self, actor_id: str) -> dict[str, Any] | None:
        """actor の投影 payload と projection_version を読む。無ければ None。"""
        row = await self._pool.fetchrow(
            """
            SELECT payload, projection_version
              FROM actor_state_projections
             WHERE actor_id = $1
            """,
            actor_id,
        )
        if row is None:
            return None
        payload = row["payload"]
        if isinstance(payload, str):  # asyncpg は JSONB を文字列で返す場合がある
            payload = json.loads(payload)
        if not isinstance(payload, dict):
            payload = {}
        payload["projection_version"] = int(row["projection_version"])
        return payload


def _template_row(row: Any) -> dict[str, Any]:
    definition = row["definition"]
    if isinstance(definition, str):
        definition = json.loads(definition)
    return {
        "template_id": row["template_id"],
        "version": row["version"],
        "status": row["status"],
        "tags": list(row["tags"]),
        "definition": definition,
    }
