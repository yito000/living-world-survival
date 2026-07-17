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

# --- 三角形バジェット（M7 3.7-3 で Kit/用途別へ厳格化） -------------------------
#
# M0 では全 Kit 一律 5000 と緩かったが、実形状（Kit ごとの用途と配置密度）に
# 合わせた現実的な上限へ引き下げる。方針:
#   - 大量にタイル配置される構造材（mine/nature）ほど厳しく
#   - 単体で置かれ視線が集まる設備（production）は緩め
# 未知 Kit は TRIANGLE_BUDGET_DEFAULT にフォールバックする。
TRIANGLE_BUDGET_DEFAULT = 1500
TRIANGLE_BUDGET_BY_KIT: dict[str, int] = {
    "mine": 800,  # floor/wall を大量にタイル配置するため最も厳しく
    "camp": 1200,  # house_shell が最大。拠点は視界に入る数が中程度
    "production": 1500,  # forge/anvil 等の設備。単体配置で視線が集まる
    "buyer": 1000,  # stall/sign。街に数点のみ
    "nature": 600,  # rock/tree_stump を高密度にインスタンス配置するため最厳
}

# Server 用軽量メッシュ（Collider 中心）は描画メッシュと別バジェット（3.7-3）。
# 物理形状は凸包相当で足りるため、描画側より一桁小さく抑える。
COLLIDER_TRIANGLE_BUDGET_DEFAULT = 128
COLLIDER_TRIANGLE_BUDGET_BY_KIT: dict[str, int] = {
    "mine": 64,
    "camp": 128,
    "production": 128,
    "buyer": 96,
    "nature": 64,
}

# Collider の命名規約（3.7-4）。
COLLIDER_NAME_PREFIX = "UCX_"

# --- Socket 要件（M7 3.7-5 で Kit 別へ拡張） ------------------------------------
#
# 各 Kit が最低限備えるべき Socket 名。未知 Kit は名前指定なし＋
# REQUIRED_SOCKET_MIN_DEFAULT 個以上、にフォールバックする。
REQUIRED_SOCKET_MIN_DEFAULT = 1
REQUIRED_SOCKETS_BY_KIT: dict[str, tuple[str, ...]] = {
    "mine": ("socket_top",),
    "camp": ("socket_top",),
    # production は素材の投入口を必須にする（設備は必ず入力を受ける）。
    "production": ("socket_top", "socket_input"),
    "buyer": ("socket_top",),
    "nature": ("socket_top",),
}

REQUIRED_LOD = "LOD0"


def triangle_budget(kit: str) -> int:
    """描画メッシュの三角形上限を Kit 別に返す（未知 Kit は既定値）。"""
    return TRIANGLE_BUDGET_BY_KIT.get(kit, TRIANGLE_BUDGET_DEFAULT)


def collider_triangle_budget(kit: str) -> int:
    """Collider（Server 用軽量メッシュ）の三角形上限を Kit 別に返す。"""
    return COLLIDER_TRIANGLE_BUDGET_BY_KIT.get(kit, COLLIDER_TRIANGLE_BUDGET_DEFAULT)


def required_sockets(kit: str) -> tuple[str, ...]:
    """Kit が必須とする Socket 名を返す（未知 Kit は名前指定なし＝空）。"""
    return REQUIRED_SOCKETS_BY_KIT.get(kit, ())


def required_socket_min(kit: str) -> int:
    """Kit が必須とする Socket 個数。名前指定がある場合はその個数を下限とする。"""
    return max(REQUIRED_SOCKET_MIN_DEFAULT, len(required_sockets(kit)))


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
