"""World Event Director（08B 3.4 / MVP 8.2・10.3）。

``worldevent.evaluation.request`` を購読し、候補テンプレ（10.3 の 3 種）をルールで絞ってから
LLM の構造化出力で ``EventProposal`` を生成し、``worldevent.proposal.{server_id}`` へ発行する。

配置の理由（3.4 の注）: 評価要求の受信は worldstate にも置けるが、**LLM 呼び出しを llm-worker に
集約**して LLM クライアントの重複を避ける方針を採り、Decision も Event も本サービスで呼ぶ。

LLM が決めるのは**テンプレ / 地域タグ / 理由タグ / 強度 / 開始 Window だけ**。具体スポーン数・
供給予算・座標は決めさせない（基本設計 8.2）— 実値はルールエンジンと DS が決める。承認検査
（同一 Region 競合・同種 Cooldown・Template Version）は api 側（3.6）が DB を見て行う。
"""

from __future__ import annotations

import json
import logging
import secrets
import time
from typing import Any

from worker.candidates import select_event_candidates, world_summary
from worker.llm import LLMError
from worker.schemas import EventProposalOutput, validate_proposal

logger = logging.getLogger("llm-worker.event_director")

# 購読/発行 subject（14.3）。
EVALUATION_REQUEST_SUBJECT = "worldevent.evaluation.request"
PROPOSAL_SUBJECT_PREFIX = "worldevent.proposal"


def proposal_id_of() -> str:
    """提案 id。時刻順に並ぶ 26 文字（ULID 相当の語彙）で衝突しない値を Worker が確定する。

    Decision と違い提案は再試行で同一性を要求されない（同じ評価窓の重複提案は api 側の
    同一 Region 競合検査が弾く）ので、ランダム成分で十分。
    """
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"  # Crockford base32（ULID と同じ）
    ms = int(time.time() * 1000)
    head = ""
    for _ in range(10):  # 48bit 時刻部を 10 文字で表す
        ms, rem = divmod(ms, 32)
        head = alphabet[rem] + head
    tail = "".join(secrets.choice(alphabet) for _ in range(16))
    return head + tail


def proposal_subject(request: dict[str, Any]) -> str:
    """提案の宛先 subject を評価要求のコンテキストから解決する（3.4）。"""
    server_id = request.get("server_id") or request.get("world_id") or "unknown"
    return f"{PROPOSAL_SUBJECT_PREFIX}.{server_id}"


def build_mock_proposal(request: dict[str, Any], candidates: list[str]) -> EventProposalOutput:
    """決定的なモック提案（``LLM_MOCK=1`` / オフライン用）。reason から 1 テンプレを選ぶ。

    実 LLM 経路と同じ ``EventProposalOutput`` を返すので、後段（検証→proto マップ→発行）は
    モックでも実 LLM でも完全に同じコードを通る。
    """
    reason = str(request.get("reason", "")).strip().lower()
    picks = (
        ("hunt", "world_event.great_hunt"),
        ("deer", "world_event.great_hunt"),
        ("resource", "world_event.rare_resource"),
        ("ore", "world_event.rare_resource"),
        ("buyer", "world_event.rare_buyer_rush"),
        ("economy", "world_event.rare_buyer_rush"),
    )
    chosen = ""
    for key, template_id in picks:
        if key in reason and template_id in candidates:
            chosen = template_id
            break
    if not chosen:
        chosen = candidates[0] if candidates else "world_event.great_hunt"
    return EventProposalOutput(
        event_template_id=chosen,  # type: ignore[arg-type]
        region_id=str(request.get("region_id") or "") or None,
        region_tags=["default"],
        reason_tags=[reason or "periodic"],
        requested_intensity=0.5,
        start_after_sec=0,
        start_before_sec=300,
    )


def build_proposal(
    request: dict[str, Any], output: EventProposalOutput, *, proposal_id: str | None = None
) -> dict[str, Any]:
    """検証済み構造化出力を proto ``EventProposal`` 形へマップする（3.3）。

    ``proposal_id``（ULID 相当）と ``world_id`` は Worker が確定する。``params`` には付録B.2 の
    JSON（region_tags / reason_tags / requested_intensity / start_after_sec / start_before_sec）を
    入れる。``score`` は ``requested_intensity``（ルール評価値）を入れる。
    """
    validate_proposal(output)
    params = {
        "region_tags": list(output.region_tags),
        "reason_tags": list(output.reason_tags),
        "requested_intensity": output.requested_intensity,
        "start_after_sec": output.start_after_sec,
        "start_before_sec": output.start_before_sec,
    }
    return {
        "proposal_id": proposal_id or proposal_id_of(),
        "template_id": output.event_template_id,
        "world_id": str(request.get("world_id", "")),
        "region_id": output.region_id or str(request.get("region_id") or ""),
        # proto は bytes(JSON)。JSON 文字列で運び、api 側が ::jsonb として保存する。
        "params": params,
        "score": output.requested_intensity,
    }


class EventDirector:
    """``worldevent.evaluation.request`` を購読し EventProposal を発行する（3.4）。

    NATS のコールバックから起動する専用タスクで LLM を呼ぶ（R7: コールバック自体は軽く保つ）。
    LLM が結果を出せなければ**提案を発行しない** — 次の評価まで待つのが正しい（10.4: 拒否時に
    LLM へ自由な代替を再生成させない、と同じ原則）。
    """

    def __init__(self, nc: Any, repo: Any, client: Any, mock: bool) -> None:
        self._nc = nc
        self._repo = repo
        self._client = client
        self._mock = mock
        self._sub: Any = None

    async def start(self) -> None:
        if self._nc is None:  # pragma: no cover - infra guard
            logger.info("llm-worker: event director disabled (no NATS)")
            return

        async def _cb(msg: Any) -> None:
            await self.on_request(msg.data)

        self._sub = await self._nc.subscribe(EVALUATION_REQUEST_SUBJECT, cb=_cb)
        logger.info("llm-worker: subscribed %s", EVALUATION_REQUEST_SUBJECT)

    async def stop(self) -> None:
        if self._sub is not None:
            try:
                await self._sub.unsubscribe()
            except Exception:  # pragma: no cover
                pass
            self._sub = None

    async def on_request(self, data: bytes) -> dict[str, Any] | None:
        """1 評価要求を処理し、発行した提案（あれば）を返す。テスト用に戻り値を持つ。"""
        try:
            request = json.loads(data)
        except (ValueError, TypeError):
            logger.warning("llm-worker: drop malformed evaluation request")
            return None
        if not isinstance(request, dict):
            return None

        candidates = select_event_candidates(getattr(self._repo, "templates", []) or [])
        if not candidates:
            logger.warning("llm-worker: no world event templates available; skipping evaluation")
            return None

        try:
            if self._mock or self._client is None:
                output = build_mock_proposal(request, candidates)
            else:
                summary = world_summary(0, [])
                output = await self._client.propose_event(candidates, summary)
            proposal = build_proposal(request, output)
        except LLMError as exc:
            # タイムアウト/拒否: 提案せず次の評価窓を待つ（結果を発行しないのが正しい）。
            logger.warning("llm-worker: event proposal skipped: %s", exc)
            return None
        except ValueError as exc:
            # Allowed ID / 値域の逸脱は破棄する（未検証の提案を api へ渡さない）。
            logger.warning("llm-worker: event proposal rejected by validation: %s", exc)
            return None

        subject = proposal_subject(request)
        await self._nc.publish(subject, json.dumps(proposal).encode())
        logger.info(
            "llm-worker: proposed %s (%s) intensity=%.2f -> %s",
            proposal["proposal_id"],
            proposal["template_id"],
            proposal["score"],
            subject,
        )
        return proposal
