#!/usr/bin/env bash
# M2 手動E2Eスモーク（05B 5章）。DS の RuntimePersistenceAgent を模擬したクライアント
# (services/api/cmd/m2smoke) を、実際に起動した apid / PostgreSQL / NATS に対して1本通す。
#   bootstrap→ready→inventory event(AppendEvents)→outbox flush(NATS)→SaveSnapshot→
#   DS再起動相当のLoadBootstrapで snapshot+event tail から復元。
# Unity Dedicated Server(Windows/A) は別途。ここは B 境界の永続化経路を実物で保証する。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
COMPOSE="docker compose -f infra/docker-compose.yml"

# .env（DATABASE_URL_HOST 等）を読む。
if [ -f .env ]; then
  set -a; # shellcheck disable=SC1091
  . ./.env; set +a
fi

log_dir="build/e2e"
mkdir -p "$log_dir"
log_file="${log_dir}/m2_smoke.log"

echo "== [1/4] インフラ起動 (postgres/nats) =="
$COMPOSE up -d --wait postgres nats

echo "== [2/4] マイグレーション適用 (0001+0002) =="
bash scripts/migrate.sh up

echo "== [3/4] api コンテナ起動 (build) & readyz 待ち =="
$COMPOSE up -d --build api
ready=""
for _ in $(seq 1 30); do
  if curl -sf localhost:8082/readyz >/dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
[ -n "$ready" ] || { echo "apid が readyz になりません" >&2; $COMPOSE logs api | tail -20; exit 1; }
curl -s localhost:8082/readyz; echo

echo "== [4/4] E2E スモーク実行 (DSシミュレータ → 実 apid) =="
# ホストから叩くので localhost 版のアドレスを渡す。
export DATABASE_URL_HOST="${DATABASE_URL_HOST:-postgres://survival:survival@localhost:5432/survival?sslmode=disable}"
export NATS_URL_HOST="${NATS_URL_HOST:-nats://localhost:4222}"
export API_GRPC_ADDR="${API_GRPC_ADDR:-localhost:8092}"

set +e
( cd services/api && go run ./cmd/m2smoke ) 2>&1 | tee "$log_file"
rc=${PIPESTATUS[0]}
set -e

echo
if [ "$rc" -eq 0 ]; then
  echo "e2e_m2_smoke: OK （ログ: ${log_file}）"
else
  echo "e2e_m2_smoke: FAILED (rc=$rc) （ログ: ${log_file}）" >&2
fi
exit "$rc"
