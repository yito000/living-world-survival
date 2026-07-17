#!/usr/bin/env python3
"""Soak CSV の判定（10B 3.2 / MVP第18.1）。

soak.sh が出した CSV を読み、次を判定する:

- **memory**: 各サービスの RSS を線形回帰し、傾き（bytes/hour）が閾値を超えたら
  リーク疑い。**どのサービスか** を名指しする（落とし穴: リーク疑い時はどの
  サービスかを CSV から特定する）。
- **Outbox 滞留**: 未 publish 件数の上限と、最古メッセージの滞留秒数。
  Relay が追随できていないと単調増加する。
- **event lag**: domain_events の最新から Consumer 処理位置までの差の上限。
- **tick**: DS tick_ms P95 の劣化（前半 vs 後半の比較）。

RSS 増加と Outbox 滞留は原因が別なので、両方を独立に判定して個別に報告する
（落とし穴 6章「Outbox 滞留 vs リーク の切り分け」）。

usage: soak_assert.py <soak_csv> [--json out.json]
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

# --- 閾値（Config Default）---------------------------------------------------

# RSS の増加が 1 時間あたりこれを超えたらリーク疑い。Go/Python のヒープは
# 起動直後に伸びてから安定するので、多少の増加は許容する。
RSS_SLOPE_LIMIT_BYTES_PER_HOUR = 50 * 1024 * 1024  # 50 MiB/h

# 未 publish の Outbox がこの件数を超えたら Relay が追随できていない。
OUTBOX_DEPTH_LIMIT = 500
# 最古の未 publish がこの秒数を超えて滞留していたら詰まっている。
OUTBOX_OLDEST_AGE_LIMIT_SEC = 60.0
# Consumer の遅れの上限。
EVENT_LAG_LIMIT_SEC = 60.0
# tick P95 が前半比でこの倍率を超えて悪化したら劣化。
TICK_DEGRADE_RATIO = 1.5

# リーク判定に必要な最低サンプル数。2 点で回帰しても意味が無い。
MIN_SAMPLES_FOR_SLOPE = 5

# 起動直後のウォームアップ（ヒープ確保・キャッシュ充填・接続確立）は
# 「ramp してから plateau」であって単調増加リークではない。この区間を回帰へ
# 混ぜると、16→60MiB のような起動ランプを時給へ外挿して巨大な偽の傾きになる
# （event_lag も、最初の 1 サンプルが loadgen 起動前で古い occurred_at を拾い得る）。
# 既定でこの秒数ぶん先頭を捨てる。full Soak(4h) では誤差だが、短縮 Soak では重要。
# ただしウォームアップ除外で有効サンプルが MIN を割るなら、捨てずに全点を使い
# 「短すぎて判定不能」を NODATA で示す（黙って甘くしない）。
WARMUP_SKIP_SEC = 120.0

RSS_COLUMNS = {
    "rss_auth": "auth",
    "rss_api": "api",
    "rss_worldstate": "worldstate",
    "rss_llm_worker": "llm-worker",
}


@dataclass
class Check:
    name: str
    status: str  # PASS / FAIL / NODATA
    detail: str


@dataclass
class Report:
    checks: list[Check] = field(default_factory=list)

    def add(self, name: str, status: str, detail: str) -> None:
        self.checks.append(Check(name, status, detail))

    @property
    def failed(self) -> bool:
        return any(c.status == "FAIL" for c in self.checks)


def _floats(rows: list[dict[str, str]], column: str) -> list[tuple[float, float]]:
    """(elapsed_sec, value) の系列を返す。空欄/非数値は「観測できなかった」点として捨てる。

    捨てるのは 0 埋めしないため: 欠測を 0 にすると、リークの傾きも Lag も過小評価する。
    """
    out: list[tuple[float, float]] = []
    for row in rows:
        raw = (row.get(column) or "").strip()
        if not raw:
            continue
        try:
            value = float(raw)
            elapsed = float((row.get("elapsed_sec") or "0").strip())
        except ValueError:
            continue
        out.append((elapsed, value))
    return out


def steady_state(
    points: list[tuple[float, float]], warmup_sec: float = WARMUP_SKIP_SEC
) -> list[tuple[float, float]]:
    """先頭のウォームアップ区間（起動 ramp）を落として定常状態のサンプルだけを返す。

    純粋なフィルタ。除外の結果が短すぎるかどうかの判断（NODATA にするか）は
    呼び出し側が行う。ここで全点へフォールバックすると、ウォームアップ汚染された
    データで誤って FAIL を出してしまう。
    """
    return [p for p in points if p[0] >= warmup_sec]


def linear_slope(points: list[tuple[float, float]]) -> float | None:
    """最小二乗法で傾き（value / 秒）を返す。点が足りない/x が一定なら None。"""
    n = len(points)
    if n < 2:
        return None
    sx = sum(p[0] for p in points)
    sy = sum(p[1] for p in points)
    sxx = sum(p[0] * p[0] for p in points)
    sxy = sum(p[0] * p[1] for p in points)
    denom = n * sxx - sx * sx
    if denom == 0:  # 全て同じ時刻 = 傾きは定義できない
        return None
    return (n * sxy - sx * sy) / denom


def check_memory(report: Report, rows: list[dict[str, str]]) -> None:
    """RSS の傾きでリーク疑いを判定し、疑わしいサービスを名指しする。"""
    for column, service in RSS_COLUMNS.items():
        # ウォームアップ（起動直後の ramp）を落としてから傾きを見る。
        points = steady_state(_floats(rows, column))
        if len(points) < MIN_SAMPLES_FOR_SLOPE:
            report.add(
                f"memory:{service}",
                "NODATA",
                f"定常サンプル {len(points)} 点（傾き判定には {MIN_SAMPLES_FOR_SLOPE} 点以上必要。"
                f"短縮 Soak では起動 ramp 除外後に不足しやすい — soak-short/full で再判定）",
            )
            continue
        slope_per_sec = linear_slope(points)
        if slope_per_sec is None:
            report.add(f"memory:{service}", "NODATA", "傾きを計算できません")
            continue

        per_hour = slope_per_sec * 3600.0
        first_mib = points[0][1] / 1024 / 1024
        last_mib = points[-1][1] / 1024 / 1024
        detail = (
            f"{per_hour / 1024 / 1024:+.1f} MiB/h "
            f"({first_mib:.0f} -> {last_mib:.0f} MiB, {len(points)} samples)"
        )
        if per_hour > RSS_SLOPE_LIMIT_BYTES_PER_HOUR:
            report.add(f"memory:{service}", "FAIL", f"リーク疑い: {detail}")
        else:
            report.add(f"memory:{service}", "PASS", detail)


def check_outbox(report: Report, rows: list[dict[str, str]]) -> None:
    depth = _floats(rows, "outbox_depth")
    if not depth:
        report.add("outbox:depth", "NODATA", "outbox_depth の観測がありません")
    else:
        peak = max(v for _, v in depth)
        # sampler は観測失敗を -1 で表す（据え置きによる誤読を避けるため）。
        unknown = sum(1 for _, v in depth if v < 0)
        detail = f"peak={peak:.0f} (limit {OUTBOX_DEPTH_LIMIT}, {len(depth)} samples)"
        if unknown:
            detail += f", {unknown} 回は観測失敗(-1)"
        if peak > OUTBOX_DEPTH_LIMIT:
            report.add("outbox:depth", "FAIL", f"Relay が追随できていません: {detail}")
        else:
            report.add("outbox:depth", "PASS", detail)

    age = _floats(rows, "outbox_oldest_age_sec")
    if not age:
        report.add("outbox:age", "NODATA", "outbox_oldest_age_sec の観測がありません")
        return
    peak_age = max(v for _, v in age)
    detail = f"peak={peak_age:.1f}s (limit {OUTBOX_OLDEST_AGE_LIMIT_SEC}s)"
    if peak_age > OUTBOX_OLDEST_AGE_LIMIT_SEC:
        report.add("outbox:age", "FAIL", f"最古メッセージが滞留しています: {detail}")
    else:
        report.add("outbox:age", "PASS", detail)


def check_event_lag(report: Report, rows: list[dict[str, str]]) -> None:
    for column, label in (("event_lag_sec", "api"), ("ws_event_lag_sec", "worldstate")):
        raw = _floats(rows, column)
        if not raw:
            report.add(f"event_lag:{label}", "NODATA", f"{column} の観測がありません")
            continue
        # 先頭サンプルは loadgen がイベントを流す前で、古い occurred_at（前の
        # 負荷/復旧テストの残り）を拾って巨大な見かけ Lag になり得る。定常区間で見る。
        points = steady_state(raw)
        if len(points) < MIN_SAMPLES_FOR_SLOPE:
            report.add(
                f"event_lag:{label}",
                "NODATA",
                f"定常サンプル {len(points)} 点（短縮 Soak では起動 ramp 除外後に不足。"
                f"soak-short/full で再判定）",
            )
            continue
        peak = max(v for _, v in points)
        detail = f"peak={peak:.1f}s (limit {EVENT_LAG_LIMIT_SEC}s, {len(points)} samples)"
        if peak > EVENT_LAG_LIMIT_SEC:
            report.add(f"event_lag:{label}", "FAIL", f"Consumer が遅れています: {detail}")
        else:
            report.add(f"event_lag:{label}", "PASS", detail)


def check_tick(report: Report, rows: list[dict[str, str]]) -> None:
    """tick P95 の劣化を前半 vs 後半で見る（リーク/断片化の兆候）。"""
    points = _floats(rows, "ds_tick_p95_sec")
    if len(points) < 4:
        report.add(
            "tick:p95",
            "NODATA",
            f"サンプル {len(points)} 点（DS 未起動なら tick は観測されません）",
        )
        return
    half = len(points) // 2
    early = [v for _, v in points[:half]]
    late = [v for _, v in points[half:]]
    early_avg = sum(early) / len(early)
    late_avg = sum(late) / len(late)
    if early_avg <= 0:
        report.add("tick:p95", "NODATA", "前半の tick P95 が 0 で比較できません")
        return
    ratio = late_avg / early_avg
    detail = (
        f"{early_avg * 1000:.1f}ms -> {late_avg * 1000:.1f}ms "
        f"(x{ratio:.2f}, limit x{TICK_DEGRADE_RATIO})"
    )
    if ratio > TICK_DEGRADE_RATIO:
        report.add("tick:p95", "FAIL", f"Tick が劣化しています: {detail}")
    else:
        report.add("tick:p95", "PASS", detail)


def main() -> int:
    ap = argparse.ArgumentParser(description="Soak CSV の判定（10B 3.2）")
    ap.add_argument("csv", type=Path, help="soak.sh が出力した CSV")
    ap.add_argument("--json", type=Path, default=None, help="判定結果の JSON 出力先")
    args = ap.parse_args()

    if not args.csv.exists():
        print(f"soak_assert: {args.csv} がありません", file=sys.stderr)
        return 2

    with args.csv.open(newline="") as fh:
        rows = list(csv.DictReader(fh))

    if not rows:
        print("soak_assert: CSV にサンプルがありません", file=sys.stderr)
        return 2

    report = Report()
    check_memory(report, rows)
    check_outbox(report, rows)
    check_event_lag(report, rows)
    check_tick(report, rows)

    duration = float(rows[-1].get("elapsed_sec") or 0)
    print(f"== soak_assert: {args.csv} ({len(rows)} samples / {duration:.0f}s) ==")
    for check in report.checks:
        color = {"PASS": "\033[32m", "FAIL": "\033[31m"}.get(check.status, "\033[33m")
        print(f"  {color}{check.status:<6}\033[0m {check.name:<22} {check.detail}")

    verdict = "FAIL" if report.failed else "PASS"
    print(f"soak_assert: {verdict}")

    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(
            json.dumps(
                {
                    "source": str(args.csv),
                    "samples": len(rows),
                    "duration_sec": duration,
                    "verdict": verdict,
                    "checks": [vars(c) for c in report.checks],
                    "thresholds": {
                        "rss_slope_bytes_per_hour": RSS_SLOPE_LIMIT_BYTES_PER_HOUR,
                        "outbox_depth": OUTBOX_DEPTH_LIMIT,
                        "outbox_oldest_age_sec": OUTBOX_OLDEST_AGE_LIMIT_SEC,
                        "event_lag_sec": EVENT_LAG_LIMIT_SEC,
                        "tick_degrade_ratio": TICK_DEGRADE_RATIO,
                    },
                },
                indent=2,
            )
            + "\n"
        )
        print(f"soak_assert: JSON -> {args.json}")

    return 1 if report.failed else 0


if __name__ == "__main__":
    sys.exit(main())
