#!/usr/bin/env bash
# DB マイグレーション（03B 7章）。usage: migrate.sh [up|down|version]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
direction="${1:-up}"

# .env があれば読み込む（DATABASE_URL_HOST 等）。
if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

# ホストから叩くので localhost 版を優先。
URL="${DATABASE_URL_HOST:-${DATABASE_URL:-postgres://survival:survival@localhost:5432/survival?sslmode=disable}}"
# .env が CRLF だと値末尾に \r が混入し接続文字列を壊す（かつ端末上でエラーを上書きして
# 見えなくする）。CR を除去して防ぐ。
URL="${URL//$'\r'/}"

# 診断用にパスワードを伏せて表示。
masked="$(printf '%s' "$URL" | sed -E 's#(://[^:]+:)[^@]*@#\1****@#')"
echo "DATABASE_URL = ${masked}"

if ! command -v migrate >/dev/null 2>&1; then
  echo "golang-migrate (migrate) 未導入: マイグレーションをスキップします。"
  echo "  導入: https://github.com/golang-migrate/migrate"
  exit 0
fi

# Postgres 起動待ち（TCP。最大 ~60s）。
host_port="$(printf '%s' "$URL" | sed -E 's#.*@([^/?]+).*#\1#')"
host="${host_port%%:*}"; port="${host_port##*:}"; port="${port:-5432}"
echo "waiting for postgres tcp at ${host}:${port} ..."
for i in $(seq 1 30); do
  if (exec 3<>"/dev/tcp/${host}/${port}") 2>/dev/null; then
    exec 3>&- 3<&- 2>/dev/null || true
    break
  fi
  sleep 2
  [ "$i" -eq 30 ] && { echo "postgres に接続できません: ${host}:${port}" >&2; exit 1; }
done

# サービスごとに専用の管理テーブルを使い、同一 DB での version 衝突を避ける。
run_one() { # run_one <name> <migrations_dir>
  local name="$1" dir="$2"
  [ -d "$dir" ] || { echo "skip ${name}: ${dir} なし"; return 0; }
  local sep="?"; case "$URL" in *\?*) sep="&";; esac
  local url="${URL}${sep}x-migrations-table=schema_migrations_${name}"
  echo "== migrate ${direction}: ${name} (${dir}) =="

  # 新規ボリューム初期化中など postgres が「起動中でクエリ不可」の一瞬があるため、
  # 実行自体を数回リトライする（stderr はそのまま表示）。
  local attempts=15 rc=0
  for ((a = 1; a <= attempts; a++)); do
    rc=0
    if [ "$direction" = "down" ]; then
      yes | migrate -path "$dir" -database "$url" down && return 0 || rc=$?
    else
      migrate -path "$dir" -database "$url" "$direction" && return 0 || rc=$?
    fi
    if [ "$a" -lt "$attempts" ]; then
      echo "  migrate 失敗 (rc=$rc)。postgres 準備待ちで再試行 ${a}/${attempts} ..." >&2
      sleep 2
    fi
  done
  echo "migrate ${name} が ${attempts} 回失敗しました (rc=$rc)。上記エラーを確認してください。" >&2
  return "$rc"
}

run_one auth services/auth/migrations
run_one api  services/api/migrations
echo "migrate.sh ${direction}: OK"
