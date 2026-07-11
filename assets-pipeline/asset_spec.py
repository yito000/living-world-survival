"""Shared asset specification & deterministic id/version helpers (MVP 15章).

Imported by both ``generate.py`` (Blender) and ``validate.py`` (pure Python) so
that the deterministic contract — same (seed, module_size) → same asset_id /
version — lives in exactly one place. This module has **no Blender dependency**.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass

# MVP 15章 Kit / モジュール一覧。
KITS: dict[str, list[str]] = {
    "mine": ["floor", "wall", "entrance", "ore_node_base"],
    "camp": ["ground_tile", "house_shell", "storage", "disposal"],
    "production": ["forge", "anvil", "cooking_station", "farm_plot"],
    "buyer": ["stall", "sign", "spawn_marker"],
    "nature": ["rock", "tree_stump", "fence"],
}

# 三角形バジェット（モジュールあたりの上限。M0 は素朴なプリミティブなので緩め）。
TRIANGLE_BUDGET = 5000

# 各モジュールに最低1つの Socket / Interaction Point / Collider / LOD0 を要求。
REQUIRED_SOCKET_MIN = 1
REQUIRED_LOD = "LOD0"


@dataclass(frozen=True)
class ModuleSpec:
    kit: str
    name: str
    seed: int
    module_size: int

    @property
    def key(self) -> str:
        return f"{self.kit}/{self.name}"

    @property
    def asset_id(self) -> str:
        """決定的な asset_id（seed/size/kit/name から算出）。"""
        h = hashlib.sha256(
            f"{self.kit}:{self.name}:{self.seed}:{self.module_size}".encode()
        ).hexdigest()
        return f"asset_{h[:16]}"

    @property
    def version(self) -> str:
        """決定的な version（同じ入力→同じ値）。"""
        h = hashlib.sha256(
            f"v1:{self.kit}:{self.name}:{self.seed}:{self.module_size}".encode()
        ).hexdigest()
        return f"1.0.0+{h[:8]}"

    @property
    def glb_filename(self) -> str:
        return f"{self.kit}_{self.name}.glb"


def iter_modules(seed: int, module_size: int) -> list[ModuleSpec]:
    """全 Kit の全モジュールを決定的順序で列挙する。"""
    mods: list[ModuleSpec] = []
    for kit in sorted(KITS):
        for name in KITS[kit]:
            mods.append(ModuleSpec(kit=kit, name=name, seed=seed, module_size=module_size))
    return mods
