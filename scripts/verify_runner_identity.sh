#!/usr/bin/env bash
set -euo pipefail

summary_file="artifacts/smoke/latest_summary.env"
[[ -f "$summary_file" ]] || { echo "verify.runner_identity.summary_missing: $summary_file" >&2; exit 64; }
# shellcheck disable=SC1091
source "$summary_file"

run_dir="${RUN_DIR:-}"
[[ -n "$run_dir" && -d "$run_dir" ]] || { echo "verify.runner_identity.run_dir_missing" >&2; exit 64; }

env_path="$run_dir/environment.json"
[[ -f "$env_path" ]] || { echo "verify.runner_identity.environment_missing: $env_path" >&2; exit 64; }

python - "$env_path" <<'PY'
import json
import pathlib
import re
import sys

obj = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
forbidden = {'hostname', 'machine_id', 'container_id', 'runner_name'}
present = sorted(forbidden.intersection(obj.keys()))
if present:
    raise SystemExit('verify.runner_identity.forbidden_fields:' + ','.join(present))

print('RUNNER_IDENTITY_OK=1')
PY
