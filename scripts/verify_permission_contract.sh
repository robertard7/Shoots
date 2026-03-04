#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_permission_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
index_path="$run_dir/artifact_index.json"
[[ -f "$index_path" ]] || { echo "verify.permission.index_missing: $index_path" >&2; exit 64; }

readarray -t vals < <(python - "$run_dir" "$index_path" <<'PY'
import json
import os
import pathlib
import stat
import sys

run_dir = pathlib.Path(sys.argv[1]).resolve()
index = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8'))

allow_exec_suffixes = ('.sh', '.ps1', '.exe', '.dll', '.so', '.dylib')
exec_count = 0
for row in index:
    rel = row.get('path', '')
    p = (run_dir / rel).resolve()
    if not p.exists() or not p.is_file():
        continue
    mode = p.stat().st_mode
    is_exec = bool(mode & (stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH))
    if is_exec:
        exec_count += 1
        if not rel.endswith(allow_exec_suffixes):
            raise SystemExit(f'verify.permission.unexpected_executable: {rel}')

print(exec_count)
PY
)

echo "PERMISSION_CONTRACT_OK=1"
echo "EXECUTABLE_COUNT=${vals[0]}"
