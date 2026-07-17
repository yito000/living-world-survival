import json
import logging

import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.main import app
from app.obs import REDACTED, JSONFormatter


@pytest.fixture(autouse=True)
def _no_nats(monkeypatch):
    """テストでは実 NATS に接続しない（環境に nats が起動していても決定論的）。

    lifespan の起動フックを差し替え、接続を張らず state.nc を None にする。
    """

    async def _noop() -> None:
        main_module.state.nc = None

    monkeypatch.setattr(main_module, "_connect_nats", _noop)


class _FakePool:
    """readiness の DB 往復だけを満たす最小のプール。"""

    def __init__(self, ok: bool = True) -> None:
        self._ok = ok

    async def fetchval(self, *_args, **_kwargs):
        if not self._ok:
            raise RuntimeError("connection refused")
        return 1


class _FakeNats:
    def __init__(self, connected: bool) -> None:
        self.is_connected = connected


def test_healthz() -> None:
    with TestClient(app) as client:
        resp = client.get("/healthz")
        assert resp.status_code == 200
        assert resp.json() == {"status": "ok", "service": "worldstate"}


# liveness は依存先の状態に左右されない。DB/NATS 断で再起動されては困る。
def test_healthz_stays_ok_without_dependencies() -> None:
    with TestClient(app) as client:
        main_module.state.pool = None
        main_module.state.nc = None
        assert client.get("/healthz").status_code == 200


def test_readyz_without_postgres() -> None:
    # Postgres 未接続は readiness 503（dependency=postgres）。
    # 投影も判断要求購読も DB 無しには成立しないため。
    with TestClient(app) as client:
        main_module.state.pool = None
        resp = client.get("/readyz")
        assert resp.status_code == 503
        assert resp.json()["dependency"] == "postgres"


def test_readyz_when_postgres_unreachable() -> None:
    # プールを握っていても実際に往復できなければ ready ではない。
    with TestClient(app) as client:
        main_module.state.pool = _FakePool(ok=False)
        main_module.state.nc = _FakeNats(connected=True)
        resp = client.get("/readyz")
        assert resp.status_code == 503
        assert resp.json()["dependency"] == "postgres"


def test_readyz_without_nats() -> None:
    # DB は生きていて NATS だけ落ちている場合、落ちた依存を名指しする。
    with TestClient(app) as client:
        main_module.state.pool = _FakePool()
        main_module.state.nc = None
        resp = client.get("/readyz")
        assert resp.status_code == 503
        assert resp.json()["dependency"] == "nats"


def test_readyz_ready_when_all_dependencies_up() -> None:
    with TestClient(app) as client:
        main_module.state.pool = _FakePool()
        main_module.state.nc = _FakeNats(connected=True)
        resp = client.get("/readyz")
        assert resp.status_code == 200
        assert resp.json() == {"status": "ready", "dependency": "postgres,nats"}


def test_metrics_exposes_prometheus_format() -> None:
    # 負荷/Soak ハーネスがスクレイプする（10B 3.1/3.2）。
    with TestClient(app) as client:
        resp = client.get("/metrics")
        assert resp.status_code == 200
        assert "text/plain" in resp.headers["content-type"]
        assert "worldstate_events_processed_total" in resp.text


def _format(record_kwargs: dict) -> dict:
    record = logging.LogRecord(
        name="worldstate",
        level=logging.INFO,
        pathname=__file__,
        lineno=1,
        msg="projected event",
        args=(),
        exc_info=None,
    )
    for key, value in record_kwargs.items():
        setattr(record, key, value)
    return json.loads(JSONFormatter("worldstate").format(record))


def test_json_formatter_emits_structured_fields() -> None:
    got = _format({"world_id": "w1", "actor_id": "a1"})
    assert got["service"] == "worldstate"
    assert got["level"] == "info"
    assert got["msg"] == "projected event"
    assert got["world_id"] == "w1"
    assert got["actor_id"] == "a1"


# MVP-SEC-002: Password/Token をログに出さない。
def test_json_formatter_redacts_secrets() -> None:
    got = _format({"password": "hunter2", "refresh_token": "rt-abc", "account_id": "acc-1"})
    assert got["password"] == REDACTED
    assert got["refresh_token"] == REDACTED
    assert got["account_id"] == "acc-1"
