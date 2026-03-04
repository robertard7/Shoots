#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_smoke_artifacts.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.smoke_artifacts.run_dir_missing: $run_dir" >&2; exit 64; }

required=(
  "trace/events.ndjson"
  "hashes.json"
  "manifest.json"
  "environment.json"
)

for rel in "${required[@]}"; do
  full="$run_dir/$rel"
  [[ -f "$full" ]] || { echo "verify.smoke_artifacts.missing: $full" >&2; exit 1; }
  [[ -s "$full" ]] || { echo "verify.smoke_artifacts.empty: $full" >&2; exit 1; }
done

echo "SMOKE_ARTIFACTS_OK=1"
echo "RUN_DIR=$run_dir"
echo "TRACE_PATH=$run_dir/trace/events.ndjson"
