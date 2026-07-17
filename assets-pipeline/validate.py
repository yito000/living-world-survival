"""Asset manifest validator (MVP 15章). Pure Python — no Blender required.

    python assets-pipeline/validate.py --in build/assets

Checks, per module (MVP 15章 CI 要件 / M7 3.7 で厳格化):
  - missing socket        … Kit 別の必須 Socket 名と個数を満たす
  - collider 存在＋命名     … UCX_ 命名の Collider が1つ以上
  - negative scale         … negative_scale False（generate.py が実測）
  - non-manifold           … Collider は 0 を要求。描画メッシュは warning 止まり
  - triangle budget        … Kit 別の描画バジェット / Collider 別バジェット
  - LOD 存在               … REQUIRED_LOD を含む
  - glb ファイル存在        … manifest の glb がディスク上に存在
Exit code 0 なら全検査通過、1 なら失敗。
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from asset_spec import (  # noqa: E402
    COLLIDER_NAME_PREFIX,
    REQUIRED_LOD,
    collider_triangle_budget,
    iter_modules,
    required_socket_min,
    required_sockets,
    triangle_budget,
)


def validate_manifest(manifest: dict, asset_dir: str | None = None) -> list[str]:
    """manifest を検証し、問題のリスト（空なら合格）を返す。"""
    problems, _warnings = check_manifest(manifest, asset_dir=asset_dir)
    return problems


def _check_triangles(m: dict, key: str, kit: str, problems: list[str]) -> None:
    """描画メッシュ / Collider の三角形数を Kit 別バジェットで検査する（3.7-3）。"""
    tris = m.get("triangles", 0)
    budget = triangle_budget(kit)
    if not isinstance(tris, int) or tris <= 0:
        problems.append(f"{key}: invalid triangle count {tris}")
    elif tris > budget:
        problems.append(f"{key}: triangle budget exceeded ({tris} > {budget} for kit {kit})")

    # Collider は Server 用軽量メッシュとして別バジェット。
    if "collider_triangles" not in m:
        problems.append(f"{key}: manifest missing 'collider_triangles' (regenerate assets)")
        return
    ctris = m.get("collider_triangles")
    cbudget = collider_triangle_budget(kit)
    if not isinstance(ctris, int) or ctris <= 0:
        problems.append(f"{key}: invalid collider triangle count {ctris}")
    elif ctris > cbudget:
        problems.append(
            f"{key}: collider triangle budget exceeded ({ctris} > {cbudget} for kit {kit})"
        )


def _check_colliders(m: dict, key: str, problems: list[str]) -> None:
    """Collider の存在と UCX_ 命名規約を検査する（3.7-4）。"""
    if "colliders" not in m:
        problems.append(f"{key}: manifest missing 'colliders' (regenerate assets)")
        return
    colliders = m.get("colliders")
    if not isinstance(colliders, list) or not colliders:
        problems.append(f"{key}: missing collider")
        return
    for name in colliders:
        if not isinstance(name, str) or not name.startswith(COLLIDER_NAME_PREFIX):
            problems.append(
                f"{key}: collider name violates {COLLIDER_NAME_PREFIX}* convention ({name!r})"
            )
    # has_collider は実測値。colliders と食い違うなら生成側のバグ。
    if not m.get("has_collider", False):
        problems.append(f"{key}: missing collider")


def _check_non_manifold(m: dict, key: str, problems: list[str], warnings: list[str]) -> None:
    """non-manifold エッジを検査する。Collider は厳格、描画メッシュは緩和（3.7-2）。"""
    if "non_manifold_edges" not in m:
        problems.append(f"{key}: manifest missing 'non_manifold_edges' (regenerate assets)")
        return
    counts = m.get("non_manifold_edges")
    if not isinstance(counts, dict):
        problems.append(f"{key}: invalid 'non_manifold_edges' (expected object, got {counts!r})")
        return

    if "collider" not in counts:
        problems.append(f"{key}: 'non_manifold_edges' missing 'collider' entry")
    else:
        collider_edges = counts.get("collider")
        if not isinstance(collider_edges, int) or collider_edges < 0:
            problems.append(f"{key}: invalid collider non-manifold count {collider_edges!r}")
        elif collider_edges > 0:
            # 衝突形状が閉多様体でないと物理が破綻するため失敗扱い。
            problems.append(f"{key}: collider is non-manifold ({collider_edges} edge(s))")

    # 描画メッシュは装飾で開いた形状があり得るため warning に留める（第15章「必要範囲」）。
    body_edges = counts.get("body")
    if isinstance(body_edges, int) and body_edges > 0:
        warnings.append(f"{key}: body mesh is non-manifold ({body_edges} edge(s))")


def _check_sockets(m: dict, key: str, kit: str, problems: list[str]) -> None:
    """Kit 別の必須 Socket 名 / 個数を検査する（3.7-5）。"""
    sockets = m.get("sockets") or []
    minimum = required_socket_min(kit)
    if len(sockets) < minimum:
        problems.append(f"{key}: missing socket (need >= {minimum} for kit {kit})")
    for name in required_sockets(kit):
        if name not in sockets:
            problems.append(f"{key}: missing required socket {name!r} for kit {kit}")


def check_manifest(manifest: dict, asset_dir: str | None = None) -> tuple[list[str], list[str]]:
    """manifest を検証し (問題, 警告) を返す。問題が空なら合格。"""
    problems: list[str] = []
    warnings: list[str] = []

    modules = manifest.get("modules")
    if not isinstance(modules, list) or not modules:
        return ["manifest has no modules"], warnings

    seed = manifest.get("seed")
    module_size = manifest.get("module_size")

    # 期待されるモジュール集合（決定的）と一致するか。
    if isinstance(seed, int) and isinstance(module_size, int):
        expected = {m.key for m in iter_modules(seed, module_size)}
        actual = {f"{m.get('kit')}/{m.get('name')}" for m in modules}
        for missing in sorted(expected - actual):
            problems.append(f"missing module: {missing}")

    seen_ids: set[str] = set()
    for m in modules:
        kit = str(m.get("kit"))
        key = f"{kit}/{m.get('name')}"

        asset_id = m.get("asset_id")
        if asset_id in seen_ids:
            problems.append(f"{key}: duplicate asset_id {asset_id}")
        else:
            seen_ids.add(asset_id)

        _check_sockets(m, key, kit, problems)
        _check_colliders(m, key, problems)
        _check_non_manifold(m, key, problems, warnings)
        _check_triangles(m, key, kit, problems)

        if m.get("negative_scale", False):
            problems.append(f"{key}: negative scale detected")

        if REQUIRED_LOD not in (m.get("lods") or []):
            problems.append(f"{key}: missing {REQUIRED_LOD}")

        if asset_dir is not None:
            glb = m.get("glb")
            if not glb or not os.path.isfile(os.path.join(asset_dir, glb)):
                problems.append(f"{key}: glb file not found ({glb})")

    return problems, warnings


def _script_argv() -> list[str]:
    # Blender 経由（blender --background --python validate.py -- --in ...）でも
    # 標準 Python（python validate.py --in ...）でも動くよう引数を取り出す。
    if "--" in sys.argv:
        return sys.argv[sys.argv.index("--") + 1 :]
    return sys.argv[1:]


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate generated asset manifest")
    parser.add_argument("--in", dest="in_dir", type=str, default="build/assets")
    parser.add_argument(
        "--skip-glb-check",
        action="store_true",
        help="manifest のみ検証し .glb 実体の存在確認をスキップ",
    )
    return parser.parse_args(_script_argv())


def main() -> int:
    args = _parse_args()
    manifest_path = os.path.join(args.in_dir, "manifest.json")
    if not os.path.isfile(manifest_path):
        print(f"ERROR: manifest not found: {manifest_path}", file=sys.stderr)
        return 1

    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)

    asset_dir = None if args.skip_glb_check else args.in_dir
    problems, warnings = check_manifest(manifest, asset_dir=asset_dir)

    for w in warnings:
        print(f"warning: {w}", file=sys.stderr)

    if problems:
        print(f"asset validation FAILED ({len(problems)} problem(s)):", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1

    print(f"asset validation OK ({len(manifest['modules'])} modules)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
