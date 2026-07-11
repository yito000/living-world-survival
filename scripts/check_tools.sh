#!/usr/bin/env bash
# 必要ツールの存在確認（03B 2章 / make bootstrap）。
set -uo pipefail

missing=0
warn=0

check() { # check <cmd> <required|optional> <hint>
  local cmd="$1" level="$2" hint="${3:-}"
  if command -v "$cmd" >/dev/null 2>&1; then
    printf "  \033[32mOK\033[0m    %-16s %s\n" "$cmd" "$("$cmd" --version 2>&1 | head -1)"
  elif [ "$level" = "required" ]; then
    printf "  \033[31mMISS\033[0m  %-16s (required) %s\n" "$cmd" "$hint"
    missing=$((missing+1))
  else
    printf "  \033[33mSKIP\033[0m  %-16s (optional) %s\n" "$cmd" "$hint"
    warn=$((warn+1))
  fi
}

# docker は「存在」だけでなく「daemon に到達できるか」まで確認する。
# WSL Integration 未有効だと /mnt/c のシムが解決され command -v は成功してしまうため。
check_docker() {
  if ! command -v docker >/dev/null 2>&1; then
    printf "  \033[31mMISS\033[0m  %-16s (required) Docker Desktop を導入\n" "docker"
    missing=$((missing+1)); return
  fi
  local out rc
  out="$(docker version --format '{{.Server.Version}}' 2>&1)"; rc=$?
  if [ "$rc" -eq 0 ] && [ -n "$out" ] && [ "${out#*could not be found}" = "$out" ]; then
    printf "  \033[32mOK\033[0m    %-16s server %s\n" "docker" "$out"
    return
  fi
  printf "  \033[31mFAIL\033[0m  %-16s daemon に接続できません\n" "docker"
  case "$out" in
    *"could not be found in this WSL 2 distro"*)
      echo "        → Docker Desktop の WSL Integration が未有効です（distro: ${WSL_DISTRO_NAME:-?}）。"
      echo "          Settings → Resources → WSL Integration で '${WSL_DISTRO_NAME:-このdistro}' を ON → Apply & Restart。"
      echo "          反映後に新しいシェルを開く（必要なら 'wsl.exe --shutdown' 後に再起動）。" ;;
    *)
      echo "        → Docker Desktop が起動しているか確認してください。詳細: $out" ;;
  esac
  missing=$((missing+1))
}

if command -v mise >/dev/null 2>&1; then
  echo "mise 検出: go/python/uv/buf/golangci-lint/migrate/pre-commit は 'mise install' で一括導入できます（mise.toml）。"
  echo
fi

echo "== 必須ツール =="
check_docker
check git            required "sudo apt install git"
check git-lfs        required "sudo apt install git-lfs && git lfs install"
check go             required "Go 1.22+"
check python3        required "Python 3.11+"
check buf            required "https://buf.build/docs/installation"
check migrate        required "golang-migrate"
check make           required "標準ツール"

echo "== 推奨/任意ツール =="
check mise           optional "版一括管理（mise trust && mise install）"
check golangci-lint  optional "Go Lint"
check uv             optional "Python 依存管理 (pip install uv)"
check ruff           optional "Python Lint（uv 経由でも可）"
check pre-commit     optional "pip install pre-commit"
check blender        optional "make assets 用（BLENDER=blender.exe でも可）"
check act            optional "GitHub Actions ローカル実行"

echo
if [ "$missing" -gt 0 ]; then
  echo "必須ツールが $missing 個不足しています。導入してください。"
  exit 1
fi
echo "必須ツールは揃っています（任意: $warn 個未導入）。"
