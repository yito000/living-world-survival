"""Anthropic API クライアント（08B 3.7）。

意思決定は Tick を待たせない低レイテンシが要件（10.2）なので、既定は
``output_config={"effort": "low"}``・``max_tokens=1024``・短い timeout（8 秒）で構成する。
thinking は既定オフ（省略）— 品質不足が確認できてから ``{"type": "adaptive"}`` を検討する。

R7: LLM 呼び出しは NATS consumer から起動する専用タスクで行い、FastAPI の request handler
では絶対に完結させない（worldstate の HTTP は health/readyz のみ）。

安全側の既定:

- ``LLM_MOCK=1`` または ``ANTHROPIC_API_KEY`` 未設定なら**モックへフォールバック**して警告する
  （テスト/オフライン開発・CI は LLM_MOCK=1 が既定）。
- ``stop_reason == "refusal"`` を ``content`` を読む**前に**判定する（拒否時は Decision を破棄
  → DS は現行行動継続 → Utility Fallback）。
- タイムアウト時は例外にして**結果を発行しない**（16章 / 08A 3.4）。
- ``response.usage`` を構造化ログへ出し、``ai_decisions.payload`` にトークン数を残す。

プロンプトに個人認証情報は入れない（17章 MVP-SEC-008）。入力は投影の短い要約と候補 ID のみ。
"""

from __future__ import annotations

import logging
import os
from typing import Any, TypeVar

from pydantic import BaseModel

from worker.schemas import ActionDecisionOutput, EventProposalOutput

try:  # pragma: no cover - import guard（LLM_MOCK 経路は SDK 非依存で動く）
    import anthropic
except Exception:  # pragma: no cover
    anthropic = None  # type: ignore[assignment]

logger = logging.getLogger("llm-worker.llm")

# モデル既定（3.7）。完全形の ID をそのまま使う（日付サフィックスを付けると 404・落とし穴5）。
DEFAULT_MODEL = "claude-opus-4-8"
# 意思決定/提案は小さい構造化出力。大きく取るとコスト暴走（落とし穴3）。
DEFAULT_MAX_TOKENS = 1024
# 既定 10 分は意思決定には長すぎる。超えたら結果を出さず DS の Fallback に委ねる。
DEFAULT_TIMEOUT_SEC = 8.0
# SDK が 429/5xx/接続エラーを指数バックオフで自動リトライする回数（既定 2 を引き上げ）。
# 自前で ai.decision.request を再発行しない（多重実行になる・落とし穴4）。
DEFAULT_MAX_RETRIES = 3
# effort 未設定の既定は high で遅い（落とし穴2）。短い意思決定は low から始める。
DEFAULT_EFFORT = "low"

M = TypeVar("M", bound=BaseModel)


class LLMError(RuntimeError):
    """LLM 呼び出しが結果を出せなかった（タイムアウト/接続/検証失敗など）。"""


class DecisionRefused(LLMError):
    """安全分類による拒否（stop_reason=refusal）。Decision を破棄し Fallback へ委ねる。"""


def _env_float(key: str, fallback: float) -> float:
    try:
        return float(os.environ[key])
    except (KeyError, ValueError):
        return fallback


def _env_int(key: str, fallback: int) -> int:
    try:
        return int(os.environ[key])
    except (KeyError, ValueError):
        return fallback


def use_mock() -> bool:
    """モック経路を使うか。``LLM_MOCK`` 明示が最優先、次に API キーの有無で判定する（3.7）。"""
    flag = os.getenv("LLM_MOCK", "").strip().lower()
    if flag in ("1", "true", "yes"):
        return True
    if flag in ("0", "false", "no"):
        return False
    if not os.getenv("ANTHROPIC_API_KEY", "").strip():
        # 安全側フォールバック: キーが無い環境で実 API を叩きにいって落ちるより、
        # 決定的なモックで成立させ、警告でオペレータに気づかせる。
        logger.warning(
            "ANTHROPIC_API_KEY 未設定のためモック LLM で動作します "
            "(実 LLM を使うにはキーを設定するか LLM_MOCK=0 を明示してください)"
        )
        return True
    return False


def log_usage(kind: str, usage: Any) -> dict[str, int]:
    """``response.usage`` を構造化ログへ出し、記録用の dict にして返す（3.7 コスト/トークン）。

    コスト目安は claude-opus-4-8（入力 $5 / 出力 $25 per 1M tokens）。同一 Projection への
    繰り返し呼び出しは prompt caching でプレフィックスを共有すれば入力コストを削減できる。
    """
    fields = (
        "input_tokens",
        "output_tokens",
        "cache_read_input_tokens",
        "cache_creation_input_tokens",
    )
    out: dict[str, int] = {}
    for field in fields:
        value = getattr(usage, field, None)
        if isinstance(value, int):
            out[field] = value
    # トークン数は個別フィールドとして出す（JSON ログ側で集計・アラートに使えるように）。
    logger.info("llm usage", extra={"kind": kind, **out})
    return out


class LLMClient:
    """構造化出力で ActionDecision / EventProposal を生成する Async クライアント。

    ``messages.parse()`` + Pydantic（``worker.schemas``）で型安全に受け取る。SDK が
    JSON Schema を導出し、構造化出力が保証しない制約はクライアント側で検証してくれるので、
    生スキーマを手で渡すより安全（3.3）。
    """

    def __init__(self) -> None:
        if anthropic is None:  # pragma: no cover - import guard
            raise LLMError("anthropic SDK が導入されていません（LLM_MOCK=1 で回避できます）")
        self.model = os.getenv("LLM_MODEL", DEFAULT_MODEL)
        self.effort = os.getenv("LLM_EFFORT", DEFAULT_EFFORT)
        self.max_tokens = _env_int("LLM_MAX_TOKENS", DEFAULT_MAX_TOKENS)
        # timeout / max_retries はクライアントに持たせる。SDK が 429/5xx を指数 Backoff で
        # 自動リトライし、超過時は APITimeoutError を投げる（= 結果を発行しない）。
        self._client = anthropic.AsyncAnthropic(
            timeout=_env_float("LLM_TIMEOUT_SEC", DEFAULT_TIMEOUT_SEC),
            max_retries=_env_int("LLM_MAX_RETRIES", DEFAULT_MAX_RETRIES),
        )
        self.last_usage: dict[str, int] = {}

    async def _parse(self, kind: str, prompt: str, system: str, output_format: type[M]) -> M:
        """1 回の構造化出力呼び出し。refusal を content より先に判定し、usage を記録する。"""
        try:
            resp = await self._client.messages.parse(
                model=self.model,
                max_tokens=self.max_tokens,
                output_config={"effort": self.effort},
                output_format=output_format,
                system=system,
                messages=[{"role": "user", "content": prompt}],
            )
        except Exception as exc:  # タイムアウト/接続/レート超過（リトライ後）
            raise LLMError(f"{kind}: LLM call failed: {exc}") from exc

        # refusal は content を読む前に判定する（落とし穴6）。
        if getattr(resp, "stop_reason", None) == "refusal":
            self.last_usage = log_usage(kind, getattr(resp, "usage", None))
            raise DecisionRefused(f"{kind}: refused by safety classifier")
        self.last_usage = log_usage(kind, getattr(resp, "usage", None))

        parsed = getattr(resp, "parsed_output", None)
        if parsed is None:
            # max_tokens 打ち切り等で出力が不完全だと parse できない。未検証の出力は使わない。
            raise LLMError(f"{kind}: structured output missing (stop_reason={resp.stop_reason})")
        return parsed

    async def decide(self, candidates: list[str], state_summary: str) -> ActionDecisionOutput:
        """候補テンプレと状態要約から ActionDecision（選択部分のみ）を生成する（10.2 手順3）。"""
        return await self._parse(
            "decision",
            build_decision_prompt(candidates, state_summary),
            DECISION_SYSTEM,
            ActionDecisionOutput,
        )

    async def propose_event(self, candidates: list[str], world_summary: str) -> EventProposalOutput:
        """World Summary から EventProposal（選択部分のみ）を生成する（10.3）。"""
        return await self._parse(
            "event_proposal",
            build_proposal_prompt(candidates, world_summary),
            PROPOSAL_SYSTEM,
            EventProposalOutput,
        )


DECISION_SYSTEM = (
    "You choose the next action for an autonomous NPC in a survival simulation. "
    "Choose exactly one template_id from the candidate list you are given — never invent one. "
    "Each step's action_template_id must be a primitive action named by the chosen template. "
    "Do not output ids, versions, timestamps, coordinates, or quantities: the server sets those. "
    "Prefer the candidate that best addresses the actor's most urgent need."
)

PROPOSAL_SYSTEM = (
    "You propose world events for a survival simulation. "
    "Choose exactly one event_template_id from the candidate list you are given. "
    "Propose only the template, region tags, reason tags, intensity (0..1), and start window. "
    "Never propose spawn counts, supply budgets, or coordinates: the rule engine decides those."
)


def build_decision_prompt(candidates: list[str], state_summary: str) -> str:
    """判断プロンプト。候補 ID と短い状態要約・制約のみを渡す（個人情報は入れない）。"""
    listed = "\n".join(f"- {c}" for c in candidates)
    return (
        f"Actor state summary:\n{state_summary}\n\n"
        f"Candidate action templates (choose exactly one):\n{listed}\n\n"
        "Return the chosen template_id, its steps (each naming a primitive action), "
        "any string parameters, and a one-line reason."
    )


def build_proposal_prompt(candidates: list[str], world_summary: str) -> str:
    """提案プロンプト。World Summary と候補イベントテンプレのみを渡す。"""
    listed = "\n".join(f"- {c}" for c in candidates)
    return (
        f"World summary:\n{world_summary}\n\n"
        f"Candidate world event templates (choose exactly one):\n{listed}\n\n"
        "Return the chosen event_template_id, region_tags, reason_tags, "
        "requested_intensity between 0 and 1, and an optional start window in seconds."
    )
