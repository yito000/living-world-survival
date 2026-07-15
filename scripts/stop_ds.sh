#!/usr/bin/env bash
# ローカルの Dedicated Server（Linux ヘッドレスビルド survival-server.x86_64）を停止する。
# DS は Docker ではなくネイティブプロセスのため `docker compose down` の管理外（make down から呼ぶ）。
# Graceful Shutdown（RuntimePersistenceAgent の drain / SaveSnapshot）を効かせるため、まず
# SIGTERM で正常終了を待ち、残った場合のみ SIGKILL する。
set -uo pipefail

pattern="survival-server\.x86_64"

pids="$(pgrep -f "$pattern" || true)"
if [ -z "$pids" ]; then
  echo "stop_ds.sh: 実行中の DS はありません。"
  exit 0
fi

echo "stop_ds.sh: DS を停止します (SIGTERM): ${pids//$'\n'/ }"
# shellcheck disable=SC2086
kill -TERM $pids 2>/dev/null || true

# Graceful shutdown（outbox flush / snapshot save）の猶予。最大 ~15s 待つ。
for _ in $(seq 1 15); do
  pids="$(pgrep -f "$pattern" || true)"
  [ -z "$pids" ] && { echo "stop_ds.sh: DS を停止しました。"; exit 0; }
  sleep 1
done

# まだ残っていれば強制終了。
pids="$(pgrep -f "$pattern" || true)"
if [ -n "$pids" ]; then
  echo "stop_ds.sh: 応答しないため強制終了します (SIGKILL): ${pids//$'\n'/ }"
  # shellcheck disable=SC2086
  kill -KILL $pids 2>/dev/null || true
fi
echo "stop_ds.sh: DS を停止しました。"
