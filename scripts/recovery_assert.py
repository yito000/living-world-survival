#!/usr/bin/env python3
"""再起動復旧テストの判定（10B 3.3 / MVP第12.1・16章 / AT-018・AT-019・AT-021）。

recovery_test.sh が出した build/reports/recovery_raw_<ts>.json（各シナリオの生検証結果）を読み、
次を判定して build/reports/recovery_<ts>.json に集計する:

- 各シナリオは FAIL の check が 1 つも無ければ PASS。NODATA は情報として残す（未実装/未観測の
  明示。落とし穴6章「実装が持たない挙動を PASS と偽らない」）。
- **復旧損失目標**（第3章）を独立に Gate する:
    - purchases_lost == 0（購入は 0 件失う。idempotency で再現）。
    - nonecon_restore_sec <= 5.0（非経済状態は 5 秒以内の snapshot+tail で復元）。

引数なしで最新の recovery_raw_*.json を拾う（make recovery が sh→py を無引数で連結するため）。

usage: recovery_assert.py [recovery_raw.json] [--json out.json]
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

REPORTS_DIR = Path("build/reports")

# 復旧損失の Gate（第3章）。
PURCHASES_LOST_LIMIT = 0
NONECON_RESTORE_LIMIT_SEC = 5.0


@dataclass
class ScenarioVerdict:
    id: str
    at: str
    title: str
    mode: str
    status: str  # PASS / FAIL
    reasons: list[str] = field(default_factory=list)
    checks: list[dict[str, str]] = field(default_factory=list)
    recovery: dict[str, object] = field(default_factory=dict)
    findings: list[str] = field(default_factory=list)


def newest_raw() -> Path | None:
    """最新の recovery_raw_*.json を返す（無ければ None）。"""
    candidates = sorted(REPORTS_DIR.glob("recovery_raw_*.json"))
    return candidates[-1] if candidates else None


def _num(value: object) -> float | None:
    if isinstance(value, bool):
        return float(value)
    if isinstance(value, int | float):
        return float(value)
    return None


def judge_scenario(raw: dict[str, object]) -> ScenarioVerdict:
    checks = list(raw.get("checks", []))  # type: ignore[arg-type]
    recovery = dict(raw.get("recovery", {}))  # type: ignore[arg-type]
    v = ScenarioVerdict(
        id=str(raw.get("id", "?")),
        at=str(raw.get("at", "")),
        title=str(raw.get("title", "")),
        mode=str(raw.get("mode", "?")),
        status="PASS",
        checks=checks,  # type: ignore[arg-type]
        recovery=recovery,
        findings=list(raw.get("findings", [])),  # type: ignore[arg-type]
    )

    fails = [c for c in checks if c.get("status") == "FAIL"]
    for c in fails:
        v.status = "FAIL"
        v.reasons.append(f"check FAIL: {c.get('name')} — {c.get('detail')}")

    # 復旧損失 Gate（値が記録されている場合のみ独立に判定する）。
    lost = _num(recovery.get("purchases_lost"))
    if lost is not None and lost > PURCHASES_LOST_LIMIT:
        v.status = "FAIL"
        v.reasons.append(f"復旧損失: 購入を {lost:.0f} 件失った（目標 {PURCHASES_LOST_LIMIT}）")

    restore = _num(recovery.get("nonecon_restore_sec"))
    if restore is not None and restore > NONECON_RESTORE_LIMIT_SEC:
        v.status = "FAIL"
        v.reasons.append(
            f"復旧損失: 非経済状態の復元 {restore:.2f}s（目標 ≤{NONECON_RESTORE_LIMIT_SEC}s）"
        )

    if v.status == "PASS":
        v.reasons.append(f"{len(checks)} checks 全て非 FAIL、復旧損失目標を満たす")
    return v


def main() -> int:
    ap = argparse.ArgumentParser(description="再起動復旧テストの判定（10B 3.3）")
    ap.add_argument(
        "raw",
        type=Path,
        nargs="?",
        default=None,
        help="recovery_test.sh の生データ JSON（省略時は最新の recovery_raw_*.json）",
    )
    ap.add_argument("--json", type=Path, default=None, help="判定結果の JSON 出力先")
    args = ap.parse_args()

    raw_path = args.raw or newest_raw()
    if raw_path is None or not raw_path.exists():
        print("recovery_assert: recovery_raw_*.json が見つかりません", file=sys.stderr)
        return 2

    try:
        doc = json.loads(raw_path.read_text())
    except json.JSONDecodeError as exc:
        print(f"recovery_assert: {raw_path} が壊れています: {exc}", file=sys.stderr)
        return 2

    scenarios = doc.get("scenarios", [])
    if not scenarios:
        print(f"recovery_assert: {raw_path} にシナリオがありません", file=sys.stderr)
        return 2

    verdicts = [judge_scenario(s) for s in scenarios]
    failed = any(v.status == "FAIL" for v in verdicts)

    print(f"== recovery_assert: {raw_path} ({len(verdicts)} scenarios) ==")
    for v in verdicts:
        color = "\033[32m" if v.status == "PASS" else "\033[31m"
        nodata = [c for c in v.checks if c.get("status") == "NODATA"]
        tag = f" [{len(nodata)} NODATA]" if nodata else ""
        print(f"  {color}{v.status:<6}\033[0m {v.id:<20} {v.at:<8} mode={v.mode}{tag}")
        for reason in v.reasons:
            print(f"         · {reason}")
        for c in nodata:
            print(f"         · NODATA {c.get('name')}: {c.get('detail')}")
        for finding in v.findings:
            print(f"         ! {finding}")

    verdict = "FAIL" if failed else "PASS"
    print(f"recovery_assert: {verdict}")

    ts = str(doc.get("generated_at", "unknown"))
    out = args.json or (REPORTS_DIR / f"recovery_{ts}.json")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(
            {
                "source": str(raw_path),
                "generated_at": ts,
                "ds_mode": doc.get("ds_mode"),
                "verdict": verdict,
                "gates": {
                    "purchases_lost_limit": PURCHASES_LOST_LIMIT,
                    "nonecon_restore_limit_sec": NONECON_RESTORE_LIMIT_SEC,
                },
                "scenarios": [
                    {
                        "id": v.id,
                        "at": v.at,
                        "title": v.title,
                        "mode": v.mode,
                        "status": v.status,
                        "reasons": v.reasons,
                        "recovery": v.recovery,
                        "checks": v.checks,
                        "findings": v.findings,
                    }
                    for v in verdicts
                ],
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n"
    )
    print(f"recovery_assert: JSON -> {out}")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
