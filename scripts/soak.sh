#!/usr/bin/env bash
# M7 Soak（長時間連続稼働 / 10B 3.2・MVP第18.1）。
#
# 全サービス（＋任意で DS）を常駐させ、AI/Buyer/World Event を繰り返し発生させながら
# 一定間隔で次を CSV へ追記する:
#   - memory : 各コンテナの RSS（リーク疑いは soak_assert.py が線形回帰の傾きで判定）
#   - outbox : 未 publish 件数と最古の滞留秒数（Relay が追随できているか）
#   - lag    : domain_events の最新から Consumer 処理位置までの差
#   - tick   : DS tick_ms の P95 推移（劣化＝リーク/断片化の兆候）
#
# 落とし穴（10B 6章）: RSS 増加と Outbox 滞留は**原因が別**。CSV に両方を並記して、
# どのサービスが伸びているかを RSS 列で特定できるようにしてある。
#
# 使い方:
#   SOAK_MINUTES=10 bash scripts/soak.sh     # 短縮 Soak（make soak-short）
#   SOAK_HOURS=4    bash scripts/soak.sh     # full Soak（夜間/手動・CI には載せない）
#
# full Soak は CI に載せない（4時間は PR を止める）。短縮 Soak を CI/nightly、
# full は手動/夜間で回す。
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if [ -f .env ]; then
  set -a; # shellcheck disable=SC1091
  . ./.env; set +a
fi

COMPOSE="docker compose -f infra/docker-compose.yml"
AUTH_PORT="${AUTH_PORT:-8081}"
API_PORT="${API_PORT:-8082}"

# 既定は 4 時間（第18.1）。SOAK_MINUTES を指定するとそちらが優先（短縮 Soak）。
SOAK_HOURS="${SOAK_HOURS:-4}"
if [ -n "${SOAK_MINUTES:-}" ]; then
  duration_sec=$((SOAK_MINUTES * 60))
else
  duration_sec=$(awk -v h="$SOAK_HOURS" 'BEGIN{printf "%d", h*3600}')
fi
INTERVAL="${SOAK_INTERVAL:-60}"   # 記録間隔（秒）。既定 60（10B 3.2 の「例60秒」）。

# 負荷ドライバ（L-2）。常駐させて AI/Buyer/購入を発生させ続ける。
LOADGEN_DIR="services/tools/loadgen"
PLAYERS="${PLAYERS:-2}"
AI="${AI:-20}"

ts="$(date -u +%Y%m%dT%H%M%SZ)"
outdir="build/reports"
mkdir -p "$outdir"
csv="${outdir}/soak_${ts}.csv"

DB_URL="${DATABASE_URL_HOST:-postgres://survival:survival@localhost:5432/survival?sslmode=disable}"

echo "== soak: ${duration_sec}s / ${INTERVAL}s 間隔 -> ${csv} =="

# --- 起動 -------------------------------------------------------------------
$COMPOSE up -d --wait postgres nats >/dev/null 2>&1 || true
bash scripts/migrate.sh up >/dev/null 2>&1 || true
$COMPOSE up -d auth api worldstate llm-worker >/dev/null 2>&1 || true

loadgen_pid=""
cleanup() {
  [ -n "$loadgen_pid" ] && kill "$loadgen_pid" 2>/dev/null || true
  wait "$loadgen_pid" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# 負荷ドライバを常駐させる。無ければ「観測のみ」で続行する（Soak の主目的は
# 常駐時の劣化観測なので、駆動が無くても CSV は意味を持つ）。
if [ -d "$LOADGEN_DIR" ] && command -v go >/dev/null 2>&1; then
  echo "  loadgen を常駐起動します（PLAYERS=${PLAYERS} AI=${AI}）"
  ( cd "$LOADGEN_DIR" && PLAYERS="$PLAYERS" AI="$AI" DURATION="${duration_sec}s" \
      go run . >/dev/null 2>&1 ) &
  loadgen_pid=$!
else
  echo "  警告: loadgen が無いため観測のみで実行します（負荷は掛かりません）" >&2
fi

# --- ヘルパ -----------------------------------------------------------------

# histogram_p95 <metrics-text> <metric-name>
# Prometheus ヒストグラムの _bucket 群から P95 を線形補間で求める。
# 系列が無ければ空文字（= no data）。0 を返すと「速い」と誤読されるため。
histogram_p95() {
  printf '%s' "$1" | awk -v m="$2" '
    $0 ~ "^"m"_bucket\\{" {
      # le="..." を取り出す
      if (match($0, /le="[^"]+"/)) {
        le = substr($0, RSTART+4, RLENGTH-5)
        cnt = $NF
        if (le == "+Inf") { inf = cnt }
        else { les[n] = le+0; cnts[n] = cnt+0; n++ }
      }
    }
    END {
      if (n == 0 || inf == 0) { exit }   # no data
      target = inf * 0.95
      prev_le = 0; prev_cnt = 0
      # bucket は昇順で出る前提だが、順序に依存しないよう挿入ソートする
      for (i = 0; i < n; i++) for (j = i+1; j < n; j++)
        if (les[j] < les[i]) { t=les[i];les[i]=les[j];les[j]=t; t=cnts[i];cnts[i]=cnts[j];cnts[j]=t }
      for (i = 0; i < n; i++) {
        if (cnts[i] >= target) {
          span = cnts[i] - prev_cnt
          if (span <= 0) { printf "%.6f", les[i]; exit }
          frac = (target - prev_cnt) / span
          printf "%.6f", prev_le + (les[i] - prev_le) * frac
          exit
        }
        prev_le = les[i]; prev_cnt = cnts[i]
      }
      printf "%.6f", les[n-1]
    }'
}

# gauge_value <metrics-text> <metric-name>
gauge_value() {
  printf '%s' "$1" | awk -v m="$2" '$1 == m { print $2; exit }'
}

# container_rss_bytes <service> — docker stats の MEM USAGE を bytes へ。
container_rss_bytes() {
  local name
  name="$($COMPOSE ps -q "$1" 2>/dev/null | head -1)"
  [ -z "$name" ] && return 0
  docker stats --no-stream --format '{{.MemUsage}}' "$name" 2>/dev/null | awk '
    {
      v = $1
      unit = v; sub(/^[0-9.]+/, "", unit)
      sub(/[A-Za-z]+$/, "", v)
      mult = 1
      if (unit ~ /^KiB/) mult = 1024
      else if (unit ~ /^MiB/) mult = 1024*1024
      else if (unit ~ /^GiB/) mult = 1024*1024*1024
      else if (unit ~ /^B/)   mult = 1
      printf "%d", v * mult
    }'
}

# --- CSV ヘッダ -------------------------------------------------------------
echo "elapsed_sec,unix_ts,rss_auth,rss_api,rss_worldstate,rss_llm_worker,outbox_depth,outbox_oldest_age_sec,event_lag_sec,ws_event_lag_sec,ds_tick_p95_sec,purchases_total" > "$csv"

start="$(date +%s)"
end=$((start + duration_sec))

while :; do
  now="$(date +%s)"
  [ "$now" -ge "$end" ] && break
  elapsed=$((now - start))

  api_metrics="$(curl -s --max-time 5 "http://localhost:${API_PORT}/metrics" 2>/dev/null || true)"
  auth_metrics="$(curl -s --max-time 5 "http://localhost:${AUTH_PORT}/metrics" 2>/dev/null || true)"
  ws_metrics="$(curl -s --max-time 5 "http://localhost:${WORLDSTATE_PORT:-8083}/metrics" 2>/dev/null || true)"

  outbox_depth="$(gauge_value "$api_metrics" outbox_depth)"
  outbox_age="$(gauge_value "$api_metrics" outbox_oldest_age_seconds)"
  event_lag="$(gauge_value "$api_metrics" event_lag_seconds)"
  ws_lag="$(gauge_value "$ws_metrics" worldstate_event_lag_seconds)"
  tick_p95="$(histogram_p95 "$auth_metrics" ds_tick_seconds)"
  purchases="$(printf '%s' "$api_metrics" | awk '/^economy_purchases_total\{/ { s += $NF } END { if (s=="") s=0; print s }')"

  # Outbox は DB からも直接見る（api が落ちていても滞留を記録できるように）。
  if [ -z "$outbox_depth" ] && command -v psql >/dev/null 2>&1; then
    outbox_depth="$(psql "$DB_URL" -tAc \
      'SELECT count(*) FROM outbox_messages WHERE published_at IS NULL' 2>/dev/null || echo "")"
  fi

  printf '%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n' \
    "$elapsed" "$now" \
    "$(container_rss_bytes auth)" "$(container_rss_bytes api)" \
    "$(container_rss_bytes worldstate)" "$(container_rss_bytes llm-worker)" \
    "${outbox_depth}" "${outbox_age}" "${event_lag}" "${ws_lag}" \
    "${tick_p95}" "${purchases}" >> "$csv"

  printf '  t=%-6ss outbox=%-6s lag=%-8s tick_p95=%-10s purchases=%s\n' \
    "$elapsed" "${outbox_depth:-n/a}" "${event_lag:-n/a}" "${tick_p95:-n/a}" "${purchases:-0}"

  sleep "$INTERVAL"
done

echo "soak.sh: CSV -> ${csv}"
echo "soak.sh: 判定は scripts/soak_assert.py ${csv}"
