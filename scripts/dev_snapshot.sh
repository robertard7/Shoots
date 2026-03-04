#!/usr/bin/env bash
set -euo pipefail

commit_sha="$(git rev-parse HEAD)"
tool_hash="$(bash scripts/verify_tool_catalog_contract.sh | awk -F= '/^TOOL_CATALOG_SHA256=/{print $2}')"
repo_fingerprint="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"

summary_file="artifacts/smoke/latest_summary.env"
run_dir=""
run_id=""
hashes_sha=""
if [[ -f "$summary_file" ]]; then
  # shellcheck disable=SC1090
  source "$summary_file"
  run_dir="${RUN_DIR:-}"
  run_id="${RUN_ID:-}"
  hashes_sha="${HASHES_SHA256:-}"
fi

backend_status="UNKNOWN"
if bash scripts/smoke_ui_backends.sh --skip-qdrant >/tmp/dev_snapshot_backend.out 2>/dev/null; then
  backend_status="OK"
else
  backend_status="DEGRADED"
fi

echo "DEV_SNAPSHOT_OK=1"
echo "COMMIT_SHA=$commit_sha"
echo "REPO_FINGERPRINT=$repo_fingerprint"
echo "TOOL_CATALOG_SHA256=$tool_hash"
echo "LAST_RUN_DIR=${run_dir:-<none>}"
echo "LAST_RUN_ID=${run_id:-<none>}"
echo "LAST_HASHES_SHA256=${hashes_sha:-<none>}"
echo "BACKEND_STATUS=$backend_status"
