import copy

from asset_spec import TRIANGLE_BUDGET, iter_modules
from validate import validate_manifest


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
                "has_collider": True,
                "sockets": ["socket_top"],
                "interaction_points": ["ip_use"],
                "lods": ["LOD0"],
                "negative_scale": False,
            }
        )
    return {"seed": seed, "module_size": module_size, "modules": modules}


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
    m["modules"][0]["triangles"] = TRIANGLE_BUDGET + 1
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
