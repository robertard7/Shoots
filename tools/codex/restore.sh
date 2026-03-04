#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

required_sdk="$(python - <<'PY'
import json
from pathlib import Path
print(json.loads(Path('global.json').read_text())['sdk']['version'])
PY
)"

export DOTNET_ROOT="${DOTNET_ROOT:-/root/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

if ! command -v dotnet >/dev/null 2>&1; then
  mkdir -p "$DOTNET_ROOT"
fi

sdk_dir="$DOTNET_ROOT/sdk/$required_sdk"
if [ ! -d "$sdk_dir" ]; then
  install_script="${TMPDIR:-/tmp}/dotnet-install.sh"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$install_script"
  bash "$install_script" --version "$required_sdk" --install-dir "$DOTNET_ROOT"
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "restore.dotnet.missing: dotnet executable not found after bootstrap" >&2
  exit 1
fi

dotnet --info
dotnet restore Shoots.sln
