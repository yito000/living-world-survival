#!/usr/bin/env bash
# M7 ログ秘匿検査（10B 3.4 / MVP-SEC-002）。
#
# 「ログ/Trace/Error に Password/Token を出さない」ことを、実際に出力された
# ログ本文を走査して確かめる。一次防御は services/common/obs（Go）と
# app/obs.py・worker/obs.py（Python）のキー名 redaction で、これはその裏取り。
#
# 走査対象:
#   - docker compose の各サービスログ（既定）
#   - 引数で渡したファイル/ディレクトリ（例: build/e2e/*.log）
#
# 使い方:
#   scripts/log_secret_scan.sh                 # compose のログを走査
#   scripts/log_secret_scan.sh build/e2e/x.log # ファイルを走査
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

COMPOSE="docker compose -f infra/docker-compose.yml"
SERVICES=(auth api worldstate llm-worker)
TAIL="${LOG_SCAN_TAIL:-2000}"

ts="$(date -u +%Y%m%dT%H%M%SZ)"
outdir="build/reports"
mkdir -p "$outdir"
out="${outdir}/log_secret_scan_${ts}.json"

# 秘匿値が「値ごと」出ているパターン。キー名だけの出現（"password" という
# 単語がメッセージに含まれる等）は誤検知になるので、**キー: 値** の形と
# 実トークン形式だけを狙う。
#
# obs 側は値を [REDACTED] へ差し替えるので、その形は検知しない（正常）。
#
# 秘匿キーの JSON フィールド（値が [REDACTED] の行は後段で除外する）。
json_leak='"(password|passwd|refresh_token|access_token|api_key|apikey|secret|signing_key|private_key|authorization)"[[:space:]]*:[[:space:]]*"[^"]'
# key=value 形式（平文ログの名残）。
kv_leak='(password|passwd|refresh_token|access_token|api_key|signing_key|private_key)=[^[:space:]"]'
# Bearer トークンの実値。
bearer_leak='Bearer[[:space:]]+[A-Za-z0-9._-]{16,}'
# Anthropic API キーの実値。
antkey_leak='sk-ant-[A-Za-z0-9_-]{16,}'
# JWT の実値（3 セグメントの base64url）。
jwt_leak='eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}'

# scan_stream <name> — stdin を走査し、ヒット行数を stdout へ返す。
scan_stream() {
  local name="$1" hits=0 line
  local combined="${json_leak}|${kv_leak}|${bearer_leak}|${antkey_leak}|${jwt_leak}"

  # [REDACTED] を含む行は、正しく伏せられた結果なので除外する。
  line="$(grep -aE "$combined" 2>/dev/null | grep -avF '[REDACTED]' || true)"
  if [ -n "$line" ]; then
    hits="$(printf '%s\n' "$line" | wc -l | tr -d ' ')"
    printf '  \033[31mFAIL\033[0m  %-12s %s 件の秘匿値らしき出力\n' "$name" "$hits" >&2
    # 全文は出さない（レポート自体が漏洩源になる）。先頭 120 文字だけ。
    printf '%s\n' "$line" | head -5 | cut -c1-120 | sed 's/^/          /' >&2
  else
    printf '  \033[32mOK\033[0m    %-12s 秘匿値の出力なし\n' "$name" >&2
  fi
  printf '%s' "$hits"
}

fail=0
results=()

echo "== log_secret_scan: 走査対象を収集 ==" >&2

if [ "$#" -gt 0 ]; then
  # 引数で渡されたファイル/ディレクトリを走査する。
  for target in "$@"; do
    if [ -d "$target" ]; then
      while IFS= read -r f; do
        hits="$(scan_stream "$(basename "$f")" < "$f")"
        [ "${hits:-0}" -gt 0 ] && fail=1
        results+=("{\"source\":\"${f}\",\"hits\":${hits:-0}}")
      done < <(find "$target" -type f -name '*.log' 2>/dev/null)
    elif [ -f "$target" ]; then
      hits="$(scan_stream "$(basename "$target")" < "$target")"
      [ "${hits:-0}" -gt 0 ] && fail=1
      results+=("{\"source\":\"${target}\",\"hits\":${hits:-0}}")
    else
      echo "  警告: ${target} が見つかりません" >&2
    fi
  done
else
  if ! command -v docker >/dev/null 2>&1; then
    echo "log_secret_scan.sh: docker 未導入のためスキップします。" >&2
    exit 0
  fi
  for svc in "${SERVICES[@]}"; do
    logs="$($COMPOSE logs --no-color --tail "$TAIL" "$svc" 2>/dev/null || true)"
    if [ -z "$logs" ]; then
      printf '  \033[33mSKIP\033[0m  %-12s ログがありません（未起動）\n' "$svc" >&2
      results+=("{\"source\":\"${svc}\",\"hits\":0,\"skipped\":true}")
      continue
    fi
    hits="$(printf '%s' "$logs" | scan_stream "$svc")"
    [ "${hits:-0}" -gt 0 ] && fail=1
    results+=("{\"source\":\"${svc}\",\"hits\":${hits:-0}}")
  done
fi

{
  printf '{\n'
  printf '  "generated_at": "%s",\n' "$ts"
  printf '  "scan": "log_secret_scan.sh",\n'
  printf '  "sources": [\n'
  for i in "${!results[@]}"; do
    printf '    %s' "${results[$i]}"
    [ "$i" -lt $((${#results[@]} - 1)) ] && printf ','
    printf '\n'
  done
  printf '  ],\n'
  printf '  "verdict": "%s"\n' "$([ "$fail" -eq 0 ] && echo PASS || echo FAIL)"
  printf '}\n'
} > "$out"

echo "log_secret_scan.sh: レポート -> ${out}" >&2
if [ "$fail" -ne 0 ]; then
  echo "log_secret_scan.sh: FAIL（ログに秘匿値が出ています / MVP-SEC-002）" >&2
  exit 1
fi
echo "log_secret_scan.sh: OK" >&2
