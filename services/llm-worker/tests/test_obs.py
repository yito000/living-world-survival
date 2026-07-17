"""可観測性のテスト（10B 3.5 / 第13章 L-1）。

/healthz（liveness）と /readyz（依存チェック）の**分離**、/metrics の exposition、
JSON 構造化ログの整形と秘密の伏せ字を担保する。
"""

from __future__ import annotations

import json
import logging
import urllib.error
import urllib.request
from collections.abc import Iterator
from typing import Any

import pytest
from prometheus_client import REGISTRY

from worker.main import READINESS, start_health_server
from worker.obs import JSONFormatter, classify_error, observe_usage, render_metrics


class FakeNats:
    """readyz が見るのは is_connected だけ（ハンドラスレッドから同期的に読める属性）。"""

    def __init__(self, connected: bool) -> None:
        self.is_connected = connected


@pytest.fixture
def health_url() -> Iterator[str]:
    # port 0 = OS に空きポートを選ばせる（テスト並走でも衝突しない）。
    server = start_health_server(0)
    try:
        yield f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        server.server_close()
        READINESS.set_nats(None)


def get(url: str) -> tuple[int, str]:
    """ステータスと本文を返す（4xx/5xx でも例外にしない）。"""
    try:
        with urllib.request.urlopen(url, timeout=5) as resp:  # noqa: S310 (localhost fixture)
            return resp.status, resp.read().decode()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode()


def test_healthz_is_liveness_only(health_url: str) -> None:
    """NATS 未接続でも /healthz は 200。依存の瞬断で再起動されてはならない。"""
    READINESS.set_nats(None)
    status, body = get(f"{health_url}/healthz")
    assert status == 200
    assert json.loads(body)["status"] == "ok"


def test_readyz_unavailable_when_nats_absent(health_url: str) -> None:
    READINESS.set_nats(None)
    status, body = get(f"{health_url}/readyz")
    assert status == 503
    assert json.loads(body) == {"status": "unavailable", "dependency": "nats"}


def test_readyz_unavailable_when_nats_disconnected(health_url: str) -> None:
    """接続オブジェクトが在っても切断中（再接続待ち）なら ready ではない。"""
    READINESS.set_nats(FakeNats(connected=False))
    status, body = get(f"{health_url}/readyz")
    assert status == 503
    assert json.loads(body)["dependency"] == "nats"


def test_readyz_ok_when_nats_connected(health_url: str) -> None:
    READINESS.set_nats(FakeNats(connected=True))
    status, body = get(f"{health_url}/readyz")
    assert status == 200
    assert json.loads(body)["status"] == "ok"


def test_readyz_ignores_postgres(health_url: str) -> None:
    """Postgres 不在（pool=None）は un-ready にしない。

    record_decision は best-effort であり、DB が無くても判断の発行と DS の fallback は
    成立する（3.2）。dependency に postgres が出ないことまで見る。
    """
    READINESS.set_nats(FakeNats(connected=True))
    status, body = get(f"{health_url}/readyz")
    assert status == 200
    assert "postgres" not in body


def test_unknown_path_is_404(health_url: str) -> None:
    status, _ = get(f"{health_url}/nope")
    assert status == 404


def test_metrics_endpoint_serves_exposition(health_url: str) -> None:
    status, body = get(f"{health_url}/metrics")
    assert status == 200
    # HELP/TYPE 付きの exposition 形式で、本サービスの指標が載っている（R-PROM）。
    assert "# HELP llm_decisions_total" in body
    assert "# TYPE llm_decision_duration_seconds histogram" in body


def test_render_metrics_content_type() -> None:
    body, content_type = render_metrics()
    assert isinstance(body, bytes)
    assert content_type.startswith("text/plain")


def test_decision_histogram_has_spec_buckets() -> None:
    """3.1 の 30 秒 / 60 秒がそのまま読める境界であること。"""
    body, _ = render_metrics()
    text = body.decode()
    assert 'llm_decision_duration_seconds_bucket{le="30.0"}' in text
    assert 'llm_decision_duration_seconds_bucket{le="60.0"}' in text


def token_total(kind: str) -> float:
    """llm_tokens_total{kind} の現在値。Counter は既定レジストリで**プロセス共有**なので、
    他のテストの寄与を避けるため絶対値ではなく差分で確認する。"""
    return REGISTRY.get_sample_value("llm_tokens_total", {"kind": kind}) or 0.0


def test_observe_usage_counts_tokens_by_kind() -> None:
    before = {k: token_total(k) for k in ("input", "output", "cache_read", "cache_creation")}
    observe_usage(
        {
            "input_tokens": 12,
            "output_tokens": 3,
            "cache_read_input_tokens": 5,
            "unknown_field": 99,  # 未知キーは黙って捨てる（ラベル爆発を防ぐ）
        }
    )
    assert token_total("input") - before["input"] == 12
    assert token_total("output") - before["output"] == 3
    assert token_total("cache_read") - before["cache_read"] == 5
    # usage に無かった kind は増えない。
    assert token_total("cache_creation") == before["cache_creation"]
    # exposition にも kind ラベル付きで出る。
    assert 'llm_tokens_total{kind="input"}' in render_metrics()[0].decode()


def test_classify_error_maps_to_bounded_labels() -> None:
    """ラベルは有限集合に落ちる（生の例外文言をラベルにしない）。"""
    from worker.llm import DecisionRefused, LLMError
    from worker.schemas import AllowedIdError

    assert classify_error(DecisionRefused("refused")) == "refusal"
    assert classify_error(AllowedIdError("bad id")) == "allowed_id"
    assert classify_error(LLMError("boom")) == "other"

    class APITimeoutError(Exception):
        pass

    # SDK のタイムアウトは LLMError に包まれて上がる（__cause__ を辿って分類する）。
    wrapped = LLMError("decision: LLM call failed")
    wrapped.__cause__ = APITimeoutError("timed out")
    assert classify_error(wrapped) == "timeout"


def make_record(**extra: Any) -> logging.LogRecord:
    record = logging.LogRecord(
        name="llm-worker",
        level=logging.INFO,
        pathname=__file__,
        lineno=1,
        msg="decision produced",
        args=(),
        exc_info=None,
    )
    for key, value in extra.items():
        setattr(record, key, value)
    return record


def test_formatter_emits_valid_json_with_service() -> None:
    out = json.loads(JSONFormatter("llm-worker").format(make_record(actor_id="a1")))
    assert out["service"] == "llm-worker"
    assert out["msg"] == "decision produced"
    assert out["level"] == "info"
    assert out["actor_id"] == "a1"


def test_formatter_redacts_secret_keys() -> None:
    out = json.loads(
        JSONFormatter("llm-worker").format(
            make_record(
                api_key="sk-ant-supersecret",
                authorization="Bearer abc",
                refresh_token="rt-1",
                actor_id="a1",
            )
        )
    )
    assert out["api_key"] == "[REDACTED]"
    assert out["authorization"] == "[REDACTED]"
    assert out["refresh_token"] == "[REDACTED]"
    # 相関フィールドは伏せない（第13章の追跡に要る）。
    assert out["actor_id"] == "a1"
    assert "supersecret" not in json.dumps(out)


def test_formatter_survives_unserializable_values() -> None:
    out = json.loads(JSONFormatter("llm-worker").format(make_record(obj=object())))
    assert isinstance(out["obj"], str)
