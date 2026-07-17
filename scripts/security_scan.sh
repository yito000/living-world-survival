#!/usr/bin/env bash
# M7 Security 静的検査（10B 3.4 / MVP第17章）。
#
# 「動かさなくても確認できる」security 要件を検査する:
#   MVP-SEC-001  Client から API/WorldState への経路を公開しない（RC プロファイル）
#   MVP-SEC-007  Secret を Repo に保存しない / 内部 RPC のサービス認証
#   sslmode      RC プロファイルは require（ローカルは disable のまま）
#
# 実際に走らせないと分からない検査（Refresh rotation・Join Ticket・価格改竄・
# Rate Limit 等）は Go の Integration テスト側にある（make security が両方回す）。
#
# 結果は build/reports/security_<ts>.json へ出す。1 件でも FAIL なら exit 1。
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

DEV_COMPOSE="infra/docker-compose.yml"
RC_COMPOSE="infra/docker-compose.rc.yml"
RC_ENV_EXAMPLE=".env.rc.example"

ts="$(date -u +%Y%m%dT%H%M%SZ)"
outdir="build/reports"
out="${outdir}/security_${ts}.json"
mkdir -p "$outdir"

fail=0
results=()

# record <id> <status> <detail>
record() {
  local id="$1" status="$2" detail="$3"
  local color reset='\033[0m'
  case "$status" in
    PASS) color='\033[32m' ;;
    SKIP) color='\033[33m' ;;
    *)    color='\033[31m'; fail=1 ;;
  esac
  printf "  ${color}%-4s${reset} %-12s %s\n" "$status" "$id" "$detail"
  # JSON へ入れるので detail のクォートとバックスラッシュを潰す。
  detail="${detail//\\/\\\\}"
  detail="${detail//\"/\\\"}"
  results+=("{\"id\":\"${id}\",\"status\":\"${status}\",\"detail\":\"${detail}\"}")
}

echo "== security_scan: MVP-SEC-001 経路の公開範囲 =="

# RC プロファイルで「外部へ publish してよい」のは auth だけ。
# api/worldstate は内部専用（MVP-SEC-001）、llm-worker は Client 接点なし、
# postgres/nats は内部インフラ。DS は compose 管理外のネイティブプロセス。
internal_only=(api worldstate llm-worker postgres nats)

if [ ! -f "$RC_COMPOSE" ]; then
  record "MVP-SEC-001" "FAIL" "RC プロファイル ${RC_COMPOSE} がありません"
elif ! command -v docker >/dev/null 2>&1; then
  record "MVP-SEC-001" "SKIP" "docker 未導入のため compose config を解決できません"
else
  # `docker compose config` で override をマージした**実効値**を見る。
  # 素の YAML を grep すると、override の打ち消しを見落とす。
  rc_config="$(docker compose -f "$DEV_COMPOSE" -f "$RC_COMPOSE" --env-file "$RC_ENV_EXAMPLE" config 2>/dev/null)"
  if [ -z "$rc_config" ]; then
    record "MVP-SEC-001" "FAIL" "docker compose config が RC プロファイルを解決できません"
  else
    for svc in "${internal_only[@]}"; do
      # 対象サービスのブロックだけを切り出し、published: が無いことを見る。
      published="$(printf '%s' "$rc_config" | awk -v s="  ${svc}:" '
        $0 == s { inblock=1; next }
        inblock && /^  [a-z]/ { inblock=0 }
        inblock && /published:/ { print }
      ')"
      if [ -n "$published" ]; then
        record "MVP-SEC-001" "FAIL" \
          "${svc} が RC でホストへ publish されています: $(printf '%s' "$published" | tr -d ' \n')"
      else
        record "MVP-SEC-001" "PASS" "${svc} は RC で外部公開されていません"
      fi
    done
    # auth は外部境界なので publish されていること（過剰に閉じても検知する）。
    auth_published="$(printf '%s' "$rc_config" | awk '
      $0 == "  auth:" { inblock=1; next }
      inblock && /^  [a-z]/ { inblock=0 }
      inblock && /published:/ { print }
    ')"
    if [ -n "$auth_published" ]; then
      record "MVP-SEC-001" "PASS" "auth は外部境界として公開されています"
    else
      record "MVP-SEC-001" "FAIL" "auth が公開されていません（Client が接続できません）"
    fi
  fi
fi

echo "== security_scan: sslmode（ローカル=disable / RC=require） =="

if [ ! -f "$RC_ENV_EXAMPLE" ]; then
  record "sslmode" "FAIL" "${RC_ENV_EXAMPLE} がありません"
else
  # RC で sslmode=disable が残っていたら失敗（10B 3.4 / 落とし穴）。
  if grep -nE '^[^#]*DATABASE_URL.*sslmode=disable' "$RC_ENV_EXAMPLE" >/dev/null 2>&1; then
    record "sslmode" "FAIL" "${RC_ENV_EXAMPLE} に sslmode=disable があります（RC は require）"
  elif grep -nE '^[^#]*DATABASE_URL.*sslmode=require' "$RC_ENV_EXAMPLE" >/dev/null 2>&1; then
    record "sslmode" "PASS" "${RC_ENV_EXAMPLE} は sslmode=require"
  else
    record "sslmode" "FAIL" "${RC_ENV_EXAMPLE} の DATABASE_URL に sslmode=require がありません"
  fi
fi

echo "== security_scan: MVP-SEC-007 Secret を Repo に保存しない =="

# .env は追跡されていてはならない（実値が入るファイル）。
if git ls-files --error-unmatch .env >/dev/null 2>&1; then
  record "MVP-SEC-007" "FAIL" ".env が git 追跡下にあります"
else
  record "MVP-SEC-007" "PASS" ".env は git 追跡外"
fi
if git check-ignore -q .env 2>/dev/null; then
  record "MVP-SEC-007" "PASS" ".env は gitignore 済み"
else
  record "MVP-SEC-007" "FAIL" ".env が gitignore されていません"
fi

# 追跡ファイルに秘密鍵の PEM ブロックが無いこと。
# .env.example の dev 用ダミー鍵は「開発用と明示された既知の値」なので対象外
# （RC は .env.rc.example のプレースホルダを実値へ差し替えて使う）。
pem_hits="$(git grep -lE 'BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY' -- \
  ':!*.example' ':!docs/*' 2>/dev/null || true)"
if [ -n "$pem_hits" ]; then
  record "MVP-SEC-007" "FAIL" "追跡ファイルに秘密鍵ブロック: $(echo "$pem_hits" | tr '\n' ' ')"
else
  record "MVP-SEC-007" "PASS" "追跡ファイルに秘密鍵ブロックなし"
fi

# 実 API キーらしき値（Anthropic の sk-ant-...）が混入していないこと。
key_hits="$(git grep -lE 'sk-ant-[A-Za-z0-9_-]{16,}' -- ':!docs/*' 2>/dev/null || true)"
if [ -n "$key_hits" ]; then
  record "MVP-SEC-007" "FAIL" "追跡ファイルに API キー: $(echo "$key_hits" | tr '\n' ' ')"
else
  record "MVP-SEC-007" "PASS" "追跡ファイルに API キーなし"
fi

# RC は内部 gRPC の共有シークレットを必須にする（空のままなら認証が無効）。
if [ -f "$RC_ENV_EXAMPLE" ]; then
  missing=""
  for k in AUTH_GRPC_SHARED_SECRET API_GRPC_SHARED_SECRET; do
    # 「キー=」で終わる（値が空）なら、RC で認証が無効のまま出荷されうる。
    if grep -qE "^${k}=\s*$" "$RC_ENV_EXAMPLE"; then
      missing="${missing} ${k}"
    fi
  done
  if [ -n "$missing" ]; then
    record "MVP-SEC-007" "FAIL" "RC で共有シークレットが空:${missing}"
  else
    record "MVP-SEC-007" "PASS" "RC は内部 gRPC の共有シークレットを要求"
  fi
fi

# --- レポート出力 -----------------------------------------------------------
{
  printf '{\n'
  printf '  "generated_at": "%s",\n' "$ts"
  printf '  "scan": "security_scan.sh",\n'
  printf '  "checks": [\n'
  for i in "${!results[@]}"; do
    printf '    %s' "${results[$i]}"
    [ "$i" -lt $((${#results[@]} - 1)) ] && printf ','
    printf '\n'
  done
  printf '  ],\n'
  printf '  "verdict": "%s"\n' "$([ "$fail" -eq 0 ] && echo PASS || echo FAIL)"
  printf '}\n'
} > "$out"

echo "security_scan.sh: レポート -> ${out}"
if [ "$fail" -ne 0 ]; then
  echo "security_scan.sh: FAIL（上記の FAIL 項目を参照）" >&2
  exit 1
fi
echo "security_scan.sh: OK"
