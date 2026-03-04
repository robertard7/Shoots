#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

snapshot_path="artifacts/ci_env_snapshot.json"
mkdir -p artifacts

cpu_model="$(awk -F: '/model name/{gsub(/^[ \t]+/, "", $2); print $2; exit}' /proc/cpuinfo 2>/dev/null || true)"
cpu_count="$(nproc 2>/dev/null || echo 0)"
mem_total_kb="$(awk '/MemTotal:/{print $2; exit}' /proc/meminfo 2>/dev/null || echo 0)"
os_name="$(uname -s 2>/dev/null || echo unknown)"
kernel="$(uname -r 2>/dev/null || echo unknown)"
dotnet_sdks="$(dotnet --list-sdks 2>/dev/null || true)"

python - "$snapshot_path" "$cpu_model" "$cpu_count" "$mem_total_kb" "$os_name" "$kernel" "$dotnet_sdks" <<'PY'
import json
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
obj = {
    'cpu_model': sys.argv[2],
    'cpu_count': int(sys.argv[3]) if sys.argv[3].isdigit() else 0,
    'mem_total_kb': int(sys.argv[4]) if sys.argv[4].isdigit() else 0,
    'os': sys.argv[5],
    'kernel': sys.argv[6],
    'dotnet_sdks': [line.strip() for line in sys.argv[7].splitlines() if line.strip()],
}
path.write_text(json.dumps(obj, indent=2) + '\n', encoding='utf-8')
print('CI_ENV_SNAPSHOT_OK=1')
print(f'CI_ENV_SNAPSHOT_PATH={path}')
PY
