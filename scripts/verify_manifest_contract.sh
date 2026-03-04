#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_manifest_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
manifest_path="$run_dir/manifest.json"
[[ -d "$run_dir" ]] || { echo "verify.manifest.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$manifest_path" ]] || { echo "verify.manifest.missing: $manifest_path" >&2; exit 64; }

python - "$manifest_path" <<'PY'
import json
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
obj = json.loads(path.read_text(encoding='utf-8'))
if not isinstance(obj, dict):
    raise SystemExit('verify.manifest.not_object')

required_keys = {'run_id', 'plan_sha256', 'trace_sha256', 'artifact_root', 'created_at'}
missing = sorted(required_keys - set(obj.keys()))
if missing:
    raise SystemExit(f"verify.manifest.missing_keys: {','.join(missing)}")

extra = sorted(set(obj.keys()) - required_keys)
if extra:
    raise SystemExit(f"verify.manifest.unexpected_keys: {','.join(extra)}")

hex64 = re.compile(r'^[0-9a-f]{64}$')
for key in ('plan_sha256', 'trace_sha256'):
    value = obj.get(key, '')
    if not isinstance(value, str) or not hex64.match(value):
        raise SystemExit(f'verify.manifest.bad_hash: {key}')

run_id = obj.get('run_id', '')
if not isinstance(run_id, str) or not re.match(r'^[0-9a-f]{16}$', run_id):
    raise SystemExit('verify.manifest.bad_run_id')

artifact_root = obj.get('artifact_root', '')
if not isinstance(artifact_root, str) or not artifact_root:
    raise SystemExit('verify.manifest.bad_artifact_root')

created = obj.get('created_at', '')
if not isinstance(created, str) or not re.match(r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$', created):
    raise SystemExit('verify.manifest.bad_created_at')

print('MANIFEST_CONTRACT_OK=1')
print(f'MANIFEST_PATH={path}')
PY
