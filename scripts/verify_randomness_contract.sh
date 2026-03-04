#!/usr/bin/env bash
set -euo pipefail

summary_file="artifacts/smoke/latest_summary.env"
[[ -f "$summary_file" ]] || { echo "verify.randomness.summary_missing: $summary_file" >&2; exit 64; }
# shellcheck disable=SC1091
source "$summary_file"

run_dir="${RUN_DIR:-}"
[[ -n "$run_dir" && -d "$run_dir" ]] || { echo "verify.randomness.run_dir_missing" >&2; exit 64; }

env_path="$run_dir/environment.json"
trace_path="$run_dir/trace/events.ndjson"
[[ -f "$env_path" ]] || { echo "verify.randomness.environment_missing: $env_path" >&2; exit 64; }
[[ -f "$trace_path" ]] || { echo "verify.randomness.trace_missing: $trace_path" >&2; exit 64; }

readarray -t vals < <(python - "$env_path" "$trace_path" <<'PY'
import json
import pathlib
import re
import sys

env = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
trace_text = pathlib.Path(sys.argv[2]).read_text(encoding='utf-8')

seed = str(env.get('random_seed', ''))
if seed and not re.fullmatch(r'[A-Za-z0-9._:-]{1,128}', seed):
    raise SystemExit('verify.randomness.bad_seed_format')

# Ensure no obvious random UUID-ish field names leak into deterministic trace surface.
if re.search(r'"(?:random|nonce|uuid|guid)"\s*:', trace_text, flags=re.IGNORECASE):
    raise SystemExit('verify.randomness.random_field_detected_in_trace')

print(seed if seed else '<none>')
PY
)

echo "RANDOMNESS_CONTRACT_OK=1"
echo "SEED_VALUE=${vals[0]}"
