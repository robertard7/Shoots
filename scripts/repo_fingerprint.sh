#!/usr/bin/env bash
set -euo pipefail

commit_sha="$(git rev-parse HEAD)"

catalog_sha="$(bash scripts/verify_tool_catalog_contract.sh | awk -F= '/^TOOL_CATALOG_SHA256=/{print $2}')"
fixture_sha="$(awk '{print $1}' etc/fixtures/builder_smoke/fixture.sha256)"

pipeline_hash="$(python - <<'PY'
import hashlib
import pathlib

files = [
    'scripts/validate_determinism.sh',
    'scripts/smoke_runner.sh',
    'scripts/replay_runner.sh',
    'scripts/verify_hash_contract.sh',
    'scripts/verify_fixture_integrity.sh',
    'scripts/verify_trace_schema.sh',
    'scripts/verify_trace_contract.sh',
    'scripts/verify_manifest_contract.sh',
    'scripts/verify_environment_schema.sh',
    'scripts/verify_trace_correlations.sh',
    'scripts/verify_execution_ledger.sh',
    'scripts/verify_sorted_hash_inputs.sh',
    'scripts/verify_artifact_bounds.sh',
    'scripts/verify_smoke_artifacts.sh',
    'scripts/verify_scripts_portability.sh',
    'scripts/verify_tool_catalog_contract.sh',
]
h = hashlib.sha256()
for rel in files:
    p = pathlib.Path(rel)
    h.update(rel.encode('utf-8'))
    h.update(b'\0')
    h.update(p.read_bytes())
    h.update(b'\0')
print(h.hexdigest())
PY
)"

repo_fingerprint="$(python - "$commit_sha" "$catalog_sha" "$fixture_sha" "$pipeline_hash" <<'PY'
import hashlib
import sys

parts = [sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]]
h = hashlib.sha256()
for part in parts:
    h.update(part.encode('utf-8'))
    h.update(b'\0')
print(h.hexdigest())
PY
)"

echo "REPO_FINGERPRINT=$repo_fingerprint"
echo "GIT_COMMIT=$commit_sha"
echo "TOOL_CATALOG_SHA256=$catalog_sha"
echo "FIXTURE_SHA256=$fixture_sha"
echo "DETERMINISM_PIPELINE_SHA256=$pipeline_hash"
