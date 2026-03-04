#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/replay_plan_graph.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "replay.plan_graph.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$run_dir/hashes.json" ]] || { echo "replay.plan_graph.hashes_missing: $run_dir/hashes.json" >&2; exit 64; }

bash scripts/replay_runner.sh "$run_dir" >/dev/null

readarray -t values < <(python - "$run_dir/hashes.json" "$run_dir/replay.json" <<'PY'
import json
import pathlib
import sys

hashes = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
replay = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8'))
orig_trace = hashes.get('traceHash', '')
new_trace = replay.get('actualTraceHash', '')
if not replay.get('pass', False):
    raise SystemExit('replay.plan_graph.replay_failed')
if orig_trace != new_trace:
    raise SystemExit(f'replay.plan_graph.trace_hash_mismatch: {orig_trace} != {new_trace}')
print(orig_trace)
PY
)

echo "PLAN_REPLAY_OK=1"
echo "PLAN_REPLAY_TRACE_SHA256=${values[0]}"
echo "PLAN_REPLAY_RUN_DIR=$run_dir"
