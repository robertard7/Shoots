#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/validate_determinism.sh [options]

Options are passed through to smoke_backends/smoke_runner:
  --skip-backends
  --ollama <url>
  --qdrant <url>
  --timeout-secs <n>
USAGE
}

args=("$@")
for arg in "${args[@]}"; do
  case "$arg" in
    -h|--help)
      usage
      exit 0
      ;;
  esac
done

bash tools/codex/restore.sh
bash scripts/smoke_runner.sh "${args[@]}"
source artifacts/smoke/latest_summary.env
bash scripts/verify_hash_contract.sh "$RUN_DIR"
bash scripts/verify_fixture_integrity.sh
bash scripts/verify_trace_schema.sh "$RUN_DIR"
bash scripts/verify_trace_contract.sh "$RUN_DIR"
bash scripts/replay_runner.sh "$RUN_DIR"
bash scripts/inspect_run.sh "$RUN_DIR"

echo "DETERMINISM_OK=1"
echo "RUN_DIR=$RUN_DIR"
echo "RUN_ID=$RUN_ID"
echo "HASHES_SHA256=$HASHES_SHA256"
