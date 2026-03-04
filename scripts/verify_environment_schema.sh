#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_environment_schema.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
env_path="$run_dir/environment.json"
[[ -d "$run_dir" ]] || { echo "verify.environment.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$env_path" ]] || { echo "verify.environment.missing: $env_path" >&2; exit 64; }

python - "$env_path" <<'PY'
import json
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
obj = json.loads(path.read_text(encoding='utf-8'))
if not isinstance(obj, dict):
    raise SystemExit('verify.environment.not_object')

required = {'captured_at_utc', 'git_commit', 'os', 'kernel', 'dotnet_info'}
missing = sorted(required - set(obj.keys()))
if missing:
    raise SystemExit(f"verify.environment.missing_keys: {','.join(missing)}")

if sorted(obj.keys()) != sorted(required):
    extra = sorted(set(obj.keys()) - required)
    if extra:
      raise SystemExit(f"verify.environment.unexpected_keys: {','.join(extra)}")

if not re.match(r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$', str(obj.get('captured_at_utc', ''))):
    raise SystemExit('verify.environment.bad_captured_at')
if not re.match(r'^[0-9a-f]{40}$', str(obj.get('git_commit', ''))):
    raise SystemExit('verify.environment.bad_git_commit')
for key in ('os', 'kernel', 'dotnet_info'):
    val = obj.get(key)
    if not isinstance(val, str) or not val.strip():
        raise SystemExit(f'verify.environment.bad_{key}')

print('ENVIRONMENT_SCHEMA_OK=1')
print(f'ENVIRONMENT_PATH={path}')
PY
