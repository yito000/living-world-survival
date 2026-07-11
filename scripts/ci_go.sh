#!/usr/bin/env bash
# Go の Lint / Test（03B 5.2）。usage: ci_go.sh [lint|test]
set -euo pipefail

cmd="${1:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
services=(services/auth services/api)

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
      *)
        echo "usage: ci_go.sh [lint|test]"; exit 2
        ;;
    esac
  )
done
echo "ci_go.sh ${cmd}: OK"
