#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_artifact_bounds.sh <RUN_DIR>

Environment overrides:
  TRACE_MAX_BYTES (default: 10485760)
  ARTIFACT_TOTAL_MAX_BYTES (default: 52428800)
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
trace_path="$run_dir/trace/events.ndjson"
[[ -d "$run_dir" ]] || { echo "verify.bounds.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$trace_path" ]] || { echo "verify.bounds.trace_missing: $trace_path" >&2; exit 64; }

trace_max="${TRACE_MAX_BYTES:-10485760}"
artifact_max="${ARTIFACT_TOTAL_MAX_BYTES:-52428800}"

trace_size="$(wc -c < "$trace_path" | awk '{print $1}')"
artifact_total="$(python - "$run_dir" <<'PY'
import pathlib, sys
root=pathlib.Path(sys.argv[1])
print(sum(p.stat().st_size for p in root.rglob("*") if p.is_file()))
PY
)"
artifact_total="$(find "$run_dir" -type f -print0 | du --files0-from=- -cb | tail -n1 | awk '{print $1}')"

if (( trace_size > trace_max )); then
  echo "ARTIFACT_BOUNDS_OK=0"
  echo "TRACE_SIZE_BYTES=$trace_size"
  echo "TRACE_MAX_BYTES=$trace_max"
  echo "ARTIFACT_TOTAL_BYTES=$artifact_total"
  echo "ARTIFACT_TOTAL_MAX_BYTES=$artifact_max"
  echo "verify.bounds.trace_too_large" >&2
  exit 1
fi

if (( artifact_total > artifact_max )); then
  echo "ARTIFACT_BOUNDS_OK=0"
  echo "TRACE_SIZE_BYTES=$trace_size"
  echo "TRACE_MAX_BYTES=$trace_max"
  echo "ARTIFACT_TOTAL_BYTES=$artifact_total"
  echo "ARTIFACT_TOTAL_MAX_BYTES=$artifact_max"
  echo "verify.bounds.artifacts_too_large" >&2
  exit 1
fi

echo "ARTIFACT_BOUNDS_OK=1"
echo "TRACE_SIZE_BYTES=$trace_size"
echo "TRACE_MAX_BYTES=$trace_max"
echo "ARTIFACT_TOTAL_BYTES=$artifact_total"
echo "ARTIFACT_TOTAL_MAX_BYTES=$artifact_max"
