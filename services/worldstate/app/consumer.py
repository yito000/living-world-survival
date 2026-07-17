"""WorldState NATS 購読（M4 / 07B 3.3-3.4）。

M0/M3 の購読土台を拡張し、M4 では次を担う:

- ``world.*.event.actor`` を購読し ``actor_state_projections`` を投影・再構築（3.3）。
  ``inbox_dedup`` で At-least-once の重複（同一 event_id）を吸収し二重適用を防ぐ。
- ``world.*.event.resource`` は従来通り dedup + 受信ログのみ（投影対象外）。
- ``ai.decision.request`` を購読し、投影＋``action_templates`` から候補 Template を
  ルール絞り込みして ``ai_decisions`` に requested を記録（3.4）。**LLM 本体は M5**。

R7 に従い handler には重い処理を置かない（テンプレ集合は起動時スナップショット、
候補選択は in-memory、DB 書き込みは軽量 upsert のみ）。nats / asyncpg は import 時に
オプショナル扱いにして、インフラ無しでもユニットテストが通るようにする。
"""

from __future__ import annotations

import json
import logging
import time
from typing import Any, Protocol

from app.candidates import select_candidates
from app.obs import (
    DECISION_REQUESTS,
    EVENT_LAG_SECONDS,
    EVENTS_PROCESSED,
    PROJECTION_SECONDS,
)
from app.repo import DecisionStore, ProjectionStore, TemplateRepo, decision_id_of

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]

try:  # pragma: no cover - import guard
    import asyncpg
except Exception:  # pragma: no cover
    asyncpg = None  # type: ignore[assignment]

logger = logging.getLogger("worldstate.consumer")

# 購読 subject（14.3）。M5 で economy を追加し、actor/resource/economy の 3 系統を購読する。
# 投影対象は actor（常に）と economy（actor_id を持つもののみ）。resource は dedup+ログのみ。
SUBJECTS: tuple[str, ...] = (
    "world.*.event.resource",
    "world.*.event.actor",
    "world.*.event.economy",
)
# Durable Consumer 名（consumer_id）。inbox_dedup のキーにも使う（08B 3.1）。
CONSUMER_ID = "worldstate-projection"
# 投影対象カテゴリ。economy は actor_id が payload に明示された場合のみ投影する
# （aggregate_id は Buyer 等 Actor でない集約を指し得るため、フォールバックさせない）。
PROJECTED_CATEGORIES: frozenset[str] = frozenset({"actor", "economy"})
# JetStream ストリーム名（api 側 outbox publisher と一致, WORLD_EVENTS）。
STREAM = "WORLD_EVENTS"
# AI 判断要求の subject（14.3）。DS→worldstate。core NATS（JetStream ではない）。
DECISION_REQUEST_SUBJECT = "ai.decision.request"


class Dedup(Protocol):
    """At-least-once 重複吸収の抽象。実装は Postgres（本番）または in-memory（テスト）。"""

    async def record(self, consumer_id: str, message_id: str) -> bool:
        """新規なら記録して True、既処理（重複）なら False を返す。"""
        ...


def message_id_of(envelope: dict[str, Any]) -> str:
    """dedup キーを決める。event_id（ULID）を第一に、無ければ world+sequence で合成する。"""
    event_id = envelope.get("event_id")
    if isinstance(event_id, str) and event_id:
        return event_id
    world = envelope.get("world_id", "")
    seq = envelope.get("sequence", "")
    return f"{world}:{seq}"


def category_of(subject: str) -> str:
    """subject（world.{id}.event.{category}）から末尾のカテゴリを取り出す。不明なら空文字。"""
    parts = subject.split(".") if subject else []
    return parts[-1] if parts else ""


def _observe_event_lag(envelope: dict[str, Any]) -> None:
    """DS が事象を起こしてから Consumer が処理するまでの遅れを記録する（第13章 イベント Lag）。

    occurred_at_unix_ms は DS 側の時計なので、DS とこのプロセスの時計がずれていると
    負値になり得る。負の Lag を Gauge へ載せると劣化を見落とすので捨てる。
    """
    occurred_ms = envelope.get("occurred_at_unix_ms")
    if not isinstance(occurred_ms, int | float) or occurred_ms <= 0:
        return
    lag = time.time() - (occurred_ms / 1000.0)
    if lag >= 0:
        EVENT_LAG_SECONDS.set(lag)


async def handle_message(
    data: bytes,
    dedup: Dedup,
    consumer_id: str = CONSUMER_ID,
    projection: ProjectionStore | None = None,
    category: str | None = None,
) -> bool:
    """1 メッセージを処理する。新規に受理したら True、重複でスキップなら False。

    envelope を解釈し、inbox_dedup で冪等記録する。新規かつ投影対象カテゴリ（actor/economy）で
    projection が与えられていれば ``actor_state_projections`` を投影する（二重適用は dedup で
    防ぐ, 3.1）。resource は dedup+ログのみ。
    """
    try:
        envelope = json.loads(data)
    except (ValueError, TypeError):
        EVENTS_PROCESSED.labels(result="malformed").inc()
        logger.warning("drop malformed message", extra={"bytes": len(data)})
        return False
    if not isinstance(envelope, dict):
        EVENTS_PROCESSED.labels(result="malformed").inc()
        logger.warning("drop non-object message")
        return False

    message_id = message_id_of(envelope)
    is_new = await dedup.record(consumer_id, message_id)
    event_type = envelope.get("type", "?")
    world_id = envelope.get("world_id", "?")
    fields = {"message_id": message_id, "event_type": event_type, "world_id": world_id}

    if not is_new:
        # 重複は異常ではない。NATS/DS 再起動後は同じ event_id が必ず再配送される
        # （10B 6章）。ここで一度だけに絞れているかを系列で見える化する。
        EVENTS_PROCESSED.labels(result="duplicate").inc()
        logger.info("duplicate event deduped", extra=fields)
        return False

    _observe_event_lag(envelope)

    if projection is not None and category in PROJECTED_CATEGORIES:
        try:
            started = time.perf_counter()
            version = await projection.apply(
                envelope, allow_aggregate_fallback=(category == "actor")
            )
            PROJECTION_SECONDS.observe(time.perf_counter() - started)
            if version is not None:
                EVENTS_PROCESSED.labels(result="projected").inc()
                logger.info("projected event", extra={**fields, "version": version})
                return True
        except Exception:  # pragma: no cover - defensive; redelivery/next event recovers
            EVENTS_PROCESSED.labels(result="failed").inc()
            logger.exception("projection apply failed", extra=fields)

    EVENTS_PROCESSED.labels(result="received").inc()
    logger.info("received event", extra=fields)
    return True


def build_requested(request: dict[str, Any], templates: list[dict[str, Any]]) -> dict[str, Any]:
    """DecisionRequest から requested レコードの材料（decision_id/actor/version/候補）を作る。

    純関数（DB 非依存）。personal_state_version は proto の state_versions マップ、無ければ
    state_version フィールドから解決する（B.1 マップ・07A 3.4）。
    """
    actor_id = str(request.get("actor_id", "unknown"))
    state_version = _personal_state_version(request)
    reason = str(request.get("reason", ""))
    candidates = select_candidates(templates, reason)
    return {
        "decision_id": decision_id_of(actor_id, state_version),
        "actor_id": actor_id,
        "state_version": state_version,
        "candidates": candidates,
    }


def _personal_state_version(request: dict[str, Any]) -> int:
    versions = request.get("state_versions")
    if isinstance(versions, dict) and versions:
        if "personal_state" in versions:
            return int(versions["personal_state"])
        return int(next(iter(versions.values())))
    return int(request.get("state_version", 0) or 0)


class InMemoryDedup:
    """テスト用の in-memory Dedup。プロセス内 set で重複を判定する。"""

    def __init__(self) -> None:
        self._seen: set[tuple[str, str]] = set()

    async def record(self, consumer_id: str, message_id: str) -> bool:
        key = (consumer_id, message_id)
        if key in self._seen:
            return False
        self._seen.add(key)
        return True


class PgDedup:
    """Postgres 版 Dedup。inbox_dedup へ INSERT ON CONFLICT DO NOTHING し、行が入れば新規。"""

    def __init__(self, pool: Any) -> None:
        self._pool = pool

    async def record(self, consumer_id: str, message_id: str) -> bool:
        row = await self._pool.fetchval(
            """
            INSERT INTO inbox_dedup (consumer_id, message_id)
            VALUES ($1, $2)
            ON CONFLICT (consumer_id, message_id) DO NOTHING
            RETURNING 1
            """,
            consumer_id,
            message_id,
        )
        return row is not None


class Consumer:
    """JetStream の購読。start() で Durable Consumer を張り、メッセージを handler に流す。

    actor イベントは projection（ProjectionStore）へ投影し、resource は dedup+ログのみ。
    """

    def __init__(
        self,
        nc: Any,
        dedup: Dedup,
        consumer_id: str = CONSUMER_ID,
        projection: ProjectionStore | None = None,
    ) -> None:
        self._nc = nc
        self._dedup = dedup
        self._consumer_id = consumer_id
        self._projection = projection
        self._subs: list[Any] = []

    async def start(self) -> None:
        """JetStream に Durable な push 購読を張る。R7: handler は軽量に保つ。

        api 側が WORLD_EVENTS ストリームを作るが、起動順の競合で未作成の一瞬があり得るため、
        購読前にストリームの存在を待つ（無ければ購読土台として自ら作成する）。"""
        if self._nc is None:
            logger.info("consumer disabled (no NATS)")
            return
        js = self._nc.jetstream()
        await self._ensure_stream(js)
        for i, subject in enumerate(SUBJECTS):
            durable = f"{self._consumer_id}-{i}"
            category = category_of(subject)

            async def _cb(msg: Any, _category: str = category) -> None:
                try:
                    await handle_message(
                        msg.data,
                        self._dedup,
                        self._consumer_id,
                        projection=self._projection,
                        category=_category,
                    )
                    await msg.ack()
                except Exception:  # pragma: no cover - defensive; redelivery handles it
                    logger.exception("handler error; leaving message for redelivery")

            sub = await js.subscribe(subject, durable=durable, stream=STREAM, cb=_cb)
            self._subs.append(sub)
            logger.info("subscribed", extra={"subject": subject, "durable": durable})

    async def _ensure_stream(self, js: Any) -> None:
        """WORLD_EVENTS ストリームの存在を確認し、無ければ作成する（api と同一設定）。"""
        try:
            await js.stream_info(STREAM)
            return
        except Exception:
            pass
        try:
            await js.add_stream(name=STREAM, subjects=["world.>"])
            logger.info("created stream", extra={"stream": STREAM})
        except Exception:
            # api が並行して作成済みなら重複作成で例外になる — 購読は継続できるので握る。
            logger.info("stream ensure raced (already exists)", extra={"stream": STREAM})

    async def stop(self) -> None:
        for sub in self._subs:
            try:
                await sub.unsubscribe()
            except Exception:  # pragma: no cover
                pass
        self._subs.clear()


class DecisionRequestConsumer:
    """``ai.decision.request`` を購読し候補 Template を絞って requested を記録する（3.4）。

    DS→worldstate は core NATS（JetStream ではない・request/response 経路）。テンプレ集合は
    起動時にスナップショットして in-memory 選択（R7: handler は軽量）。**LLM 本体は M5**。
    """

    def __init__(self, nc: Any, templates: TemplateRepo, decisions: DecisionStore) -> None:
        self._nc = nc
        self._templates = templates
        self._decisions = decisions
        self._active: list[dict[str, Any]] = []
        self._sub: Any = None

    async def start(self) -> None:
        if self._nc is None:
            logger.info("decision request consumer disabled (no NATS)")
            return
        try:
            self._active = await self._templates.list_active()
        except Exception:  # pragma: no cover - infra dependent
            logger.exception("could not load active templates; using empty set")
            self._active = []
        logger.info("loaded active templates for candidates", extra={"count": len(self._active)})

        async def _cb(msg: Any) -> None:
            await self._on_request(msg.data)

        self._sub = await self._nc.subscribe(DECISION_REQUEST_SUBJECT, cb=_cb)
        logger.info("subscribed", extra={"subject": DECISION_REQUEST_SUBJECT})

    async def _on_request(self, data: bytes) -> None:
        try:
            request = json.loads(data)
        except (ValueError, TypeError):
            DECISION_REQUESTS.labels(result="malformed").inc()
            logger.warning("drop malformed decision request")
            return
        if not isinstance(request, dict):
            DECISION_REQUESTS.labels(result="malformed").inc()
            return
        rec = build_requested(request, self._active)
        try:
            await self._decisions.record_requested(
                rec["decision_id"], rec["actor_id"], rec["state_version"], rec["candidates"]
            )
            DECISION_REQUESTS.labels(result="requested").inc()
            logger.info(
                "decision requested",
                extra={
                    "decision_id": rec["decision_id"],
                    "actor_id": rec["actor_id"],
                    "candidates": len(rec["candidates"]),
                },
            )
        except Exception:  # pragma: no cover - infra dependent
            DECISION_REQUESTS.labels(result="failed").inc()
            logger.exception("could not record requested decision")

    async def stop(self) -> None:
        if self._sub is not None:
            try:
                await self._sub.unsubscribe()
            except Exception:  # pragma: no cover
                pass
            self._sub = None
