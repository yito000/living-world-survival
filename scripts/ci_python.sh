#!/usr/bin/env bash
# Python の Lint / Test（03B 5.3）。usage: ci_python.sh [lint|test]
set -euo pipefail

cmd="${1:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

# CI は実 LLM を絶対に呼ばない（08B 3.9）。llm-worker のテストは LLM をモッククライアントで
# 差し替えるが、環境に ANTHROPIC_API_KEY が居ても実 API を叩かないよう明示的に固定する。
export LLM_MOCK=1

# 対象サービス: "dir:package:import_check"
targets=(
  "services/worldstate:app:app.main"
  "services/llm-worker:worker:worker.main"
  "assets-pipeline::"    # package/import 検査なし（トップレベル module）
)

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 未導入: Python サービスの CI をスキップします。"
  exit 0
fi

USE_UV=0
command -v uv >/dev/null 2>&1 && USE_UV=1

# 依存を用意（uv sync もしくは venv + pip install -e .[dev]）。
ensure_env() {
  local dir="$1"
  if [ "$USE_UV" -eq 1 ]; then
    (cd "$dir" && uv sync --extra dev)
  else
    (cd "$dir" && python3 -m venv .venv && . .venv/bin/activate && \
      python -m pip install -q --upgrade pip && python -m pip install -q -e '.[dev]')
  fi
}

# サービス内でコマンド実行（uv run もしくは venv 有効化）。
run_in() {
  local dir="$1"; shift
  if [ "$USE_UV" -eq 1 ]; then
    (cd "$dir" && uv run "$@")
  else
    (cd "$dir" && . .venv/bin/activate && "$@")
  fi
}

for t in "${targets[@]}"; do
  IFS=":" read -r dir pkg imp <<<"$t"
  echo "== python ${cmd}: ${dir} =="
  ensure_env "$dir"
  case "$cmd" in
    lint)
      run_in "$dir" ruff check .
      run_in "$dir" ruff format --check .
      if [ -n "$pkg" ]; then
        run_in "$dir" mypy "$pkg" || echo "mypy 警告あり（任意扱い）: ${dir}"
      fi
      ;;
    test)
      run_in "$dir" pytest -q
      if [ -n "$imp" ]; then
        run_in "$dir" python -c "import ${imp}"
      fi
      ;;
    *)
      echo "usage: ci_python.sh [lint|test]"; exit 2
      ;;
  esac
done
echo "ci_python.sh ${cmd}: OK"
