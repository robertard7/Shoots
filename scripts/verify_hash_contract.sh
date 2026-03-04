#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_hash_contract.sh <RUN_DIR>

Validates the deterministic hash contract for RUN_DIR/hashes.json.
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
hashes_file="$run_dir/hashes.json"
[[ -d "$run_dir" ]] || { echo "verify.hash_contract.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$hashes_file" ]] || { echo "verify.hash_contract.hashes_missing: $hashes_file" >&2; exit 64; }

python - "$hashes_file" <<'PY'
import json, pathlib, re, sys

path = pathlib.Path(sys.argv[1])
obj = json.loads(path.read_text())

# Freeze known schema keys from the runner.
allowed_keys = {
    'runId',
    'planHash',
    'providerHash',
    'envHash',
    'traceHash',
    'retrievalHash',
    'outputManifestHash',
}

required_keys = {
    'runId',
    'planHash',
    'traceHash',
    'outputManifestHash',
}

missing = sorted(required_keys - set(obj.keys()))
if missing:
    raise SystemExit(f"verify.hash_contract.missing_keys: {','.join(missing)}")

extra = sorted(set(obj.keys()) - allowed_keys)
if extra:
    raise SystemExit(f"verify.hash_contract.unexpected_keys: {','.join(extra)}")

# Semantic guard aliases requested for contract freeze.
semantic = {
    'plan_sha256': obj.get('planHash', ''),
    'trace_sha256': obj.get('traceHash', ''),
    'manifest_sha256': obj.get('outputManifestHash', ''),
    'artifacts_sha256': obj.get('outputManifestHash', ''),
}
for name, value in semantic.items():
    if not value:
        raise SystemExit(f"verify.hash_contract.missing_semantic_value: {name}")

hex64 = re.compile(r'^[0-9a-f]{64}$')
for key in ('planHash', 'providerHash', 'envHash', 'traceHash', 'outputManifestHash'):
    value = obj.get(key, '')
    if value and not hex64.match(value):
        raise SystemExit(f"verify.hash_contract.bad_hash_format: {key}")

run_id = obj.get('runId', '')
if run_id and not re.match(r'^[0-9a-f]{16}$', run_id):
    raise SystemExit('verify.hash_contract.bad_run_id_format')

print('VERIFY_HASH_CONTRACT_OK=1')
print(f"HASHES_FILE={path}")
print(f"PLAN_SHA256={semantic['plan_sha256']}")
print(f"TRACE_SHA256={semantic['trace_sha256']}")
print(f"MANIFEST_SHA256={semantic['manifest_sha256']}")
print(f"ARTIFACTS_SHA256={semantic['artifacts_sha256']}")
PY
