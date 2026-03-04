#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/replay_runner.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "replay.runner.dotnet_missing: dotnet is required" >&2
  exit 127
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "replay.runner.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$run_dir/hashes.json" ]] || { echo "replay.runner.hashes_missing: $run_dir/hashes.json" >&2; exit 64; }

before_sha="$(python - "$run_dir/hashes.json" <<'PY'
import hashlib, pathlib, sys
print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())
PY
)"

before_fields="$(python - "$run_dir/hashes.json" <<'PY'
import json, pathlib, sys
obj=json.loads(pathlib.Path(sys.argv[1]).read_text())
print(obj.get('planHash',''))
print(obj.get('traceHash',''))
print(obj.get('outputManifestHash',''))
PY
)"

replay_log="$run_dir/replay.stdout.log"
dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- replay --run "$run_dir" >"$replay_log" 2>&1

[[ -f "$run_dir/replay.json" ]] || { echo "replay.runner.replay_json_missing: $run_dir/replay.json" >&2; exit 1; }

readarray -t replay_fields < <(python - "$run_dir/replay.json" <<'PY'
import json, pathlib, sys
obj=json.loads(pathlib.Path(sys.argv[1]).read_text())
print('1' if obj.get('pass') else '0')
print(obj.get('expectedTraceHash',''))
print(obj.get('actualTraceHash',''))
print(obj.get('expectedManifestHash',''))
print(obj.get('actualManifestHash',''))
PY
)

[[ "${replay_fields[0]}" == "1" ]] || { echo "replay.runner.failed: replay reported pass=false" >&2; exit 1; }
[[ "${replay_fields[1]}" == "${replay_fields[2]}" ]] || { echo "replay.runner.trace_mismatch: ${replay_fields[1]} != ${replay_fields[2]}" >&2; exit 1; }
[[ "${replay_fields[3]}" == "${replay_fields[4]}" ]] || { echo "replay.runner.manifest_mismatch: ${replay_fields[3]} != ${replay_fields[4]}" >&2; exit 1; }

after_sha="$(python - "$run_dir/hashes.json" <<'PY'
import hashlib, pathlib, sys
print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())
PY
)"

[[ "$before_sha" == "$after_sha" ]] || { echo "replay.runner.hashes_json_changed: $before_sha != $after_sha" >&2; exit 1; }

readarray -t after_fields < <(python - "$run_dir/hashes.json" <<'PY'
import json, pathlib, sys
obj=json.loads(pathlib.Path(sys.argv[1]).read_text())
print(obj.get('planHash',''))
print(obj.get('traceHash',''))
print(obj.get('outputManifestHash',''))
PY
)

readarray -t before_lines <<<"$before_fields"
[[ "${before_lines[0]}" == "${after_fields[0]}" ]] || { echo "replay.runner.plan_hash_changed" >&2; exit 1; }
[[ "${before_lines[1]}" == "${after_fields[1]}" ]] || { echo "replay.runner.trace_hash_changed" >&2; exit 1; }
[[ "${before_lines[2]}" == "${after_fields[2]}" ]] || { echo "replay.runner.manifest_hash_changed" >&2; exit 1; }

echo "REPLAY_RUNNER_OK=1"
echo "RUN_DIR=${run_dir}"
echo "HASHES_SHA256_BEFORE=${before_sha}"
echo "HASHES_SHA256_AFTER=${after_sha}"
echo "TRACE_HASH=${after_fields[1]}"
echo "MANIFEST_HASH=${after_fields[2]}"
