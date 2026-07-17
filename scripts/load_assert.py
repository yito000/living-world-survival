#!/usr/bin/env python3
"""M7 負荷試験レポートの Gate 判定（10B 3.1 / AT-020）。

scripts/load_test.sh が出した build/reports/load_*.json を読み、第3章の暫定 Gate を判定する。

    Tick        P95 <= 40ms / P99 <= 50ms   （auth: ds_tick_seconds）
    通常操作     P95 <= 200ms                （api: grpc_server_handling_seconds, AppendEvents 等）
    購入        P95 <= 500ms                （api: grpc_server_handling_seconds の
                                             method=CommitPurchase）

判定は **PASS または RECORD** である（10B 3.1 / AT-020）。未達は失敗ではなく
「測定結果とボトルネックを数値付きで記録する」もの。したがって終了コードは PASS/RECORD とも 0
で、レポート自体が壊れている（読めない/必須フィールド欠落）ときだけ非 0 を返す。

系列が存在しないことは 0 でも PASS でもない。Prometheus はラベル付き系列を最初の観測まで
公開しないので、「まだ何も起きていない」正当な欠落がありうる。その場合は明示的に "no data" と
出し、Gate は RECORD にする（10B 6章「skip を成功と誤認しない」と同じ姿勢）。

使い方:
    python3 scripts/load_assert.py [report.json]
    （引数省略時は build/reports/load_*.json の最新を使う）
"""

from __future__ import annotations

import json
import sys
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

# Gate（10B 3.1 / MVP 第3章）。秒で持つ。
TICK_P95_MAX = 0.040
TICK_P99_MAX = 0.050
OP_P95_MAX = 0.200
PURCHASE_P95_MAX = 0.500

# 「通常操作（採取/使用/Pickup）」に相当する RPC。loadgen はこれらを AppendEvents で送る。
OP_METHODS = ("AppendEvents",)
PURCHASE_METHODS = ("CommitPurchase",)


@dataclass(frozen=True)
class Sample:
    """Prometheus exposition の 1 行（name/labels/value）。"""

    name: str
    labels: dict[str, str]
    value: float


@dataclass
class Histogram:
    """1 ヒストグラム系列（bucket 群 + count + sum）。値は差分適用後の想定。"""

    buckets: dict[float, float] = field(default_factory=dict)  # le -> 累積 count
    count: float = 0.0
    sum: float = 0.0


def parse_exposition(lines: list[str]) -> list[Sample]:
    """Prometheus テキスト形式の行を Sample へ。# 行と壊れた行は捨てる。"""
    out: list[Sample] = []
    for raw in lines:
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "{" in line:
            name, rest = line.split("{", 1)
            label_str, _, value_str = rest.partition("}")
            labels = parse_labels(label_str)
        else:
            parts = line.split()
            if len(parts) < 2:
                continue
            name, value_str, labels = parts[0], parts[1], {}
        try:
            value = float(value_str.strip().split()[0])
        except (ValueError, IndexError):
            continue
        out.append(Sample(name=name.strip(), labels=labels, value=value))
    return out


def parse_labels(label_str: str) -> dict[str, str]:
    """`a="1",b="2"` を dict に。値中のカンマは想定しない（本リポジトリのラベルは単純）。"""
    labels: dict[str, str] = {}
    for part in label_str.split(","):
        if "=" not in part:
            continue
        k, _, v = part.partition("=")
        labels[k.strip()] = v.strip().strip('"')
    return labels


def collect_histogram(
    samples: list[Sample], metric: str, match: dict[str, tuple[str, ...]] | None = None
) -> Histogram:
    """metric の bucket/count/sum を、ラベル条件に合う系列すべてで合算する。

    合算してよいのは同一 bucket 境界を共有する系列だけ。本リポジトリのヒストグラムは
    services/common/obs の共通 bucket（auth の tick も専用だが単一定義）なので成立する。
    """
    hist = Histogram()
    for s in samples:
        base = s.name
        if base.endswith("_bucket"):
            kind = "bucket"
            base = base[: -len("_bucket")]
        elif base.endswith("_count"):
            kind = "count"
            base = base[: -len("_count")]
        elif base.endswith("_sum"):
            kind = "sum"
            base = base[: -len("_sum")]
        else:
            continue
        if base != metric:
            continue
        if match and not labels_match(s.labels, match):
            continue
        if kind == "bucket":
            le_raw = s.labels.get("le")
            if le_raw is None:
                continue
            le = float("inf") if le_raw == "+Inf" else float(le_raw)
            hist.buckets[le] = hist.buckets.get(le, 0.0) + s.value
        elif kind == "count":
            hist.count += s.value
        else:
            hist.sum += s.value
    return hist


def labels_match(labels: dict[str, str], match: dict[str, tuple[str, ...]]) -> bool:
    return all(labels.get(k) in allowed for k, allowed in match.items())


def subtract(after: Histogram, before: Histogram) -> Histogram:
    """負荷窓だけを見るため after - before を取る。

    ヒストグラムは単調増加カウンタなので、差分こそが「この負荷実行で観測された分布」。
    差分を取らないと、直前のスモークや前回実行の観測が Gate に混ざる。サービスが途中で
    再起動して after < before になった場合は 0 で切り上げる（負の count は無意味）。
    """
    out = Histogram()
    for le in set(after.buckets) | set(before.buckets):
        out.buckets[le] = max(0.0, after.buckets.get(le, 0.0) - before.buckets.get(le, 0.0))
    out.count = max(0.0, after.count - before.count)
    out.sum = max(0.0, after.sum - before.sum)
    return out


def quantile(hist: Histogram, q: float) -> float | None:
    """Prometheus 互換の histogram_quantile（bucket 内は線形補間）。

    データが無ければ None（＝"no data"）。0 を返して PASS 扱いにはしない。
    """
    if not hist.buckets:
        return None
    ordered = sorted(hist.buckets.items())
    total = ordered[-1][1]  # +Inf バケット = 総数
    if total <= 0:
        return None
    rank = q * total
    lower_le = 0.0
    lower_count = 0.0
    for le, cum in ordered:
        if cum >= rank:
            if le == float("inf"):
                # 最上位バケットに落ちた＝有限境界を超えている。境界値までしか言えない。
                return lower_le
            if cum == lower_count:
                return le
            frac = (rank - lower_count) / (cum - lower_count)
            return lower_le + (le - lower_le) * frac
        lower_le, lower_count = le, cum
    return ordered[-1][0]


@dataclass
class GateResult:
    name: str
    measured: float | None
    threshold: float
    status: str  # PASS / RECORD
    note: str = ""
    observations: float = 0.0


def fmt_ms(v: float | None) -> str:
    return "no data" if v is None else f"{v * 1000:.1f}ms"


def display_width(s: str) -> int:
    """端末上の表示幅。全角（W/F）は 2 桁として数え、表がずれないようにする。"""
    return sum(2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in s)


def lpad(s: str, n: int) -> str:
    return s + " " * max(0, n - display_width(s))


def rpad(s: str, n: int) -> str:
    return " " * max(0, n - display_width(s)) + s


def judge(
    name: str, measured: float | None, threshold: float, observations: float, note: str = ""
) -> GateResult:
    if measured is None:
        return GateResult(name, None, threshold, "RECORD", note or "系列なし（no data）", 0.0)
    status = "PASS" if measured <= threshold else "RECORD"
    return GateResult(name, measured, threshold, status, note, observations)


def service_samples(report: dict, phase: str, service: str) -> list[Sample] | None:
    """スクレイプできなかったサービスは None（空リストと区別する）。"""
    node = report.get("metrics", {}).get(phase, {}).get("services", {}).get(service)
    if node is None or not node.get("scraped"):
        return None
    return parse_exposition(node.get("lines", []))


def window_histogram(
    report: dict, service: str, metric: str, match: dict[str, tuple[str, ...]] | None = None
) -> Histogram | None:
    after = service_samples(report, "after", service)
    if after is None:
        return None
    before = service_samples(report, "before", service) or []
    return subtract(
        collect_histogram(after, metric, match),
        collect_histogram(before, metric, match),
    )


def gauge(report: dict, service: str, metric: str) -> float | None:
    samples = service_samples(report, "after", service)
    if samples is None:
        return None
    vals = [s.value for s in samples if s.name == metric]
    return max(vals) if vals else None


def counter_delta(report: dict, service: str, metric: str) -> dict[str, float]:
    after = service_samples(report, "after", service)
    if after is None:
        return {}
    before = service_samples(report, "before", service) or []
    prev = {label_key(s.labels): s.value for s in before if s.name == metric}
    out: dict[str, float] = {}
    for s in after:
        if s.name != metric:
            continue
        key = label_key(s.labels)
        out[key] = max(0.0, s.value - prev.get(key, 0.0))
    return out


def label_key(labels: dict[str, str]) -> str:
    return ",".join(f"{k}={v}" for k, v in sorted(labels.items())) or "-"


def evaluate(report: dict) -> tuple[list[GateResult], dict[str, object]]:
    gates: list[GateResult] = []

    # --- Tick（出所は DS の自報告。ハーネス RTT ではない: 10B 6章） ---
    tick_source = report.get("tick_source", "unknown")
    tick = window_histogram(report, "auth", "ds_tick_seconds")
    if tick is None:
        miss = "auth の metrics を取得できず"
        gates.append(GateResult("Tick P95", None, TICK_P95_MAX, "RECORD", miss))
        gates.append(GateResult("Tick P99", None, TICK_P99_MAX, "RECORD", miss))
    else:
        p95, p99 = quantile(tick, 0.95), quantile(tick, 0.99)
        if tick_source != "real_ds":
            # 合成 tick は「DS が自報告した計測値」ではない。数値は記録するが PASS にはしない。
            note = (
                f"tick_source={tick_source}"
                "（合成値であり計測値ではない: 実 DS を DS=1 で起動すること）"
            )
            gates.append(GateResult("Tick P95", p95, TICK_P95_MAX, "RECORD", note, tick.count))
            gates.append(GateResult("Tick P99", p99, TICK_P99_MAX, "RECORD", note, tick.count))
        else:
            gates.append(judge("Tick P95", p95, TICK_P95_MAX, tick.count, "tick_source=real_ds"))
            gates.append(judge("Tick P99", p99, TICK_P99_MAX, tick.count, "tick_source=real_ds"))

    # --- 通常操作（採取/使用/Pickup = AppendEvents） ---
    ops = window_histogram(
        report, "api", "grpc_server_handling_seconds", {"method": OP_METHODS}
    )
    if ops is None:
        gates.append(
            GateResult("通常操作 P95", None, OP_P95_MAX, "RECORD", "api の metrics を取得できず")
        )
    else:
        gates.append(
            judge(
                "通常操作 P95",
                quantile(ops, 0.95),
                OP_P95_MAX,
                ops.count,
                f"method={'/'.join(OP_METHODS)}",
            )
        )

    # --- 購入 ---
    buy = window_histogram(
        report, "api", "grpc_server_handling_seconds", {"method": PURCHASE_METHODS}
    )
    if buy is None:
        gates.append(
            GateResult("購入 P95", None, PURCHASE_P95_MAX, "RECORD", "api の metrics を取得できず")
        )
    else:
        gates.append(
            judge(
                "購入 P95",
                quantile(buy, 0.95),
                PURCHASE_P95_MAX,
                buy.count,
                f"method={'/'.join(PURCHASE_METHODS)}",
            )
        )

    # --- ボトルネック記録用の参考値（Gate ではない / AT-020「数値付きで記録」） ---
    db = window_histogram(report, "api", "db_query_duration_seconds")
    ws = window_histogram(report, "worldstate", "worldstate_projection_duration_seconds")
    context: dict[str, object] = {
        "db_query_p95": fmt_ms(quantile(db, 0.95)) if db else "no data",
        "db_query_p99": fmt_ms(quantile(db, 0.99)) if db else "no data",
        "outbox_depth": gauge(report, "api", "outbox_depth"),
        "outbox_oldest_age_seconds": gauge(report, "api", "outbox_oldest_age_seconds"),
        "event_lag_seconds": gauge(report, "api", "event_lag_seconds"),
        "worldstate_event_lag_seconds": gauge(report, "worldstate", "worldstate_event_lag_seconds"),
        "worldstate_projection_p95": fmt_ms(quantile(ws, 0.95)) if ws else "no data",
        "economy_purchases_total": counter_delta(report, "api", "economy_purchases_total"),
        "buyer_sold_out_total": counter_delta(report, "api", "buyer_sold_out_total"),
        "outbox_publish_total": counter_delta(report, "api", "outbox_publish_total"),
        "worldstate_events_processed_total": counter_delta(
            report, "worldstate", "worldstate_events_processed_total"
        ),
        "llm_decisions_total": counter_delta(report, "llm-worker", "llm_decisions_total"),
    }
    return gates, context


def print_report(report: dict, gates: list[GateResult], context: dict[str, object]) -> str:
    cfg = report.get("config", {})
    print("=" * 78)
    print("M7 負荷試験 Gate 判定（10B 3.1 / AT-020）")
    print("=" * 78)
    print(
        f"  規模        : players={cfg.get('players')} ai={cfg.get('ai')} "
        f"animals={cfg.get('animals')} duration={cfg.get('duration')}"
    )
    print(f"  tick_source : {report.get('tick_source')}")
    print(f"  scope       : {report.get('scope_note', '')}")
    print()
    print(f"  {lpad('Gate', 16)}{rpad('実測', 12)}{rpad('閾値', 12)}{rpad('観測数', 10)}  判定")
    print(f"  {'-' * 52}")
    for g in gates:
        obs = "-" if g.measured is None else f"{int(g.observations)}"
        print(
            f"  {lpad(g.name, 16)}{rpad(fmt_ms(g.measured), 12)}"
            f"{rpad(fmt_ms(g.threshold), 12)}{rpad(obs, 10)}  {g.status}"
        )
        if g.note:
            print(f"      └ {g.note}")
    print()
    print("  参考値（ボトルネック記録用。Gate ではない）:")
    for k, v in context.items():
        print(f"    {k:<34} {v if v is not None else 'no data'}")
    print()

    verdict = "PASS" if all(g.status == "PASS" for g in gates) else "RECORD"
    if verdict == "PASS":
        print("  判定: PASS — 第3章の暫定 Gate をすべて満たした。")
    else:
        missed = [g for g in gates if g.status == "RECORD"]
        print("  判定: RECORD — 未達/未測定を数値付きで記録する（AT-020: 未達は失敗ではない）。")
        for g in missed:
            # 閾値以内なのに RECORD なのは「そもそも計測値ではない」ケース（合成 tick 等）。
            # 「実測 > 閾値」と書くと嘘になるので、理由は note を出す。
            if g.measured is None or g.measured <= g.threshold:
                reason = g.note or "計測できず"
            else:
                reason = f"実測 {fmt_ms(g.measured)} > 閾値 {fmt_ms(g.threshold)}"
            print(f"    - {g.name}: {reason}")
    print("=" * 78)
    return verdict


def latest_report() -> Path:
    reports = sorted(Path("build/reports").glob("load_*.json"))
    reports = [p for p in reports if not p.name.endswith(".verdict.json")]
    if not reports:
        die(
            "build/reports に load_*.json がありません。"
            "先に scripts/load_test.sh を実行してください。"
        )
    return reports[-1]


def die(msg: str) -> None:
    print(f"load_assert.py: {msg}", file=sys.stderr)
    raise SystemExit(2)


def main(argv: list[str]) -> int:
    path = Path(argv[1]) if len(argv) > 1 else latest_report()
    if not path.exists():
        die(f"レポートがありません: {path}")
    try:
        report = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        die(f"レポートが JSON として読めません: {path}: {e}")
        return 2
    if report.get("schema") != "load_report/v1":
        die(f"未知の schema: {report.get('schema')!r}（load_report/v1 を期待）")
    if "metrics" not in report or "tick_source" not in report:
        die("レポートに metrics / tick_source がありません（壊れたレポート）")

    print(f"report: {path}\n")
    gates, context = evaluate(report)
    verdict = print_report(report, gates, context)

    # 判定をレポートへ書き戻す（後から報告書だけで受入判定が追えるように / 5.3）。
    report["verdict"] = {
        "result": verdict,
        "gates": [
            {
                "name": g.name,
                "measured_seconds": g.measured,
                "threshold_seconds": g.threshold,
                "observations": g.observations,
                "status": g.status,
                "note": g.note,
            }
            for g in gates
        ],
        "context": context,
    }
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"判定をレポートへ書き戻しました: {path}")
    # PASS / RECORD いずれも 0。壊れたレポートのみ非 0（die が 2 を返す）。
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
