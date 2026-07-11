#!/usr/bin/env bash
# proto lint + generate + ドリフト検査（03B 5.4）。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if ! command -v buf >/dev/null 2>&1; then
  echo "buf 未導入: proto CI をスキップします（bootstrap で導入してください）。"
  exit 0
fi

echo "== buf lint =="
buf lint proto

echo "== buf generate =="
# buf.gen.yaml はリポジトリ直下基準の出力パスを持つため root で実行。
buf generate proto --template proto/buf.gen.yaml

# 生成物が各所へ出力されるパス（0.4/0.5）。
gen_paths=(services/gen proto unity/SurvivalWorld/Assets/Generated)

echo "== drift 検査 =="
if command -v git >/dev/null 2>&1 && git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  # 追跡ファイルの変更
  if ! git -C "$root" diff --exit-code -- "${gen_paths[@]}"; then
    echo "ERROR: proto 生成物にドリフトがあります。buf generate 後の差分をコミットしてください。" >&2
    exit 1
  fi
  # 未追跡の新規生成ファイル（git diff では検出できない）
  untracked="$(git -C "$root" ls-files --others --exclude-standard -- "${gen_paths[@]}")"
  if [ -n "$untracked" ]; then
    echo "ERROR: 未コミットの新規生成ファイルがあります:" >&2
    echo "$untracked" >&2
    exit 1
  fi
  # main がある場合のみ breaking 検査
  if git -C "$root" show-ref --verify --quiet refs/heads/main; then
    echo "== buf breaking (against main) =="
    buf breaking proto --against ".git#branch=main,subdir=proto" || {
      echo "WARN: breaking 検査に失敗（main 比較）。"; }
  fi
else
  echo "git リポジトリ外のため drift/breaking 検査をスキップします。"
fi

echo "ci_proto.sh: OK"
