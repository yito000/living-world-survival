from worker.decision import (
    FALLBACK_TEMPLATE,
    _select_template,
    build_mock_decision,
    decision_id_of,
    result_subject,
)


def test_build_mock_decision_shape() -> None:
    req = {"actor_id": "a1", "state_version": 7, "server_id": "s1", "reason": "hunger_high"}
    d = build_mock_decision(req)
    assert d["actor_id"] == "a1"
    assert d["state_version"] == 7
    # decision_id は worldstate（requested）と一致する決定的合成（actor:version）。
    assert d["decision_id"] == "a1:7"
    assert d["template_id"] == "survival.eat_owned_food"
    assert d["steps"] == [{"action_template_id": "survival.eat_owned_food", "params": {}}]
    assert d["mock"] is True


def test_build_mock_decision_defaults_to_fallback() -> None:
    d = build_mock_decision({})
    assert d["actor_id"] == "unknown"
    assert d["state_version"] == 0
    # reason 無しは安全待機（フォールバック終端）。
    assert d["template_id"] == FALLBACK_TEMPLATE


def test_state_version_from_state_versions_map() -> None:
    # proto DecisionRequest は state_versions マップで personal_state_version を運ぶ。
    d = build_mock_decision({"actor_id": "a2", "state_versions": {"personal_state": 12}})
    assert d["state_version"] == 12
    assert d["decision_id"] == "a2:12"


def test_select_template_maps_reason() -> None:
    assert _select_template("cleanup pressure") == "cleaning.clean_nearby"
    assert _select_template("need to earn iron") == "mining.acquire_iron"
    assert _select_template("inventory overflow, sell") == "economy.sell_surplus"
    assert _select_template("") == FALLBACK_TEMPLATE


def test_result_subject_resolves_server_id() -> None:
    assert result_subject({"server_id": "s9"}) == "ai.decision.result.s9"
    # server_id 欠落は world_id、それも無ければ unknown へフォールバック。
    assert result_subject({"world_id": "w3"}) == "ai.decision.result.w3"
    assert result_subject({}) == "ai.decision.result.unknown"


def test_decision_id_is_stable() -> None:
    assert decision_id_of("actor-x", 5) == "actor-x:5"
