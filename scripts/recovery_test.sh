#!/usr/bin/env bash
# M7 再起動復旧テスト（10B 3.3 / MVP第12.1・16章 / AT-018・AT-019・AT-021）。
#
# サービス/DB/NATS の再起動をまたいで、snapshot staging→checksum→active・Outbox flush・
# inbox_dedup による event_id 重複排除の整合を検証し、復旧損失目標（購入0件・非経済状態
# 5秒以内）を満たすことを示す。5 シナリオをそれぞれサブコマンドで実行し、既定（引数なし）は
# 全シナリオを回して build/reports/recovery_raw_<ts>.json を書く。判定は recovery_assert.py。
#
#   bash scripts/recovery_test.sh              # 全シナリオ（make recovery が呼ぶ）
#   bash scripts/recovery_test.sh scenario3    # 個別シナリオ
#
# シナリオ:
#   1 DS crash → 別 DS で snapshot(active)+event tail から復元（AT-018）
#   2 購入応答直後の crash → inventory/currency 保持・二重付与/欠落なし（AT-019/021）
#   3 NATS 再起動 → outbox 滞留→順送 Flush、inbox_dedup で一度だけ処理
#   4 DB 再起動 → Outbox Relay 回復、idempotency_key で再送が二重確定しない
#   5 Corrupt Snapshot → staging→checksum で弾き active は健全な直前を指す（16章）
#
# DS は Unity/FishNet で Go/bash から駆動できない（10B 0.1/0.2）。DS は m2smoke/m6check と同じく
# 模擬（recoverygen が RuntimePersistence 役を演じる）。DS=1 かつ実 DS 稼働時は crash を
# stop_ds.sh で実機の SIGTERM として起こす。どちらで走ったかは各結果の mode に記録する。
#
# 注意（NATS の JetStream 状態）: nats.conf の /data/jetstream は volume ではなく ephemeral。
# よって NATS は down ではなく stop/start で再起動する（stream 状態は消えるが、relay が未 publish
# を republish するので outbox flush の検証は成立する）。
#
# CRITICAL: 本テストはコンテナを stop/start する。trap で必ずスタックを復元して終了する。
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if [ -f .env ]; then
  set -a; # shellcheck disable=SC1091
  . ./.env; set +a
fi

COMPOSE="docker compose -f infra/docker-compose.yml"
API_PORT="${API_PORT:-8082}"
WORLDSTATE_PORT="${WORLDSTATE_PORT:-8083}"

export DATABASE_URL_HOST="${DATABASE_URL_HOST:-postgres://survival:survival@localhost:5432/survival?sslmode=disable}"
export NATS_URL_HOST="${NATS_URL_HOST:-nats://localhost:4222}"
export API_GRPC_ADDR="${API_GRPC_ADDR:-localhost:8092}"
export WORLDSTATE_METRICS="${WORLDSTATE_METRICS:-http://localhost:${WORLDSTATE_PORT}/metrics}"

ts="$(date -u +%Y%m%dT%H%M%SZ)"
outdir="build/reports"
mkdir -p "$outdir"
raw="${outdir}/recovery_raw_${ts}.json"
lines_file="$(mktemp)"
state_file="$(mktemp)"
DRIVER_DIR="services/tools/recoverygen"
DRIVER_BIN=""

# --- スタック復元（必ず実行）-----------------------------------------------
cleanup() {
  echo "== cleanup: スタックを復元します =="
  $COMPOSE up -d --wait postgres nats auth api worldstate llm-worker >/dev/null 2>&1 || \
    $COMPOSE up -d postgres nats auth api worldstate llm-worker >/dev/null 2>&1 || true
  rm -f "$lines_file" "$state_file" 2>/dev/null || true
  [ -n "$DRIVER_BIN" ] && rm -f "$DRIVER_BIN" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# --- 起動確認とドライバのビルド ---------------------------------------------
ensure_stack() {
  echo "== 前提: スタック起動確認 =="
  $COMPOSE up -d --wait postgres nats >/dev/null 2>&1 || $COMPOSE up -d postgres nats >/dev/null 2>&1 || true
  bash scripts/migrate.sh up >/dev/null 2>&1 || true
  $COMPOSE up -d auth api worldstate llm-worker >/dev/null 2>&1 || true
  wait_ready "$API_PORT"
}

build_driver() {
  if ! command -v go >/dev/null 2>&1; then
    echo "go 未導入: recoverygen をビルドできません" >&2
    exit 2
  fi
  DRIVER_BIN="$(mktemp -d)/recoverygen"
  echo "== recoverygen をビルドします =="
  ( cd "$DRIVER_DIR" && GOTOOLCHAIN=local go build -o "$DRIVER_BIN" . ) || {
    echo "recoverygen のビルドに失敗しました" >&2; exit 2; }
}

# wait_ready <port> — /readyz が 200 を返すまで最大 30s 待つ。
wait_ready() {
  local port="$1" code
  for _ in $(seq 1 30); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${port}/readyz" 2>/dev/null || echo 000)"
    [ "$code" = "200" ] && return 0
    sleep 1
  done
  echo "  警告: :${port}/readyz が 200 になりません（続行）" >&2
  return 0
}

# run_driver <subcommand> [args...] — 結果 JSON 行を lines_file へ追記する。
run_driver() {
  local out
  out="$("$DRIVER_BIN" "$@" 2> >(sed 's/^/    /' >&2))"
  # 最終行が結果 JSON（進捗 JSON を出すサブコマンドは verify 側だけ収集する）。
  local last
  last="$(printf '%s\n' "$out" | tail -1)"
  printf '%s\n' "$last" >> "$lines_file"
  # 端末にも要約を出す。
  printf '%s\n' "$last" | python3 -c 'import sys, json
try:
    r = json.load(sys.stdin)
except Exception:
    sys.exit(0)
checks = r.get("checks", [])
mark = "FAIL" if any(c["status"] == "FAIL" for c in checks) else "PASS"
print("  [{}] {}  ({} checks, mode={})".format(r["id"], mark, len(checks), r.get("mode")))
for c in checks:
    print("    - {:<6} {}: {}".format(c["status"], c["name"], c["detail"]))' || true
}

# maybe_stop_ds — 実 DS が動いていれば stop_ds.sh で SIGTERM（crash を実機で起こす）。
# 実 DS が無ければ模擬（recoverygen プロセスの状態破棄で再起動を表現）。戻り値で mode を決める。
maybe_stop_ds() {
  if [ "${DS:-0}" = "1" ] && pgrep -f 'survival-server\.x86_64' >/dev/null 2>&1; then
    echo "  実 DS を停止して crash を再現します（SIGTERM）"
    bash scripts/stop_ds.sh >/dev/null 2>&1 || true
    export RECOVERY_DS_MODE=real
  else
    export RECOVERY_DS_MODE=simulated
  fi
}

# --- 各シナリオ -------------------------------------------------------------

run_s1() {
  echo "== シナリオ1: DS crash → 別 DS で復元（AT-018）=="
  maybe_stop_ds
  run_driver scenario1
}

run_s2() {
  echo "== シナリオ2: 購入応答直後の crash（AT-019/021）=="
  maybe_stop_ds
  run_driver scenario2
}

run_s3() {
  echo "== シナリオ3: NATS 再起動（outbox flush + inbox_dedup）=="
  export RECOVERY_DS_MODE=simulated
  echo "  NATS を停止（stop; JetStream 状態は ephemeral）"
  $COMPOSE stop nats >/dev/null 2>&1 || true
  echo "  NATS 停止中に Gameplay 変更を続行（outbox へ滞留）"
  "$DRIVER_BIN" s3-accumulate --state "$state_file" 2> >(sed 's/^/    /' >&2) >/dev/null
  echo "  NATS を復旧（start）"
  $COMPOSE start nats >/dev/null 2>&1 || true
  sleep 3
  wait_ready "$WORLDSTATE_PORT"
  run_driver s3-verify --state "$state_file"
}

run_s4() {
  echo "== シナリオ4: DB 再起動（Relay 回復 + idempotency）=="
  export RECOVERY_DS_MODE=simulated
  echo "  DB 再起動前に Buyer 登録＋購入"
  "$DRIVER_BIN" s4-setup --state "$state_file" 2> >(sed 's/^/    /' >&2) >/dev/null
  echo "  postgres を再起動"
  $COMPOSE restart postgres >/dev/null 2>&1 || true
  wait_ready "$API_PORT"
  run_driver s4-verify --state "$state_file"
}

run_s5() {
  echo "== シナリオ5: Corrupt Snapshot（16章）=="
  export RECOVERY_DS_MODE=simulated
  run_driver scenario5
}

# --- 結果のとりまとめ -------------------------------------------------------
finalize() {
  local ds_mode="simulated"
  [ "${DS:-0}" = "1" ] && ds_mode="real-ds-opportunistic"
  {
    printf '{\n'
    printf '  "generated_at": "%s",\n' "$ts"
    printf '  "ds_mode": "%s",\n' "$ds_mode"
    printf '  "scenarios": [\n'
    # lines_file の各行（1 シナリオ = 1 JSON オブジェクト）をカンマ区切りで並べる。
    awk 'NF { lines[n++]=$0 } END { for (i=0;i<n;i++) printf "    %s%s\n", lines[i], (i<n-1?",":"") }' "$lines_file"
    printf '  ]\n'
    printf '}\n'
  } > "$raw"
  echo "recovery_test: 生データ -> ${raw}"
  echo "recovery_test: 判定は scripts/recovery_assert.py（引数なしで最新を拾う）"
}

# --- ディスパッチ -----------------------------------------------------------
ensure_stack
build_driver

cmd="${1:-all}"
case "$cmd" in
  scenario1) run_s1 ;;
  scenario2) run_s2 ;;
  scenario3) run_s3 ;;
  scenario4) run_s4 ;;
  scenario5) run_s5 ;;
  all)       run_s1; run_s2; run_s3; run_s4; run_s5 ;;
  *) echo "未知のサブコマンド: $cmd（scenario1..5 | all）" >&2; exit 2 ;;
esac

finalize
