"""worldstate の可観測性（JSON 構造化ログ / Prometheus metrics）。

10B 3.5 / 基本設計第13章。Go 側 services/common/obs と同じ方針を Python で持つ:
- ログは JSON 1 行 1 レコード。相関フィールド（world_id/server_id/account_id/
  actor_id/correlation_id）を extra で載せる。
- Password/Token の類はキー名で機械的に伏せる（MVP-SEC-002）。
- /metrics は Prometheus exposition 形式（R-PROM）。
"""

from __future__ import annotations

import json
import logging
import os
import sys
from typing import Any

from prometheus_client import CONTENT_TYPE_LATEST, Counter, Gauge, Histogram, generate_latest

# 第13章が要求する相関フィールド。Go 側 obs.Field* と同じ名前で揃える。
CORRELATION_FIELDS = (
    "world_id",
    "server_id",
    "account_id",
    "actor_id",
    "correlation_id",
)

REDACTED = "[REDACTED]"

# 値をログへ出してはならない属性キー（MVP-SEC-002）。Go 側 redactedKeys と対。
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

# イベント Lag: DS が書いた事象を Consumer が処理するまでの遅れ。
EVENT_LAG_SECONDS = Gauge(
    "worldstate_event_lag_seconds",
    "Seconds between a domain event's occurred_at and its projection.",
)

# 投影件数（result=ok/failed/duplicate）。duplicate は inbox_dedup が効いた数で、
# 再起動後に必ず出る（10B 6章: 重複排除は再起動後こそ本番）。
EVENTS_PROCESSED = Counter(
    "worldstate_events_processed_total",
    "Domain events consumed, by result.",
    ["result"],
)

# 判断要求の発行数（result=published/no_candidates/failed）。
DECISION_REQUESTS = Counter(
    "worldstate_decision_requests_total",
    "ai.decision.request messages handled, by result.",
    ["result"],
)

# 投影の所要時間。DB latency の worldstate 側の見え方。
PROJECTION_SECONDS = Histogram(
    "worldstate_projection_duration_seconds",
    "Time to project one domain event.",
    buckets=(0.005, 0.01, 0.02, 0.04, 0.05, 0.1, 0.2, 0.5, 1, 2.5, 5),
)


def render_metrics() -> tuple[bytes, str]:
    """Prometheus exposition 形式の本文と Content-Type を返す（R-PROM）。"""
    return generate_latest(), CONTENT_TYPE_LATEST
