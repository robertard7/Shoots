#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/sample_artifact_repro.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "sample.artifact_repro.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$run_dir/hashes.json" ]] || { echo "sample.artifact_repro.hashes_missing: $run_dir/hashes.json" >&2; exit 64; }

trace_path="$run_dir/trace/events.ndjson"
manifest_path="$run_dir/manifest.json"
[[ -f "$trace_path" ]] || { echo "sample.artifact_repro.trace_missing: $trace_path" >&2; exit 1; }
[[ -f "$manifest_path" ]] || { echo "sample.artifact_repro.manifest_missing: $manifest_path" >&2; exit 1; }

readarray -t checks < <(python - "$run_dir/hashes.json" "$trace_path" "$manifest_path" <<'PY'
import hashlib, json, pathlib, sys
hashes = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
trace = pathlib.Path(sys.argv[2])
manifest = pathlib.Path(sys.argv[3])
trace_sha = hashlib.sha256(trace.read_bytes()).hexdigest()
manifest_sha = hashlib.sha256(manifest.read_bytes()).hexdigest()
if hashes.get('traceHash') != trace_sha:
    raise SystemExit('sample.artifact_repro.trace_hash_mismatch')
if hashes.get('outputManifestHash') != manifest_sha:
    raise SystemExit('sample.artifact_repro.manifest_hash_mismatch')
print(trace_sha)
print(manifest_sha)
PY
)

sample_list="$(find "$run_dir" -type f | sort | awk 'NR % 5 == 1' | head -n 25)"
[[ -n "$sample_list" ]] || { echo "sample.artifact_repro.no_files" >&2; exit 1; }

sample_hash="$(
  while IFS= read -r file; do
    sha256sum "$file"
  done <<<"$sample_list" | sha256sum | awk '{print $1}'
)"
sample_count="$(wc -l <<<"$sample_list" | tr -d ' ')"

echo "ARTIFACT_SAMPLE_OK=1"
echo "ARTIFACT_SAMPLE_COUNT=$sample_count"
echo "ARTIFACT_SAMPLE_HASH=$sample_hash"
echo "ARTIFACT_TRACE_SHA256=${checks[0]}"
echo "ARTIFACT_MANIFEST_SHA256=${checks[1]}"
