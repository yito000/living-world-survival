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

from asset_spec import (  # noqa: E402
    COLLIDER_NAME_PREFIX,
    REQUIRED_LOD,
    ModuleSpec,
    iter_modules,
    required_socket_min,
    required_sockets,
)

try:
    import bmesh  # type: ignore
    import bpy  # type: ignore
except ImportError:  # Blender 外で import された場合
    bmesh = None
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


def _socket_location(name: str, size: float) -> tuple[float, float, float]:
    """Socket 名から決定的な配置座標を返す（未知名は上面中央）。"""
    if name == "socket_input":
        # 投入口は前面やや上（production の素材投入を想定）。
        return (0.0, -size / 2.0, size * 0.75)
    return (0.0, 0.0, size)


def _module_socket_names(kit: str) -> list[str]:
    """Kit の要求を満たす Socket 名を決定的順序で返す。"""
    names = list(required_sockets(kit))
    # 名前指定の無い Kit でも最低個数は満たす（既定は socket_top を補う）。
    if len(names) < required_socket_min(kit) and "socket_top" not in names:
        names.insert(0, "socket_top")
    return names


def _build_module(spec: ModuleSpec, size: float) -> dict:
    """Blender 上に1モジュールを構築し、実測値から manifest エントリを返す。"""
    _reset_scene()

    # 本体メッシュ（bottom-center pivot にするため原点を底面へ）。
    bpy.ops.mesh.primitive_cube_add(size=size, location=(0, 0, size / 2.0))
    body = bpy.context.active_object
    body.name = f"{spec.kit}_{spec.name}_{REQUIRED_LOD}"

    # Socket Empty（接続点）。Kit 別の必須 Socket を実際に生成する（3.7-5）。
    socket_objs = []
    for socket_name in _module_socket_names(spec.kit):
        bpy.ops.object.empty_add(type="PLAIN_AXES", location=_socket_location(socket_name, size))
        socket = bpy.context.active_object
        socket.name = socket_name
        socket_objs.append(socket)

    # Interaction Point。
    bpy.ops.object.empty_add(type="ARROWS", location=(0, -size / 2.0, size / 2.0))
    ip = bpy.context.active_object
    ip.name = "ip_use"

    # Collider（別メッシュ、命名規約 UCX_ に準拠）。
    bpy.ops.mesh.primitive_cube_add(size=size, location=(0, 0, size / 2.0))
    collider = bpy.context.active_object
    collider.name = f"{COLLIDER_NAME_PREFIX}{spec.kit}_{spec.name}"

    # ここから先は「実測」のみ。ハードコードすると CI ゲートが自己証明になる（3.7-1）。
    created = [body, collider, ip, *socket_objs]

    return {
        "asset_id": spec.asset_id,
        "version": spec.version,
        "kit": spec.kit,
        "name": spec.name,
        "glb": spec.glb_filename,
        "triangles": _triangle_count(body),
        "collider_triangles": _triangle_count(collider),
        "colliders": sorted(
            o.name for o in created if o.type == "MESH" and _is_collider_name(o.name)
        ),
        "has_collider": any(o.type == "MESH" and _is_collider_name(o.name) for o in created),
        "sockets": sorted(o.name for o in socket_objs),
        "interaction_points": sorted(
            o.name for o in created if o.type == "EMPTY" and o.name.startswith("ip_")
        ),
        "lods": sorted({_lod_of(o.name) for o in created if o.type == "MESH"} - {""}),
        "negative_scale": any(_has_negative_scale(o) for o in created),
        "non_manifold_edges": {
            "body": _non_manifold_edge_count(body),
            "collider": _non_manifold_edge_count(collider),
        },
    }


def _is_collider_name(name: str) -> bool:
    return name.startswith(COLLIDER_NAME_PREFIX)


def _lod_of(name: str) -> str:
    """オブジェクト名末尾の LOD サフィックスを取り出す（無ければ空文字）。"""
    tail = name.rsplit("_", 1)[-1]
    return tail if tail.startswith("LOD") else ""


def _has_negative_scale(obj) -> bool:
    """ワールド行列 determinant / スケール符号から負スケールを実測する（3.7-1）。

    ミラー等で負スケールが混入すると法線が反転し Unity 側で裏返るため、
    生成時点で検出できるようにする。
    """
    if obj.matrix_world.determinant() < 0:
        return True
    # Empty は行列が単位でも scale だけ負のことがあるため符号も見る。
    return any(component < 0 for component in obj.matrix_world.to_scale())


def _non_manifold_edge_count(obj) -> int:
    """non-manifold なエッジ数を bmesh で数える（3.7-2）。メッシュ以外は 0。"""
    if obj.type != "MESH":
        return 0
    bm = bmesh.new()
    try:
        bm.from_mesh(obj.data)
        return sum(1 for edge in bm.edges if not edge.is_manifold)
    finally:
        bm.free()


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
