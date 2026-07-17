#!/usr/bin/env bash
# M7 負荷試験ハーネス（10B 3.1 / AT-020）。
#
# ■ 何を測るスクリプトか（スコープ）
#   10B 0.1 の分担で WSL2 側に割り当てられているのは「負荷試験ハーネス（バックエンド計測）」で、
#   0.2 は「負荷/Soak の Unity 側は 10A が担当し、本書はバックエンド計測とハーネス駆動に限定する」
#   と明記している。よって本スクリプトが測るのは **バックエンド**（auth / api / worldstate /
#   llm-worker / PostgreSQL / NATS）である。負荷ドライバ services/tools/loadgen は
#   「DS 模擬 + N プレイヤー」の役で実 RPC 面を叩く。
#   **Client→DS（FishNet）の描画/同期負荷は 10A の PlayMode テストの担当であり本書の対象外**。
#   ここを Client 権威経路の試験と取り違えないこと（MVP-SEC-005/006）。
#
# ■ 指示書との差異（明示）
#   10B 3.1 は「RC の Linux DS を Docker/Headless で起動」と書くが、本リポジトリの DS は
#   Docker ではなくネイティブプロセスとして WSL2 上で動く（scripts/stop_ds.sh の注記
#   「DS は Docker ではなくネイティブプロセス」参照）。ここはリポジトリの実態に合わせ、
#   DS=1 のとき unity/SurvivalWorld/Build/Server/survival-server.x86_64 を直接起動する。
#
# ■ Tick の出所（10B 6章）
#   Gate の tick_ms は **DS が Heartbeat で自報告した値**（auth の ds_tick_seconds）が正で、
#   ハーネス側 RTT ではない。
#     DS=1 → 実 DS を起動し、loadgen には TICK_MS=0 を渡す（合成 tick を混ぜない）。
#            report の tick_source = "real_ds"。
#     DS=0 → 実 DS 無し。loadgen が TICK_MS の **合成値** を Heartbeat に載せる。
#            report の tick_source = "synthetic"（＝計測値ではない。load_assert.py は
#            Tick Gate を PASS にせず RECORD 扱いにする）。
#
# ■ 使い方
#   ローカル開発スケール:  PLAYERS=2 AI=20 DURATION=60s bash scripts/load_test.sh
#   AT-020 目標スケール:   PLAYERS=16 AI=20 ANIMALS=80 DURATION=300s DS=1 bash scripts/load_test.sh
#
# ■ 出力
#   build/reports/load_<scale>_<ts>.json — 規模/構成・時刻・tick_source・loadgen サマリ・
#   前後の /metrics（Gate 判定に要るヒストグラム bucket と outbox/lag/db の系列）。
#   判定は scripts/load_assert.py が行う。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if [ -f .env ]; then
  set -a; # shellcheck disable=SC1091
  . ./.env; set +a
fi

PLAYERS="${PLAYERS:-2}"
AI="${AI:-20}"
ANIMALS="${ANIMALS:-0}"
DURATION="${DURATION:-60s}"
DS="${DS:-0}"
TICK_MS="${TICK_MS:-33}"

AUTH_PORT="${AUTH_PORT:-8081}"
API_PORT="${API_PORT:-8082}"
WORLDSTATE_PORT="${WORLDSTATE_PORT:-8083}"
LLM_WORKER_PORT="${LLM_WORKER_PORT:-8084}"
AUTH_GRPC_PORT="${AUTH_GRPC_PORT:-9091}"
API_GRPC_PORT="${API_GRPC_PORT:-8092}"

# ホストから直接叩くので localhost 版のアドレスを使う（e2e_m2_smoke.sh と同じ方針）。
export DATABASE_URL_HOST="${DATABASE_URL_HOST:-postgres://survival:survival@localhost:5432/survival?sslmode=disable}"
export NATS_URL_HOST="${NATS_URL_HOST:-nats://localhost:4222}"

COMPOSE="docker compose -f infra/docker-compose.yml"
ts="$(date -u +%Y%m%dT%H%M%SZ)"
scale="p${PLAYERS}_ai${AI}_an${ANIMALS}"
report="build/reports/load_${scale}_${ts}.json"
work="build/reports/.load_${scale}_${ts}"
mkdir -p build/reports "$work"

# --- 後始末 ----------------------------------------------------------------
# 本スクリプトが起動したものだけを止める（元から動いていたサービスは触らない）。
started_pids=()
started_ds=0
cleanup() {
  local rc=$?
  set +e
  for pid in "${started_pids[@]:-}"; do
    [ -n "${pid:-}" ] || continue
    kill -TERM "$pid" 2>/dev/null
  done
  for pid in "${started_pids[@]:-}"; do
    [ -n "${pid:-}" ] || continue
    for _ in $(seq 1 10); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.5
    done
    kill -KILL "$pid" 2>/dev/null
  done
  if [ "$started_ds" -eq 1 ]; then
    bash scripts/stop_ds.sh >/dev/null 2>&1
  fi
  # ログは残すがビルド成果物は残さない（レポート置き場に数十MBのバイナリを積まない）。
  rm -f "${work}"/*.bin
  exit "$rc"
}
trap cleanup EXIT INT TERM

# --- 1. インフラ -----------------------------------------------------------
echo "== [1/6] インフラ起動 (postgres/nats) + migration =="
$COMPOSE up -d --wait postgres nats
bash scripts/migrate.sh up >/dev/null

# --- 2. サービス -----------------------------------------------------------
# 既に上がっていればそのまま使う。上がっていなければソースから起動する
# （compose のイメージが古いことがあるため、ここでは source of truth をソースに置く）。
wait_ready() {
  local port="$1" tries="${2:-60}"
  for _ in $(seq 1 "$tries"); do
    curl -sf "localhost:${port}/readyz" >/dev/null 2>&1 && return 0
    sleep 1
  done
  return 1
}

ensure_service() {
  local name="$1" port="$2" dir="$3" cmd="$4" bin
  if curl -sf "localhost:${port}/readyz" >/dev/null 2>&1; then
    echo "  既に稼働中: ${name} (:${port})"
    if [ "$name" = "auth" ] && [ "$PLAYERS" -gt 5 ]; then
      echo "  注意: 既存の auth の Rate Limit は上書きできません。PLAYERS=${PLAYERS} だと" >&2
      echo "        アカウント作成が既定 5/分で弾かれる可能性があります（下の注記参照）。" >&2
      echo "        auth を止めてから再実行すると、ハーネスが緩和値で起動します。" >&2
    fi
    return 0
  fi
  # `go run` ではなくバイナリを作って直接起動する。go run は子プロセスを別 PID で
  # 抱えるので、親を kill してもサービスがポートに居座る（後始末の取りこぼし）。
  bin="${root}/${work}/${name}.bin"
  echo "  ビルド: ${name} (${dir} → ${cmd})"
  ( cd "$dir" && go build -o "$bin" "$cmd" )
  echo "  起動: ${name} (:${port})"
  # cwd はサービスディレクトリ。ITEM_DEFINITIONS_PATH 等が相対パスなので、リポジトリ
  # ルートから起動すると api がマスタを読めない。
  # 接続先は .env の docker network 版（host=postgres/nats）ではなく localhost 版に差し替える。
  #
  # Rate Limit の緩和（ハーネス限定）: auth は 1 送信元あたりのログイン失敗/アカウント作成を
  # 絞る（第16章 / 10B 3.4）。合成プレイヤーは全員同一 IP なので、既定値のままだと
  # **バックエンドではなくレート制限**が律速して負荷が入らない。ここは計測のための上書きで
  # あって、Rate Limit そのものの検証は L-6（Security）の担当。RC/security プロファイルは
  # 既定値のままであること。
  ( cd "$root/$dir" && \
    DATABASE_URL="$DATABASE_URL_HOST" NATS_URL="$NATS_URL_HOST" \
    LOGIN_RATE_LIMIT="$harness_rate" LOGIN_RATE_BURST="$harness_rate" \
    ACCOUNT_CREATE_RATE_LIMIT="$harness_rate" \
    exec "$bin" ) >"${work}/${name}.log" 2>&1 &
  started_pids+=("$!")
  if ! wait_ready "$port" 90; then
    echo "load_test.sh: ${name} が readyz になりません。ログ: ${work}/${name}.log" >&2
    tail -20 "${work}/${name}.log" >&2 || true
    exit 1
  fi
  echo "  ready: ${name} (:${port})"
}

echo "== [2/6] サービス確認 (auth/api) =="
# ハーネスが起動する auth に渡す Rate Limit（上の ensure_service の注記参照）。
harness_rate="${LOADGEN_AUTH_RATE:-$(( PLAYERS * 4 + 100 ))}"
ensure_service auth "$AUTH_PORT" services/auth ./cmd/authd
ensure_service api "$API_PORT" services/api ./cmd/apid

# --- 3. DS ------------------------------------------------------------------
ds_bin="unity/SurvivalWorld/Build/Server/survival-server.x86_64"
tick_source="synthetic"
loadgen_tick_ms="$TICK_MS"
echo "== [3/6] DS (DS=${DS}) =="
if [ "$DS" = "1" ]; then
  if [ ! -x "$ds_bin" ]; then
    echo "load_test.sh: DS=1 ですが ${ds_bin} が実行可能な形で見つかりません。" >&2
    echo "  10A（Windows側）が Linux DS ビルドを生成してから再実行してください。" >&2
    exit 1
  fi
  # DS は Docker ではなくネイティブプロセス（stop_ds.sh の注記）。
  # 停止は stop_ds.sh に任せる（SIGTERM で drain/SaveSnapshot を効かせるため、
  # started_pids には積まない）。
  "./${ds_bin}" -batchmode -nographics -logFile "${work}/ds.log" &
  started_ds=1
  tick_source="real_ds"
  # 実 DS が自報告する tick_ms だけを ds_tick_seconds に載せる（合成値を混ぜない）。
  loadgen_tick_ms=0
  echo "  実 DS 起動: ${ds_bin} (tick_source=real_ds)"
  sleep 5
else
  echo "  実 DS 無し。loadgen が合成 tick_ms=${TICK_MS} を Heartbeat に載せる"
  echo "  → tick_source=synthetic（Tick Gate は計測値ではない。load_assert.py が RECORD 扱いにする）"
fi

# --- 4. metrics スクレイプ（前） --------------------------------------------
scrape() {
  local phase="$1" name="$2" port="$3"
  curl -s --max-time 5 "http://localhost:${port}/metrics" >"${work}/${phase}_${name}.txt" 2>/dev/null || true
}
scrape_all() {
  local phase="$1"
  scrape "$phase" auth "$AUTH_PORT"
  scrape "$phase" api "$API_PORT"
  scrape "$phase" worldstate "$WORLDSTATE_PORT"
  scrape "$phase" llm-worker "$LLM_WORKER_PORT"
  date -u +%Y-%m-%dT%H:%M:%SZ >"${work}/${phase}_at.txt"
}

echo "== [4/6] metrics スクレイプ（負荷前） =="
scrape_all before

# --- 5. 負荷投入 ------------------------------------------------------------
echo "== [5/6] 負荷投入 (players=${PLAYERS} ai=${AI} animals=${ANIMALS} duration=${DURATION}) =="
( cd services/tools/loadgen && go build -o "${root}/${work}/loadgen.bin" . )
set +e
PLAYERS="$PLAYERS" AI="$AI" ANIMALS="$ANIMALS" DURATION="$DURATION" \
TICK_MS="$loadgen_tick_ms" \
AUTH_PORT="$AUTH_PORT" AUTH_GRPC_PORT="$AUTH_GRPC_PORT" API_GRPC_PORT="$API_GRPC_PORT" \
DATABASE_URL_HOST="$DATABASE_URL_HOST" \
  "${work}/loadgen.bin" >"${work}/loadgen.json" 2>"${work}/loadgen.log"
loadgen_rc=$?
set -e
tail -5 "${work}/loadgen.log" || true
if [ "$loadgen_rc" -ne 0 ]; then
  echo "load_test.sh: loadgen が失敗しました (rc=${loadgen_rc})。ログ: ${work}/loadgen.log" >&2
  exit "$loadgen_rc"
fi

# --- 6. metrics スクレイプ（後）→ レポート ----------------------------------
echo "== [6/6] metrics スクレイプ（負荷後）→ レポート =="
# Outbox relay / worldstate 投影が負荷分を吐き切るのを少し待ってから後スクレイプする
# （outbox_depth を「実行直後のたまたま」で読まない）。
sleep 3
scrape_all after

REPORT_PATH="$report" WORK_DIR="$work" \
SCALE="$scale" PLAYERS="$PLAYERS" AI="$AI" ANIMALS="$ANIMALS" DURATION="$DURATION" \
TICK_SOURCE="$tick_source" TICK_MS="$TICK_MS" DS_MODE="$DS" \
python3 - <<'PY'
import json
import os
import pathlib

work = pathlib.Path(os.environ["WORK_DIR"])
report_path = pathlib.Path(os.environ["REPORT_PATH"])

# Gate 判定に要る系列だけを残す（Go/process collector まで抱えるとレポートが読めなくなる）。
KEEP = (
    "ds_tick_seconds", "ds_players", "ds_ready", "ds_heartbeats_total",
    "auth_login_attempts_total", "auth_refresh_rotations_total",
    "auth_join_ticket_redeems_total", "http_request_duration_seconds",
    "grpc_server_handling_seconds", "db_query_duration_seconds",
    "outbox_depth", "outbox_oldest_age_seconds", "outbox_publish_total",
    "event_lag_seconds", "economy_purchases_total", "buyer_sold_out_total",
    "world_snapshots_saved_total",
    "worldstate_events_processed_total", "worldstate_event_lag_seconds",
    "worldstate_projection_duration_seconds",
    "llm_decision_duration_seconds", "llm_decisions_total", "llm_tokens_total",
    "llm_errors_total",
)


def read_lines(phase: str, name: str) -> dict:
    path = work / f"{phase}_{name}.txt"
    if not path.exists() or path.stat().st_size == 0:
        # スクレイプできなかったことを「0」ではなく「取れなかった」として残す。
        return {"scraped": False, "lines": []}
    kept = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith("#"):
            continue
        if line.split("{", 1)[0].split(" ", 1)[0].startswith(KEEP):
            kept.append(line)
    return {"scraped": True, "lines": kept}


def phase(name: str) -> dict:
    at = work / f"{name}_at.txt"
    return {
        "scraped_at": at.read_text().strip() if at.exists() else None,
        "services": {s: read_lines(name, s) for s in ("auth", "api", "worldstate", "llm-worker")},
    }


loadgen_raw = (work / "loadgen.json").read_text(encoding="utf-8").strip()
loadgen = json.loads(loadgen_raw) if loadgen_raw else None

report = {
    "schema": "load_report/v1",
    "scale_label": os.environ["SCALE"],
    "config": {
        "players": int(os.environ["PLAYERS"]),
        "ai": int(os.environ["AI"]),
        "animals": int(os.environ["ANIMALS"]),
        "duration": os.environ["DURATION"],
        "ds_mode": os.environ["DS_MODE"],
        "synthetic_tick_ms": int(os.environ["TICK_MS"]),
    },
    # 合成 tick を「計測された tick」と取り違えないための必須フィールド（10B 6章）。
    "tick_source": os.environ["TICK_SOURCE"],
    "scope_note": (
        "バックエンド計測（10B 0.1/0.2）。loadgen は DS 模擬 + N プレイヤーとして実 RPC 面を"
        "駆動する。Client→DS(FishNet) の描画/同期負荷は 10A の PlayMode 負荷の担当で対象外。"
    ),
    # Gate の閾値はレポートに焼き込む（後から報告書だけ見て判定根拠が追えるように）。
    "gates": {
        "tick_p95_seconds": 0.040,
        "tick_p99_seconds": 0.050,
        "op_p95_seconds": 0.200,
        "purchase_p95_seconds": 0.500,
        "source": "10B 3.1 / MVP 第3章",
    },
    "loadgen": loadgen,
    "metrics": {"before": phase("before"), "after": phase("after")},
}
report_path.parent.mkdir(parents=True, exist_ok=True)
report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"  レポート: {report_path}")
PY

echo
echo "load_test.sh: OK"
echo "  レポート : ${report}"
echo "  作業ログ : ${work}/"
echo "  判定     : python3 scripts/load_assert.py ${report}"
