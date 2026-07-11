"""Blender headless asset generator (MVP 15章).

Run under Blender:

    blender --background --python assets-pipeline/generate.py -- \
        --seed 1 --module-size 4 --out build/assets

Deterministic: the same (seed, module-size) yields the same asset_id / version
for every module (see asset_spec.py). Each module gets a bottom-center pivot,
a Socket Empty, a Collider mesh, an LOD0 name and an Interaction Point, then is
exported as glTF (.glb). A manifest.json describing every module is written to
the output directory for validate.py to check.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

# assets-pipeline をパスに追加（Blender 実行時の cwd に依存しないため）。
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from asset_spec import REQUIRED_LOD, ModuleSpec, iter_modules  # noqa: E402

try:
    import bpy  # type: ignore
except ImportError:  # Blender 外で import された場合
    bpy = None


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate MVP Blender asset kits")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--module-size", type=int, default=4)
    parser.add_argument("--out", type=str, default="build/assets")
    return parser.parse_args(argv)


def _argv_after_dashdash() -> list[str]:
    # Blender は `-- ` 以降の引数をスクリプトへ渡す。
    if "--" in sys.argv:
        return sys.argv[sys.argv.index("--") + 1 :]
    return []


def _reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)


def _build_module(spec: ModuleSpec, size: float) -> dict:
    """Blender 上に1モジュールを構築し、manifest エントリを返す。"""
    _reset_scene()

    # 本体メッシュ（bottom-center pivot にするため原点を底面へ）。
    bpy.ops.mesh.primitive_cube_add(size=size, location=(0, 0, size / 2.0))
    obj = bpy.context.active_object
    obj.name = f"{spec.kit}_{spec.name}_{REQUIRED_LOD}"

    # Socket Empty（接続点）。
    bpy.ops.object.empty_add(type="PLAIN_AXES", location=(0, 0, size))
    socket = bpy.context.active_object
    socket.name = "socket_top"

    # Interaction Point。
    bpy.ops.object.empty_add(type="ARROWS", location=(0, -size / 2.0, size / 2.0))
    ip = bpy.context.active_object
    ip.name = "ip_use"

    # Collider（別メッシュ、命名規約 UCX_ に準拠）。
    bpy.ops.mesh.primitive_cube_add(size=size, location=(0, 0, size / 2.0))
    collider = bpy.context.active_object
    collider.name = f"UCX_{spec.kit}_{spec.name}"

    tri_count = _triangle_count(obj)

    return {
        "asset_id": spec.asset_id,
        "version": spec.version,
        "kit": spec.kit,
        "name": spec.name,
        "glb": spec.glb_filename,
        "triangles": tri_count,
        "has_collider": True,
        "sockets": ["socket_top"],
        "interaction_points": ["ip_use"],
        "lods": [REQUIRED_LOD],
        "negative_scale": False,
    }


def _triangle_count(obj) -> int:
    # Blender 4.x/5.x で API 差があるため堅牢に数える。
    mesh = obj.data
    try:
        mesh.calc_loop_triangles()
    except Exception:  # 一部バージョンでは自動計算/削除済み
        pass
    n = len(mesh.loop_triangles)
    if n == 0:
        # フォールバック: n-gon を三角形換算（頂点数-2）。
        n = sum(max(0, len(p.vertices) - 2) for p in mesh.polygons)
    return n


def _export_glb(path: str) -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True)


def main() -> None:
    args = _parse_args(_argv_after_dashdash())
    os.makedirs(args.out, exist_ok=True)

    if bpy is None:
        print("ERROR: generate.py must be run inside Blender (bpy unavailable).", file=sys.stderr)
        sys.exit(2)

    modules: list[dict] = []
    for spec in iter_modules(args.seed, args.module_size):
        entry = _build_module(spec, float(args.module_size))
        _export_glb(os.path.join(args.out, spec.glb_filename))
        modules.append(entry)
        print(f"generated {spec.key} -> {entry['glb']} ({entry['triangles']} tris)")

    manifest = {
        "seed": args.seed,
        "module_size": args.module_size,
        "modules": modules,
    }
    manifest_path = os.path.join(args.out, "manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=True)
    print(f"wrote {manifest_path} ({len(modules)} modules)")


if __name__ == "__main__":
    main()
