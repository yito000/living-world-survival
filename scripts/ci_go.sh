#!/usr/bin/env bash
# Go の Lint / Test / Security（03B 5.2 / M7 10B 3.6）。
# usage: ci_go.sh [lint|test|security]
#
#   security … MVP-SEC-00x を裏取りする Go テストだけを回す（10B 3.4）。
#              DB 依存の Integration は DB 不在なら自己 skip するので、
#              `make ci`（サーバー不要）でも失敗しない。
set -euo pipefail

cmd="${1:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
# services/gen/go は生成 gRPC/protobuf スタブの module（2.1）。auth が replace で
# 参照するため、ビルド対象に含めてスタブ自体の解決を保証する。
# services/common は M7 で追加した可観測性の共有 module（10B 3.5）。auth/api が
# replace で参照するので、被参照側として先に検査する。
services=(services/gen/go services/common services/auth services/api)

if ! command -v go >/dev/null 2>&1; then
  echo "go 未導入: Go サービスの CI をスキップします。"
  exit 0
fi

for s in "${services[@]}"; do
  echo "== go ${cmd}: ${s} =="
  (
    cd "$s"
    # go.sum が未コミットでも解決できるよう依存グラフを確定（初回はネットワーク必要）。
    go mod tidy
    case "$cmd" in
      lint)
        fmt="$(gofmt -l .)"
        if [ -n "$fmt" ]; then
          echo "gofmt 差分あり:"; echo "$fmt"; exit 1
        fi
        go vet ./...
        if command -v golangci-lint >/dev/null 2>&1; then
          golangci-lint run ./...
        else
          echo "golangci-lint 未導入: skip"
        fi
        ;;
      test)
        go test ./... -race -count=1 -coverprofile=coverage.out
        GOOS=linux GOARCH=amd64 go build ./...
        ;;
      security)
        # 名前で拾う: Security / RateLimit / Redeem(JoinTicket 単回・不一致) /
        # Refresh(ローテーション・再利用検知) / Redact(ログ秘匿) /
        # OWASP(Argon2id パラメータ下限) / Tamper(改竄拒否) / Ownership。
        # -run に一致するテストが無いモジュールは 0 件で成功扱い（正常）。
        #
        # 注意: パターンに漏れたテストは黙って回らない。security テストを足したら
        # ここにも名前が載るようにすること。
        go test ./... -count=1 \
          -run 'Security|RateLimit|Redeem|Refresh|Redact|Secret|Argon2|OWASP|Password|Ownership|Tamper|Expired'
        ;;
      *)
        echo "usage: ci_go.sh [lint|test|security]"; exit 2
        ;;
    esac
  )
done
echo "ci_go.sh ${cmd}: OK"
