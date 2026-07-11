from worker.main import build_mock_decision


def test_build_mock_decision_shape() -> None:
    req = {"actor_id": "a1", "state_version": 7, "server_id": "s1"}
    d = build_mock_decision(req)
    assert d["actor_id"] == "a1"
    assert d["state_version"] == 7
    assert d["decision_id"] == "mock-a1-7"
    assert d["steps"] == [{"action_template_id": "idle.wait", "params": {}}]
    assert d["mock"] is True


def test_build_mock_decision_defaults() -> None:
    d = build_mock_decision({})
    assert d["actor_id"] == "unknown"
    assert d["state_version"] == 0
