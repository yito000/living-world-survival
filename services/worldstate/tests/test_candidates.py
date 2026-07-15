"""候補 Template 絞り込みルールのユニットテスト（07B 5 Unit / 3.4）。"""

from __future__ import annotations

from app.candidates import FALLBACK_TEMPLATE, select_candidates

TEMPLATES = [
    {"template_id": "survival.eat_owned_food", "tags": ["food", "hunger_high"]},
    {"template_id": "cleaning.clean_nearby", "tags": ["cleanup", "waste_nearby"]},
    {"template_id": "mining.acquire_iron", "tags": ["earn", "iron_needed"]},
    {"template_id": "economy.sell_surplus", "tags": ["sell", "inventory_overflow"]},
    {"template_id": "safety.idle_at_camp", "tags": ["fallback"]},
]


def test_food_reason_keeps_food_templates_only() -> None:
    got = select_candidates(TEMPLATES, "hunger_high")
    assert "survival.eat_owned_food" in got
    # 候補外（cleanup/earn/sell）は除外される。
    assert "cleaning.clean_nearby" not in got
    assert "mining.acquire_iron" not in got


def test_fallback_is_always_terminal() -> None:
    got = select_candidates(TEMPLATES, "earn")
    assert "mining.acquire_iron" in got
    # フォールバックは必ず末尾に含まれる（終端）。
    assert got[-1] == FALLBACK_TEMPLATE


def test_unknown_reason_returns_fallback_only() -> None:
    got = select_candidates(TEMPLATES, "totally-unknown-reason")
    assert got == [FALLBACK_TEMPLATE]


def test_empty_reason_returns_fallback_only() -> None:
    assert select_candidates(TEMPLATES, "") == [FALLBACK_TEMPLATE]


def test_order_is_stable_by_template_id() -> None:
    templates = [
        {"template_id": "b.food", "tags": ["food"]},
        {"template_id": "a.food", "tags": ["food"]},
        {"template_id": "safety.idle_at_camp", "tags": ["fallback"]},
    ]
    got = select_candidates(templates, "food")
    assert got == ["a.food", "b.food", "safety.idle_at_camp"]
