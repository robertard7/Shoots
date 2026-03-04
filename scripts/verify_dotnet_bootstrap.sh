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
  echo "verify.dotnet_bootstrap.missing_dotnet: dotnet is not on PATH. run bash tools/codex/restore.sh" >&2
  exit 1
fi

dotnet --info >/tmp/dotnet-info.txt 2>&1 || {
  cat /tmp/dotnet-info.txt >&2
  echo "verify.dotnet_bootstrap.dotnet_info_failed: dotnet --info failed" >&2
  exit 1
}

if ! dotnet --list-sdks | awk '{print $1}' | rg -x --fixed-strings "$required_sdk" >/dev/null 2>&1; then
  echo "verify.dotnet_bootstrap.sdk_missing: required SDK $required_sdk not found. run bash tools/codex/restore.sh" >&2
  exit 1
fi

echo "verify.dotnet_bootstrap.ok: sdk=$required_sdk"
