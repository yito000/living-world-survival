#!/usr/bin/env bash
# Blender アセット生成 + 検査（03B 6章）。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

SEED="${SEED:-1}"
MODULE_SIZE="${MODULE_SIZE:-4}"
OUT="${OUT:-build/assets}"

# Blender の解決順:
#   1) 明示指定 BLENDER=...
#   2) Linux ネイティブ blender
#   3) WSL interop 経由の Windows blender.exe（PATH 上）
# いずれも無ければ skip（CI を止めない）。
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  if command -v blender >/dev/null 2>&1; then
    BLENDER=blender
  elif command -v blender.exe >/dev/null 2>&1; then
    BLENDER=blender.exe
    echo "WSL: Linux 版 blender が無いため Windows の blender.exe を使用します。"
  else
    BLENDER=blender
  fi
fi

if ! command -v "$BLENDER" >/dev/null 2>&1; then
  echo "Blender 未導入 ($BLENDER): アセット生成をスキップします。"
  echo "  Linux: sudo apt install blender / Windows Blender: BLENDER=blender.exe make assets"
  exit 0
fi

echo "== generate (seed=$SEED size=$MODULE_SIZE) =="
"$BLENDER" --background --python assets-pipeline/generate.py -- \
  --seed "$SEED" --module-size "$MODULE_SIZE" --out "$OUT"

echo "== validate =="
python3 assets-pipeline/validate.py --in "$OUT"

echo "ci_assets.sh: OK"
