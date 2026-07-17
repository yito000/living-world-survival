"""投影の replay 再構築（08B 3.1 / L-4）。

``actor_state_projections`` はイベントを正とする派生データなので、いつでも捨てて
``world.{id}.event.*`` の先頭から作り直せる（13.1 / 8.1）。本モジュールは JetStream の
``WORLD_EVENTS`` ストリームを DeliverPolicy=all の一時 Consumer で頭から読み、逐次適用と
同じ順序で投影を再構成する。``projection_version`` は replay 順で 0 から再採番される
（投影は履歴ではなく現在値なので、逐次適用時と同じ最終状態になる）。

運用/テストから使う:

    python -m app.rebuild            # 全 world を再構築
    python -m app.rebuild <world_id> # 1 world のみ再構築

replay は常駐 Consumer（app.consumer）とは別の一時 Consumer を使うので、durable の
配信位置や ``inbox_dedup`` を壊さない。dedup も通さない（replay はストリーム内で既に
一意な履歴であり、ここで弾くと再構築できなくなる）。
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import sys
from typing import Any

from app.consumer import PROJECTED_CATEGORIES, STREAM, category_of
from app.repo import ProjectionStore, create_pool

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]

logger = logging.getLogger("worldstate.rebuild")

# replay の 1 回あたり取得件数と、空振り時の打ち切り待ち時間（秒）。
BATCH = 100
IDLE_TIMEOUT = 2.0


def subject_for(world_id: str | None) -> str:
    """再構築対象の subject を返す。world_id 省略時は全 world をワイルドカードで読む。"""
    return f"world.{world_id or '*'}.event.*"


async def apply_events(
    events: list[tuple[str, dict[str, Any]]],
    projection: ProjectionStore,
) -> int:
    """(subject, envelope) の並びを順に投影へ適用し、適用できた件数を返す。

    純粋な適用ループ（NATS 非依存）なので、逐次適用と replay が同一結果になることを
    ユニットテストで突き合わせられる。カテゴリ別の扱いは常駐 Consumer と同じ規則
    （actor は aggregate_id フォールバックあり、economy は payload.actor_id のみ、
    resource は投影しない）。
    """
    applied = 0
    for subject, envelope in events:
        category = category_of(subject)
        if category not in PROJECTED_CATEGORIES:
            continue
        version = await projection.apply(envelope, allow_aggregate_fallback=(category == "actor"))
        if version is not None:
            applied += 1
    return applied


async def fetch_history(nc: Any, subject: str) -> list[tuple[str, dict[str, Any]]]:
    """JetStream から subject の履歴を先頭から全部読み、(subject, envelope) で返す。

    一時（非 durable）pull Consumer を DeliverPolicy=all で張る。取得が空振りしたら
    末尾に達したとみなして終了する。
    """
    js = nc.jetstream()
    sub = await js.pull_subscribe(subject, durable=None, stream=STREAM)
    out: list[tuple[str, dict[str, Any]]] = []
    while True:
        try:
            msgs = await sub.fetch(BATCH, timeout=IDLE_TIMEOUT)
        except Exception:
            # fetch のタイムアウト = 末尾に到達（nats-py は TimeoutError を投げる）。
            break
        if not msgs:
            break
        for msg in msgs:
            try:
                envelope = json.loads(msg.data)
            except (ValueError, TypeError):
                logger.warning("rebuild: skip malformed message on %s", msg.subject)
                await msg.ack()
                continue
            if isinstance(envelope, dict):
                out.append((msg.subject, envelope))
            await msg.ack()
    await sub.unsubscribe()
    return out


async def rebuild(world_id: str | None = None) -> int:
    """投影を捨てて replay で作り直す。適用したイベント件数を返す。

    NATS / Postgres が使えない環境では 0 を返して何もしない（CI をインフラ非依存に保つ）。
    """
    if nats is None:
        logger.error("rebuild: nats-py が無いので何もしません")
        return 0
    pool = await create_pool()
    if pool is None:
        logger.error("rebuild: DATABASE_URL 未設定/接続不可なので何もしません")
        return 0

    url = os.getenv("NATS_URL", "nats://localhost:4222")
    nc = await nats.connect(url, connect_timeout=2, max_reconnect_attempts=0)
    try:
        projection = ProjectionStore(pool)
        # 先に投影を捨てる。イベントが正なので、途中で落ちても再実行すれば戻る。
        deleted = await projection.reset(world_id)
        logger.info("rebuild: cleared %d projection rows (world=%s)", deleted, world_id or "*")

        events = await fetch_history(nc, subject_for(world_id))
        applied = await apply_events(events, projection)
        logger.info("rebuild: replayed %d events, applied %d", len(events), applied)
        return applied
    finally:
        await nc.drain()
        await pool.close()


def main() -> None:
    logging.basicConfig(level=logging.INFO)
    world_id = sys.argv[1] if len(sys.argv) > 1 else None
    applied = asyncio.run(rebuild(world_id))
    print(f"rebuild: applied {applied} events")  # noqa: T201


if __name__ == "__main__":
    main()
