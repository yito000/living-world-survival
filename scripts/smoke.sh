#!/usr/bin/env bash
# 全サービス health 確認（03B 5章 smoke）。
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

# name -> "port path"
declare -A endpoints=(
  [auth]="${AUTH_PORT} /healthz"
  [api]="${API_PORT} /healthz"
  [worldstate]="${WORLDSTATE_PORT} /healthz"
  [llm-worker]="${LLM_WORKER_PORT} /healthz"
)

fail=0
check_health() { # check_health <name> <port> <path>
  local name="$1" port="$2" path="$3" url
  url="http://localhost:${port}${path}"
  for i in $(seq 1 30); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$url" 2>/dev/null || echo 000)"
    if [ "$code" = "200" ]; then
      printf "  \033[32mOK\033[0m    %-12s %s (200)\n" "$name" "$url"
      return 0
    fi
    sleep 2
  done
  printf "  \033[31mFAIL\033[0m  %-12s %s (last=%s)\n" "$name" "$url" "${code:-000}"
  return 1
}

echo "== smoke: health チェック =="
for name in auth api worldstate llm-worker; do
  read -r port path <<<"${endpoints[$name]}"
  check_health "$name" "$port" "$path" || fail=1
done

if [ "$fail" -ne 0 ]; then
  echo "smoke.sh: 一部サービスが health を返しませんでした。" >&2
  echo "  ログ確認: make logs" >&2
  exit 1
fi
echo "smoke.sh: 全サービス health OK"
