#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/build_artifact_index.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "build.artifact_index.run_dir_missing: $run_dir" >&2; exit 64; }

index_path="$run_dir/artifact_index.json"

python - "$run_dir" "$index_path" <<'PY'
import hashlib
import json
import pathlib
import sys

run_dir = pathlib.Path(sys.argv[1]).resolve()
out = pathlib.Path(sys.argv[2])
entries = []
for p in sorted(run_dir.rglob('*')):
    if not p.is_file():
        continue
    rel = p.relative_to(run_dir).as_posix()
    if rel == 'artifact_index.json':
        continue
    entries.append({
        'path': rel,
        'size': p.stat().st_size,
        'sha256': hashlib.sha256(p.read_bytes()).hexdigest(),
    })

out.write_text(json.dumps(entries, indent=2) + '\n', encoding='utf-8')
print('ARTIFACT_INDEX_OK=1')
print(f'ARTIFACT_COUNT={len(entries)}')
print(f'ARTIFACT_INDEX_PATH={out}')
PY
