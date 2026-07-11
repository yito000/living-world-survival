from fastapi.testclient import TestClient

from app.main import app


def test_healthz() -> None:
    with TestClient(app) as client:
        resp = client.get("/healthz")
        assert resp.status_code == 200
        assert resp.json() == {"status": "ok", "service": "worldstate"}


def test_readyz_without_nats() -> None:
    # No NATS available in unit tests → readiness reports unavailable (503).
    with TestClient(app) as client:
        resp = client.get("/readyz")
        assert resp.status_code == 503
        assert resp.json()["dependency"] == "nats"
