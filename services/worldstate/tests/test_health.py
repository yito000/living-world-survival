import pytest
from fastapi.testclient import TestClient

import app.main as main_module
from app.main import app


@pytest.fixture(autouse=True)
def _no_nats(monkeypatch):
    """テストでは実 NATS に接続しない（環境に nats が起動していても決定論的）。

    lifespan の起動フックを差し替え、接続を張らず state.nc を None にする。
    これで /readyz は常に「未接続=503」を返す（ユニットテストの前提）。
    """

    async def _noop() -> None:
        main_module.state.nc = None

    monkeypatch.setattr(main_module, "_connect_nats", _noop)


def test_healthz() -> None:
    with TestClient(app) as client:
        resp = client.get("/healthz")
        assert resp.status_code == 200
        assert resp.json() == {"status": "ok", "service": "worldstate"}


def test_readyz_without_nats() -> None:
    # NATS 未接続時は readiness が 503（dependency=nats）を返す。
    with TestClient(app) as client:
        resp = client.get("/readyz")
        assert resp.status_code == 503
        assert resp.json()["dependency"] == "nats"
