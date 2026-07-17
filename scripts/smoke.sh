#!/usr/bin/env bash
# 全サービス health 確認（03B 5章 smoke）。
#
# M7（10B 3.5）で liveness(/healthz) に加えて readiness(/readyz) と metrics(/metrics)
# も確認する。/healthz は依存先を見ないので、DB/NATS が落ちたままでも 200 を返す:
# 依存先込みで「使える」ことを言えるのは /readyz だけ。
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if [ -f .env ]; then
  set -a; # shellcheck disable=SC1091
  . ./.env; set +a
fi

AUTH_PORT="${AUTH_PORT:-8081}"
API_PORT="${API_PORT:-8082}"
WORLDSTATE_PORT="${WORLDSTATE_PORT:-8083}"
LLM_WORKER_PORT="${LLM_WORKER_PORT:-8084}"

# name -> port
declare -A ports=(
  [auth]="${AUTH_PORT}"
  [api]="${API_PORT}"
  [worldstate]="${WORLDSTATE_PORT}"
  [llm-worker]="${LLM_WORKER_PORT}"
)
services=(auth api worldstate llm-worker)

fail=0
# check_http <name> <port> <path> <retries>
# 200 を待つ。retries=1 なら即時判定（リトライで待つ意味が無い検査用）。
check_http() {
  local name="$1" port="$2" path="$3" retries="$4" url code
  url="http://localhost:${port}${path}"
  for _ in $(seq 1 "$retries"); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$url" 2>/dev/null || echo 000)"
    if [ "$code" = "200" ]; then
      printf "  \033[32mOK\033[0m    %-12s %s (200)\n" "$name" "$url"
      return 0
    fi
    [ "$retries" -gt 1 ] && sleep 2
  done
  printf "  \033[31mFAIL\033[0m  %-12s %s (last=%s)\n" "$name" "$url" "${code:-000}"
  return 1
}

echo "== smoke: liveness (/healthz) =="
for name in "${services[@]}"; do
  check_http "$name" "${ports[$name]}" /healthz 30 || fail=1
done

# readiness は依存先（DB/NATS）込みの判定。liveness が通ってから見る。
echo "== smoke: readiness (/readyz) =="
for name in "${services[@]}"; do
  check_http "$name" "${ports[$name]}" /readyz 30 || fail=1
done

# /metrics は負荷/Soak ハーネスの入力（10B 3.1/3.2）。200 なだけでなく、
# 実際に系列が載っていることまで見る（空の 200 を成功と誤認しない）。
echo "== smoke: metrics (/metrics) =="
for name in "${services[@]}"; do
  url="http://localhost:${ports[$name]}/metrics"
  body="$(curl -s --max-time 3 "$url" 2>/dev/null || true)"
  if printf '%s' "$body" | grep -qE '^# TYPE '; then
    printf "  \033[32mOK\033[0m    %-12s %s (%d series)\n" \
      "$name" "$url" "$(printf '%s' "$body" | grep -c '^# TYPE ')"
  else
    printf "  \033[31mFAIL\033[0m  %-12s %s (no exposition output)\n" "$name" "$url"
    fail=1
  fi
done

if [ "$fail" -ne 0 ]; then
  echo "smoke.sh: 一部サービスが health/ready/metrics を返しませんでした。" >&2
  echo "  ログ確認: make logs" >&2
  exit 1
fi
echo "smoke.sh: 全サービス health/ready/metrics OK"

# --- M1: Auth/Matchmaking E2E 疎通（5.3） -----------------------------------
# アカウント作成→ログイン→RegisterServer/Heartbeat→matchmaking join→
# RedeemJoinTicket（単回消費: 1回目OK / 2回目 error）を Go スモークバイナリで確認する。
# grpcurl 非依存（REST/gRPC を 1 バイナリで実行, 5.3 の但し書き）。
echo "== smoke: Auth/Matchmaking E2E =="
if ! command -v go >/dev/null 2>&1; then
  echo "  go 未導入: Matchmaking E2E をスキップします。" >&2
else
  ( cd services/auth && AUTH_PORT="${AUTH_PORT}" AUTH_GRPC_PORT="${AUTH_GRPC_PORT:-9091}" \
      go run ./cmd/mm-smoke )
  rc=$?
  if [ "$rc" -ne 0 ]; then
    echo "smoke.sh: Auth/Matchmaking E2E が失敗しました（rc=$rc）。" >&2
    echo "  ログ確認: make logs" >&2
    exit 1
  fi
fi
echo "smoke.sh: smoke OK"
