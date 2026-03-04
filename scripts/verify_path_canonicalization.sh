#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_path_canonicalization.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
index_path="$run_dir/artifact_index.json"
manifest_path="$run_dir/manifest.json"
[[ -f "$index_path" ]] || { echo "verify.path_canon.index_missing: $index_path" >&2; exit 64; }
[[ -f "$manifest_path" ]] || { echo "verify.path_canon.manifest_missing: $manifest_path" >&2; exit 64; }

python - "$index_path" "$manifest_path" <<'PY'
import json, pathlib, re, sys

bad = []

index = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
for row in index:
    p = str(row.get('path',''))
    if not p:
        bad.append('index:<empty>')
        continue
    if '../' in p or p.startswith('../') or p.startswith('/') or '\\' in p or '//' in p:
        bad.append(f'index:{p}')

manifest = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8'))
for key in ('artifact_root',):
    value = str(manifest.get(key,''))
    if '../' in value or '\\' in value or '//' in value:
        bad.append(f'manifest:{key}:{value}')

if bad:
    raise SystemExit('verify.path_canon.invalid_paths:' + ','.join(bad[:20]))

print('PATH_CANONICALIZATION_OK=1')
PY
