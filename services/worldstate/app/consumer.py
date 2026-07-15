"""WorldState NATS 購読土台（M3 / 06B 3.3）。

`world.*.event.resource` と `world.*.event.actor` を JetStream の Durable Consumer で
購読し、At-least-once の重複を `inbox_dedup(consumer_id, message_id)` で吸収して受信ログを
出すところまでを担う（投影本体は M5）。R7 に従い handler には重い処理を置かない。

nats / asyncpg は import 時にオプショナル扱いにして、インフラが無くても
`import app.consumer` とユニットテストが通るようにする（app.main と同じ方針）。
"""

from __future__ import annotations

import json
import logging
import os
from typing import Any, Protocol

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]

try:  # pragma: no cover - import guard
    import asyncpg
except Exception:  # pragma: no cover
    asyncpg = None  # type: ignore[assignment]

logger = logging.getLogger("worldstate.consumer")

# 購読 subject（14.3）。resource と actor の 2 系統を購読土台として受ける。
SUBJECTS: tuple[str, ...] = ("world.*.event.resource", "world.*.event.actor")
# Durable Consumer 名（consumer_id）。inbox_dedup のキーにも使う。
CONSUMER_ID = "worldstate"
# JetStream ストリーム名（api 側 outbox publisher と一致, WORLD_EVENTS）。
STREAM = "WORLD_EVENTS"


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


async def handle_message(data: bytes, dedup: Dedup, consumer_id: str = CONSUMER_ID) -> bool:
    """1 メッセージを処理する。新規に受理したら True、重複でスキップなら False。

    投影は M5。ここでは envelope を解釈し、inbox_dedup で冪等記録＋ログのみ行う。
    """
    try:
        envelope = json.loads(data)
    except (ValueError, TypeError):
        logger.warning("worldstate: drop malformed message (%d bytes)", len(data))
        return False
    if not isinstance(envelope, dict):
        logger.warning("worldstate: drop non-object message")
        return False

    message_id = message_id_of(envelope)
    is_new = await dedup.record(consumer_id, message_id)
    event_type = envelope.get("type", "?")
    world_id = envelope.get("world_id", "?")
    if not is_new:
        logger.info(
            "worldstate: duplicate %s type=%s world=%s (deduped)",
            message_id,
            event_type,
            world_id,
        )
        return False
    logger.info("worldstate: received %s type=%s world=%s", message_id, event_type, world_id)
    return True


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
    """JetStream の購読土台。start() で Durable Consumer を張り、メッセージを handler に流す。"""

    def __init__(self, nc: Any, dedup: Dedup, consumer_id: str = CONSUMER_ID) -> None:
        self._nc = nc
        self._dedup = dedup
        self._consumer_id = consumer_id
        self._subs: list[Any] = []

    async def start(self) -> None:
        """JetStream に Durable な push 購読を張る。R7: handler は軽量に保つ。

        api 側が WORLD_EVENTS ストリームを作るが、起動順の競合で未作成の一瞬があり得るため、
        購読前にストリームの存在を待つ（無ければ購読土台として自ら作成する）。"""
        if self._nc is None:
            logger.info("worldstate: consumer disabled (no NATS)")
            return
        js = self._nc.jetstream()
        await self._ensure_stream(js)
        for i, subject in enumerate(SUBJECTS):
            durable = f"{self._consumer_id}-{i}"

            async def _cb(msg: Any) -> None:
                try:
                    await handle_message(msg.data, self._dedup, self._consumer_id)
                    await msg.ack()
                except Exception:  # pragma: no cover - defensive; redelivery handles it
                    logger.exception("worldstate: handler error; leaving message for redelivery")

            sub = await js.subscribe(subject, durable=durable, stream=STREAM, cb=_cb)
            self._subs.append(sub)
            logger.info("worldstate: subscribed %s (durable=%s)", subject, durable)

    async def _ensure_stream(self, js: Any) -> None:
        """WORLD_EVENTS ストリームの存在を確認し、無ければ作成する（api と同一設定）。"""
        try:
            await js.stream_info(STREAM)
            return
        except Exception:
            pass
        try:
            await js.add_stream(name=STREAM, subjects=["world.>"])
            logger.info("worldstate: created stream %s", STREAM)
        except Exception:
            # api が並行して作成済みなら重複作成で例外になる — 購読は継続できるので握る。
            logger.info("worldstate: stream %s ensure raced (already exists)", STREAM)

    async def stop(self) -> None:
        for sub in self._subs:
            try:
                await sub.unsubscribe()
            except Exception:  # pragma: no cover
                pass
        self._subs.clear()


async def build_pg_dedup() -> PgDedup | None:
    """DATABASE_URL から asyncpg プールを張り PgDedup を返す。失敗時 None（購読土台は無効）。"""
    if asyncpg is None:
        return None
    url = os.getenv("DATABASE_URL")
    if not url:
        return None
    # asyncpg は libpq DSN の sslmode 等を解釈しないため postgres:// はそのまま渡す。
    try:
        pool = await asyncpg.create_pool(dsn=url, min_size=1, max_size=4)
    except Exception:  # pragma: no cover - infra dependent
        logger.warning("worldstate: could not connect Postgres for dedup", exc_info=True)
        return None
    return PgDedup(pool)
