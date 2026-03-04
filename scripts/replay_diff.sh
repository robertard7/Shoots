#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/replay_diff.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "replay.diff.run_dir_missing: $run_dir" >&2; exit 64; }

if [[ ! -f "$run_dir/replay.json" ]]; then
  bash scripts/replay_runner.sh "$run_dir"
fi

[[ -f "$run_dir/replay.json" ]] || { echo "replay.diff.replay_json_missing: $run_dir/replay.json" >&2; exit 1; }
[[ -f "$run_dir/manifest.json" ]] || { echo "replay.diff.manifest_missing: $run_dir/manifest.json" >&2; exit 1; }
[[ -f "$run_dir/hashes.json" ]] || { echo "replay.diff.hashes_missing: $run_dir/hashes.json" >&2; exit 1; }

python - "$run_dir/replay.json" "$run_dir/manifest.json" "$run_dir/hashes.json" <<'PY'
import json
import pathlib
import sys

replay = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
manifest = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8'))
hashes = json.loads(pathlib.Path(sys.argv[3]).read_text(encoding='utf-8'))

sections = []

exp_trace = replay.get('expectedTraceHash', '')
act_trace = replay.get('actualTraceHash', '')
if exp_trace != act_trace:
    sections.append('trace')

exp_manifest = replay.get('expectedManifestHash', '')
act_manifest = replay.get('actualManifestHash', '')
if exp_manifest != act_manifest:
    sections.append('manifest')

if hashes.get('traceHash', '') != manifest.get('trace_sha256', ''):
    sections.append('artifact')

if not sections:
    print('REPLAY_DIFF=0')
    print('DIFF_SECTION=none')
    raise SystemExit(0)

print('REPLAY_DIFF=1')
for section in sorted(set(sections)):
    print(f'DIFF_SECTION={section}')
PY
