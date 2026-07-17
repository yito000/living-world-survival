import copy

from asset_spec import (
    collider_triangle_budget,
    iter_modules,
    required_sockets,
    triangle_budget,
)
from validate import check_manifest, validate_manifest


def _good_manifest(seed: int = 1, module_size: int = 4) -> dict:
    modules = []
    for spec in iter_modules(seed, module_size):
        modules.append(
            {
                "asset_id": spec.asset_id,
                "version": spec.version,
                "kit": spec.kit,
                "name": spec.name,
                "glb": spec.glb_filename,
                "triangles": 12,
                "collider_triangles": 12,
                "colliders": [f"UCX_{spec.kit}_{spec.name}"],
                "has_collider": True,
                "sockets": sorted(required_sockets(spec.kit)),
                "interaction_points": ["ip_use"],
                "lods": ["LOD0"],
                "negative_scale": False,
                "non_manifold_edges": {"body": 0, "collider": 0},
            }
        )
    return {"seed": seed, "module_size": module_size, "modules": modules}


def _module(manifest: dict, kit: str) -> dict:
    return next(m for m in manifest["modules"] if m["kit"] == kit)


def test_good_manifest_passes() -> None:
    problems = validate_manifest(_good_manifest())
    assert problems == [], problems


def test_deterministic_ids() -> None:
    a = _good_manifest(1, 4)
    b = _good_manifest(1, 4)
    ids_a = [m["asset_id"] for m in a["modules"]]
    ids_b = [m["asset_id"] for m in b["modules"]]
    assert ids_a == ids_b
    # 異なる seed は異なる id
    c = _good_manifest(2, 4)
    assert [m["asset_id"] for m in c["modules"]] != ids_a


def test_missing_socket_detected() -> None:
    m = _good_manifest()
    m["modules"][0]["sockets"] = []
    problems = validate_manifest(m)
    assert any("missing socket" in p for p in problems)


def test_missing_collider_detected() -> None:
    m = _good_manifest()
    m["modules"][0]["has_collider"] = False
    problems = validate_manifest(m)
    assert any("missing collider" in p for p in problems)


def test_negative_scale_detected() -> None:
    m = _good_manifest()
    m["modules"][0]["negative_scale"] = True
    problems = validate_manifest(m)
    assert any("negative scale" in p for p in problems)


def test_triangle_budget_detected() -> None:
    m = _good_manifest()
    kit = m["modules"][0]["kit"]
    m["modules"][0]["triangles"] = triangle_budget(kit) + 1
    problems = validate_manifest(m)
    assert any("triangle budget" in p for p in problems)


def test_missing_lod_detected() -> None:
    m = _good_manifest()
    m["modules"][0]["lods"] = []
    problems = validate_manifest(m)
    assert any("LOD0" in p for p in problems)


def test_missing_module_detected() -> None:
    m = _good_manifest()
    removed = m["modules"].pop()
    problems = validate_manifest(m)
    assert any(removed["kit"] in p and "missing module" in p for p in problems)


def test_empty_manifest() -> None:
    assert validate_manifest({"modules": []}) == ["manifest has no modules"]


def test_deepcopy_independence() -> None:
    # validate_manifest は入力を変更しないこと。
    m = _good_manifest()
    snapshot = copy.deepcopy(m)
    validate_manifest(m)
    assert m == snapshot


# --- M7 3.7 で追加した厳格化ルール ---------------------------------------------


def test_triangle_budget_is_per_kit() -> None:
    # nature(600) は production(1500) より厳しい。
    assert triangle_budget("nature") < triangle_budget("production")
    # 未知 Kit は既定値へフォールバックし、例外にならない。
    assert triangle_budget("unknown_kit") > 0


def test_kit_budget_boundary_pass_and_fail() -> None:
    m = _good_manifest()
    nature = _module(m, "nature")
    # ちょうど上限は合格。
    nature["triangles"] = triangle_budget("nature")
    assert validate_manifest(m) == []
    # 1つ超えると失敗。しかも production 用の緩い上限では見逃される値であること。
    nature["triangles"] = triangle_budget("nature") + 1
    assert any("triangle budget exceeded" in p for p in validate_manifest(m))
    assert nature["triangles"] <= triangle_budget("production")


def test_collider_triangle_budget_detected() -> None:
    m = _good_manifest()
    kit = m["modules"][0]["kit"]
    m["modules"][0]["collider_triangles"] = collider_triangle_budget(kit) + 1
    problems = validate_manifest(m)
    assert any("collider triangle budget exceeded" in p for p in problems)


def test_collider_naming_violation_detected() -> None:
    m = _good_manifest()
    m["modules"][0]["colliders"] = ["Cube_collision"]
    problems = validate_manifest(m)
    assert any("convention" in p for p in problems)


def test_empty_collider_list_detected() -> None:
    m = _good_manifest()
    m["modules"][0]["colliders"] = []
    problems = validate_manifest(m)
    assert any("missing collider" in p for p in problems)


def test_non_manifold_collider_fails() -> None:
    m = _good_manifest()
    m["modules"][0]["non_manifold_edges"] = {"body": 0, "collider": 3}
    problems, warnings = check_manifest(m)
    assert any("collider is non-manifold" in p for p in problems)
    assert warnings == []


def test_non_manifold_body_warns_only() -> None:
    # 描画メッシュの non-manifold は緩和（装飾を落とさないため）。
    m = _good_manifest()
    m["modules"][0]["non_manifold_edges"] = {"body": 7, "collider": 0}
    problems, warnings = check_manifest(m)
    assert problems == [], problems
    assert any("body mesh is non-manifold" in w for w in warnings)


def test_per_kit_required_socket_missing_detected() -> None:
    # production は投入口 socket_input を必須にしている。
    assert "socket_input" in required_sockets("production")
    m = _good_manifest()
    forge = _module(m, "production")
    forge["sockets"] = ["socket_top"]
    problems = validate_manifest(m)
    assert any("socket_input" in p for p in problems)
    # 同じ socket 構成でも mine では合格する（Kit 別であること）。
    m2 = _good_manifest()
    _module(m2, "mine")["sockets"] = ["socket_top"]
    assert validate_manifest(m2) == []


def test_missing_new_keys_reported_gracefully() -> None:
    # 旧スキーマの manifest は KeyError ではなく明示的な problem になること。
    m = _good_manifest()
    for k in ("colliders", "non_manifold_edges", "collider_triangles"):
        m["modules"][0].pop(k)
    problems = validate_manifest(m)  # 例外を投げないこと
    assert any("missing 'colliders'" in p for p in problems)
    assert any("missing 'non_manifold_edges'" in p for p in problems)
    assert any("missing 'collider_triangles'" in p for p in problems)


def test_malformed_non_manifold_shape_reported() -> None:
    m = _good_manifest()
    m["modules"][0]["non_manifold_edges"] = 0  # dict でない
    problems = validate_manifest(m)
    assert any("invalid 'non_manifold_edges'" in p for p in problems)
