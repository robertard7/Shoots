#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_artifact_graph.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.artifact_graph.run_dir_missing: $run_dir" >&2; exit 64; }

manifest_path="$run_dir/manifest.json"
index_path="$run_dir/artifact_index.json"
[[ -f "$manifest_path" ]] || { echo "verify.artifact_graph.manifest_missing: $manifest_path" >&2; exit 64; }
[[ -f "$index_path" ]] || { echo "verify.artifact_graph.index_missing: $index_path" >&2; exit 64; }

python - "$run_dir" "$manifest_path" "$index_path" <<'PY'
import json
import pathlib
import sys

run_dir = pathlib.Path(sys.argv[1]).resolve()
manifest = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8'))
index_entries = json.loads(pathlib.Path(sys.argv[3]).read_text(encoding='utf-8'))

artifact_root = pathlib.Path(manifest.get('artifact_root', '')).resolve()
if not artifact_root.exists():
    raise SystemExit('verify.artifact_graph.artifact_root_missing')

if artifact_root != run_dir:
    raise SystemExit(f'verify.artifact_graph.artifact_root_mismatch: {artifact_root} != {run_dir}')

index_paths = set()
for entry in index_entries:
    rel = entry.get('path', '')
    if not rel:
        raise SystemExit('verify.artifact_graph.index_missing_path')
    p = (run_dir / rel).resolve()
    if not p.exists():
        raise SystemExit(f'verify.artifact_graph.index_references_missing: {rel}')
    if run_dir not in p.parents and p != run_dir:
        raise SystemExit(f'verify.artifact_graph.index_outside_run: {rel}')
    index_paths.add(p)

actual_paths = {p.resolve() for p in run_dir.rglob('*') if p.is_file() and p.name != 'artifact_index.json'}
orphan = sorted(str(p.relative_to(run_dir)) for p in actual_paths if p not in index_paths)

if orphan:
    raise SystemExit('verify.artifact_graph.orphans:' + ','.join(orphan[:20]))

print('ARTIFACT_GRAPH_OK=1')
print('ORPHAN_COUNT=0')
print(f'ARTIFACT_GRAPH_COUNT={len(index_paths)}')
PY
