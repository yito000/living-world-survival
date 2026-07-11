"""Asset manifest validator (MVP 15章). Pure Python — no Blender required.

    python assets-pipeline/validate.py --in build/assets

Checks, per module (MVP 15章 CI 要件):
  - missing socket        … 各モジュールに Socket が >= REQUIRED_SOCKET_MIN
  - collider 存在          … has_collider True
  - negative scale         … negative_scale False
  - triangle budget        … triangles <= TRIANGLE_BUDGET
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
    REQUIRED_LOD,
    REQUIRED_SOCKET_MIN,
    TRIANGLE_BUDGET,
    iter_modules,
)


def validate_manifest(manifest: dict, asset_dir: str | None = None) -> list[str]:
    """manifest を検証し、問題のリスト（空なら合格）を返す。"""
    problems: list[str] = []

    modules = manifest.get("modules")
    if not isinstance(modules, list) or not modules:
        return ["manifest has no modules"]

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
        key = f"{m.get('kit')}/{m.get('name')}"

        asset_id = m.get("asset_id")
        if asset_id in seen_ids:
            problems.append(f"{key}: duplicate asset_id {asset_id}")
        else:
            seen_ids.add(asset_id)

        sockets = m.get("sockets") or []
        if len(sockets) < REQUIRED_SOCKET_MIN:
            problems.append(f"{key}: missing socket (need >= {REQUIRED_SOCKET_MIN})")

        if not m.get("has_collider", False):
            problems.append(f"{key}: missing collider")

        if m.get("negative_scale", False):
            problems.append(f"{key}: negative scale detected")

        tris = m.get("triangles", 0)
        if not isinstance(tris, int) or tris <= 0:
            problems.append(f"{key}: invalid triangle count {tris}")
        elif tris > TRIANGLE_BUDGET:
            problems.append(f"{key}: triangle budget exceeded ({tris} > {TRIANGLE_BUDGET})")

        if REQUIRED_LOD not in (m.get("lods") or []):
            problems.append(f"{key}: missing {REQUIRED_LOD}")

        if asset_dir is not None:
            glb = m.get("glb")
            if not glb or not os.path.isfile(os.path.join(asset_dir, glb)):
                problems.append(f"{key}: glb file not found ({glb})")

    return problems


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
    problems = validate_manifest(manifest, asset_dir=asset_dir)

    if problems:
        print(f"asset validation FAILED ({len(problems)} problem(s)):", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1

    print(f"asset validation OK ({len(manifest['modules'])} modules)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
