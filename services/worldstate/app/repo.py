"""WorldState の Postgres リポジトリ層（M4 / 07B 3.2-3.4）。

worldstate が Writer/Reader になるテーブルへの asyncpg アクセスをまとめる:

- ``action_templates``  … status=active の配信・タグ絞り込み（Reader は DS/LLM）。
- ``actor_state_projections`` … Actor イベントを正に投影を再構築（Writer=WorldState Consumer）。
- ``ai_decisions`` … 判断履歴の記録（Writer=WorldState / LLM Worker）。

worldstate は ``domain_events`` を**直接書かない**（投影専用, 13.1 / 落とし穴1）。
asyncpg は import 時にオプショナル扱いにし、インフラ無しでもユニットテストが通るようにする。
"""

from __future__ import annotations

import json
import logging
import os
from typing import Any

try:  # pragma: no cover - import guard
    import asyncpg
except Exception:  # pragma: no cover
    asyncpg = None  # type: ignore[assignment]

logger = logging.getLogger("worldstate.repo")


async def create_pool() -> Any | None:
    """DATABASE_URL から asyncpg プールを張る。ライブラリ/URL/接続が無ければ None。

    プールは配信 API・投影・判断履歴・dedup で共有する（1 サービス 1 プール）。
    """
    if asyncpg is None:
        return None
    url = os.getenv("DATABASE_URL")
    if not url:
        return None
    try:
        return await asyncpg.create_pool(dsn=url, min_size=1, max_size=8)
    except Exception:  # pragma: no cover - infra dependent
        logger.warning("could not connect Postgres", exc_info=True)
        return None


# World Event Template（10.3）は action_templates に相乗りしているが、Actor の行動テンプレでは
# ない（definition は steps ではなく constraints を持つ）。配信/候補からは必ず除外する。
WORLD_EVENT_TAG = "world_event"


class TemplateRepo:
    """action_templates の Reader/配信（07B 3.2）。status=active の**行動**テンプレのみ配信する。"""

    def __init__(self, pool: Any) -> None:
        self._pool = pool

    async def list_active(self) -> list[dict[str, Any]]:
        """status=active の Action Template definition 一式を template_id 昇順で返す。

        DS 起動時取得（GET /internal/action_templates）と Utility Fallback 供給で使う。
        version で世代管理し、同 template_id は最新 version のみ返す（DS のキャッシュ突合用）。

        M5 で同テーブルに World Event Template（10.3）が同居したため、``world_event`` タグの
        行は除外する — DS はこれを Action Template として解釈する（07A のタグ→テンプレ解決）ので、
        steps を持たない World Event 行を混ぜると Fallback の候補が壊れる。World Event 側は
        api の承認検査（3.6）が template_id 直引きで参照するので、この除外の影響を受けない。
        """
        rows = await self._pool.fetch(
            """
            SELECT DISTINCT ON (template_id)
                   template_id, version, status, tags, definition
              FROM action_templates
             WHERE status = 'active'
               AND NOT (tags @> ARRAY[$1]::text[])
             ORDER BY template_id, version DESC
            """,
            WORLD_EVENT_TAG,
        )
        return [_template_row(r) for r in rows]


def _template_row(row: Any) -> dict[str, Any]:
    """asyncpg Record を JSON 応答向けの dict へ整形する（definition は JSONB→dict）。"""
    definition = row["definition"]
    if isinstance(definition, str):  # asyncpg は JSONB を文字列で返す場合がある
        definition = json.loads(definition)
    return {
        "template_id": row["template_id"],
        "version": row["version"],
        "status": row["status"],
        "tags": list(row["tags"]),
        "definition": definition,
    }


class ProjectionStore:
    """actor_state_projections の Writer（07B 3.3 / MVP 10.1）。

    Actor イベントを正に投影を**単調増加**で再構築する。projection_version は既存 +1、
    新規は 0 起点。payload は PersonalState / Inventory Summary / 現在行動 / 最近イベントを
    保持する。冪等（同一 event_id の二重適用なし）は呼び出し側の inbox_dedup で担保する。
    """

    def __init__(self, pool: Any) -> None:
        self._pool = pool

    async def apply(
        self, envelope: dict[str, Any], allow_aggregate_fallback: bool = True
    ) -> int | None:
        """1 つのイベント envelope を投影へ適用し、新しい projection_version を返す。

        actor_id は payload.actor_id を第一に、``allow_aggregate_fallback`` なら aggregate_id へ
        フォールバックする（M3 の owner 欠落と同種の契約ギャップ・無ければ集約 id で投影を進める）。
        economy イベントの aggregate_id は Buyer 等 Actor でない集約を指し得るので、呼び出し側は
        フォールバックを切って payload.actor_id が明示された場合だけ投影する（3.1）。
        actor_id/world_id が解決できなければ何もしない（None）。
        """
        actor_id, world_id = _actor_world_of(envelope, allow_aggregate_fallback)
        if not actor_id or not world_id:
            return None
        payload = _projection_payload(envelope)
        row = await self._pool.fetchrow(
            """
            INSERT INTO actor_state_projections
                   (actor_id, world_id, projection_version, payload, rebuilt_at)
            VALUES ($1, $2, 0, $3::jsonb, now())
            ON CONFLICT (actor_id) DO UPDATE
               SET world_id = EXCLUDED.world_id,
                   projection_version = actor_state_projections.projection_version + 1,
                   payload = actor_state_projections.payload || EXCLUDED.payload,
                   rebuilt_at = now()
            RETURNING projection_version
            """,
            actor_id,
            world_id,
            json.dumps(payload),
        )
        return int(row["projection_version"]) if row else None

    async def reset(self, world_id: str | None = None) -> int:
        """投影を捨てる（replay 前段, 3.1）。world_id 指定でその world のみ、省略で全件。

        イベントが正なので投影は捨てて作り直せる（13.1）。削除行数を返す。
        """
        if world_id:
            tag = await self._pool.execute(
                "DELETE FROM actor_state_projections WHERE world_id = $1", world_id
            )
        else:
            tag = await self._pool.execute("DELETE FROM actor_state_projections")
        return _rows_affected(tag)


def _rows_affected(tag: Any) -> int:
    """asyncpg の command tag（"DELETE 3"）から件数を取り出す。解釈できなければ 0。"""
    try:
        return int(str(tag).rsplit(" ", 1)[-1])
    except (ValueError, IndexError):  # pragma: no cover - defensive
        return 0


class DecisionStore:
    """ai_decisions の Writer（07B 3.4 / MVP 14.3）。

    request 受信（status=requested）と result 生成（status=produced）を**単一行**で遷移させる。
    decision_id は request の actor+state_version から決定的に合成し（両サービスで一致）、
    冪等記録（同一 decision_id を二重記録しない）を PK ON CONFLICT で担保する。
    """

    def __init__(self, pool: Any) -> None:
        self._pool = pool

    async def record_requested(
        self,
        decision_id: str,
        actor_id: str,
        state_version: int,
        candidates: list[str],
    ) -> None:
        """判断要求を requested として記録する。既存行があれば payload の候補のみ更新する。"""
        await self._pool.execute(
            """
            INSERT INTO ai_decisions (decision_id, actor_id, state_version, status, payload)
            VALUES ($1, $2, $3, 'requested', $4::jsonb)
            ON CONFLICT (decision_id) DO UPDATE
               SET payload = jsonb_set(
                       coalesce(ai_decisions.payload, '{}'::jsonb),
                       '{candidates}', EXCLUDED.payload -> 'candidates', true)
            """,
            decision_id,
            actor_id,
            int(state_version),
            json.dumps({"candidates": candidates}),
        )


def decision_id_of(actor_id: str, state_version: int) -> str:
    """request/result で一致する決定的 decision_id（actor+personal_state_version）。

    worldstate（requested）と llm-worker（produced）が同一行を遷移させるため、両者が同じ
    規則で合成する（M4 骨格の単一行ライフサイクル）。
    """
    return f"{actor_id}:{state_version}"


def _actor_world_of(
    envelope: dict[str, Any], allow_aggregate_fallback: bool = True
) -> tuple[str, str]:
    payload = envelope.get("payload")
    actor_id = ""
    if isinstance(payload, dict):
        actor_id = str(payload.get("actor_id") or "")
    if not actor_id and allow_aggregate_fallback:
        actor_id = str(envelope.get("aggregate_id") or "")
    world_id = str(envelope.get("world_id") or "")
    return actor_id, world_id


def _projection_payload(envelope: dict[str, Any]) -> dict[str, Any]:
    """envelope から投影 payload（10.1 相当）を組む。イベントを正に再構築可能な最小形。"""
    payload = envelope.get("payload")
    body = payload if isinstance(payload, dict) else {}
    proj: dict[str, Any] = {
        "last_event": {
            "event_id": envelope.get("event_id"),
            "type": envelope.get("type"),
            "sequence": envelope.get("sequence"),
        },
    }
    # PersonalState / Inventory Summary / 現在行動 / 財布が来ていれば投影へ引き上げる。
    for key in ("personal_state", "inventory_summary", "active_template", "wallet"):
        if key in body:
            proj[key] = body[key]
    return proj
