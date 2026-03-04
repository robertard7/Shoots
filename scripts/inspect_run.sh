#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/inspect_run.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "inspect.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$run_dir/run.json" ]] || { echo "inspect.run_json_missing: $run_dir/run.json" >&2; exit 64; }
[[ -f "$run_dir/hashes.json" ]] || { echo "inspect.hashes_missing: $run_dir/hashes.json" >&2; exit 64; }

python - "$run_dir" <<'PY'
import json, pathlib, sys
run_dir=pathlib.Path(sys.argv[1])
run=json.loads((run_dir/'run.json').read_text())
hashes=json.loads((run_dir/'hashes.json').read_text())
summary={
  'RUN_DIR': str(run_dir),
  'RUN_ID': run.get('runId',''),
  'PLAN_HASH': hashes.get('planHash',''),
  'PROVIDER_HASH': hashes.get('providerHash',''),
  'ENV_HASH': hashes.get('envHash',''),
  'TRACE_HASH': hashes.get('traceHash',''),
  'MANIFEST_HASH': hashes.get('outputManifestHash',''),
  'TRACE_FILE': str(run_dir/'trace/events.ndjson'),
  'NARRATION_FILE': str(run_dir/'narration/events.ndjson'),
  'HASHES_FILE': str(run_dir/'hashes.json')
}
for k,v in summary.items():
  print(f"{k}={v}")
PY
