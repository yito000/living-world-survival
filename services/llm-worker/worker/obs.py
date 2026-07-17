"""llm-worker の可観測性（JSON 構造化ログ / Prometheus metrics）。

10B 3.5 / 基本設計第13章。worldstate の app/obs.py と同じ方針を持つ（両者は別パッケージで
共有 Python パッケージが無いため、重複は許容する）:

- ログは JSON 1 行 1 レコード。相関フィールド（world_id/server_id/account_id/actor_id/
  correlation_id）を extra で載せる。
- Password/Token/APIKey の類はキー名で機械的に伏せる（MVP-SEC-002）。
  プロンプト本文や ANTHROPIC_API_KEY は**そもそもログへ渡さない**（17章 MVP-SEC-008）。
- /metrics は Prometheus exposition 形式（R-PROM）。

metrics は第13章が必須指標に挙げる **LLM latency / cost** と、AT-014（LLM 停止時に DS が
Fallback で継続する）が観測可能であることを担保する。
"""

from __future__ import annotations

import json
import logging
import os
import sys
from typing import Any

from prometheus_client import CONTENT_TYPE_LATEST, Counter, Gauge, Histogram, generate_latest

# 第13章が要求する相関フィールド。worldstate / Go 側 obs.Field* と同じ名前で揃える。
CORRELATION_FIELDS = (
    "world_id",
    "server_id",
    "account_id",
    "actor_id",
    "correlation_id",
)

REDACTED = "[REDACTED]"

# 値をログへ出してはならない属性キー（MVP-SEC-002）。worldstate 側 _REDACTED_KEYS と対。
_REDACTED_KEYS = (
    "password",
    "passwd",
    "secret",
    "token",
    "refresh",
    "authorization",
    "accesstoken",
    "refreshtoken",
    "apikey",
    "signingkey",
    "privatekey",
)

# LogRecord の組み込み属性。extra で載せた値だけを拾うために除外する。
_STD_ATTRS = frozenset(
    """args asctime created exc_info exc_text filename funcName levelname levelno
    lineno module msecs message msg name pathname process processName relativeCreated
    stack_info thread threadName taskName""".split()
)


def _is_redacted_key(key: str) -> bool:
    norm = key.lower().replace("-", "").replace("_", "").replace(".", "")
    return any(k in norm for k in _REDACTED_KEYS)


class JSONFormatter(logging.Formatter):
    """1 レコード = JSON 1 行。extra で渡した任意フィールドを取り込む。"""

    def __init__(self, service: str) -> None:
        super().__init__()
        self._service = service

    def format(self, record: logging.LogRecord) -> str:
        out: dict[str, Any] = {
            "time": self.formatTime(record, "%Y-%m-%dT%H:%M:%S%z"),
            "level": record.levelname.lower(),
            "service": self._service,
            "logger": record.name,
            "msg": record.getMessage(),
        }
        for key, value in record.__dict__.items():
            if key in _STD_ATTRS or key.startswith("_"):
                continue
            out[key] = REDACTED if _is_redacted_key(key) else value
        if record.exc_info:
            out["error"] = self.formatException(record.exc_info)
        # 値に非シリアライズ可能な物が来てもログ行を落とさない。
        return json.dumps(out, default=str, ensure_ascii=False)


def setup_logging(service: str) -> None:
    """root logger を JSON 構造化ログへ差し替える（LOG_LEVEL で閾値可変）。"""
    level = getattr(logging, os.getenv("LOG_LEVEL", "INFO").strip().upper(), logging.INFO)
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JSONFormatter(service))

    root = logging.getLogger()
    # basicConfig 済みの素の handler が残っていると、同じ行が非 JSON でも出る。
    for existing in list(root.handlers):
        root.removeHandler(existing)
    root.addHandler(handler)
    root.setLevel(level)


# --- metrics（第13章 / 10B 3.5）---------------------------------------------

# LLM 判断 1 回の所要時間。3.1 の「通常 30 秒以内 / Hard timeout 60 秒」を境界に持たせ、
# ヒストグラムから閾値越えがそのまま読めるようにする。
DECISION_SECONDS = Histogram(
    "llm_decision_duration_seconds",
    "Time for one LLM decide() call.",
    buckets=(0.5, 1, 2, 5, 10, 20, 30, 45, 60, 120),
)

# 判断の結果分布（status=produced/mock/rejected）。record_decision の status 語彙と対。
# rejected の増加 = DS が Utility Fallback で走っている（AT-014）。
DECISIONS = Counter(
    "llm_decisions_total",
    "Action decisions handled, by status.",
    ["status"],
)

# トークン数（kind=input/output/cache_read/cache_creation）。第13章の Cost 指標の素データ。
TOKENS = Counter(
    "llm_tokens_total",
    "LLM tokens consumed, by kind.",
    ["kind"],
)

# 結果を出せなかった理由（reason=timeout/refusal/allowed_id/other）。
# ラベルは下の classify_error が返す固定集合のみ（生の例外文字列は絶対に入れない）。
ERRORS = Counter(
    "llm_errors_total",
    "LLM calls that produced no decision, by reason.",
    ["reason"],
)

# NATS へ繋がっているか（1=ready / 0=not ready）。/readyz と同じ判定を metrics 側にも出す。
UP = Gauge(
    "llm_up",
    "1 when the worker is connected to NATS and consuming, else 0.",
)

# usage の属性名 → llm_tokens_total の kind ラベル。
_TOKEN_KINDS = {
    "input_tokens": "input",
    "output_tokens": "output",
    "cache_read_input_tokens": "cache_read",
    "cache_creation_input_tokens": "cache_creation",
}

# 例外の型名に含まれていたら timeout 扱いにする語（SDK の APITimeoutError 等）。
_TIMEOUT_HINTS = ("timeout", "timederror")


def classify_error(exc: BaseException) -> str:
    """例外を llm_errors_total の**有限な** reason ラベルへ写す（カーディナリティ上限）。

    型名のみで判定する。例外メッセージには可変長のモデル出力やパスが混ざり得るので、
    ラベルには決して使わない。
    """
    for cause in (exc, exc.__cause__):
        if cause is None:
            continue
        name = type(cause).__name__.lower()
        if name == "decisionrefused":
            return "refusal"
        if name == "allowediderror":
            return "allowed_id"
        if any(hint in name for hint in _TIMEOUT_HINTS):
            return "timeout"
    return "other"


def observe_usage(usage: dict[str, int]) -> None:
    """log_usage が返す usage dict を llm_tokens_total へ積む（未知キーは無視）。"""
    for field, kind in _TOKEN_KINDS.items():
        value = usage.get(field)
        if isinstance(value, int) and value > 0:
            TOKENS.labels(kind=kind).inc(value)


def render_metrics() -> tuple[bytes, str]:
    """Prometheus exposition 形式の本文と Content-Type を返す（R-PROM）。"""
    return generate_latest(), CONTENT_TYPE_LATEST
