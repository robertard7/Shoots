#!/usr/bin/env bash
set -euo pipefail

summary_file="artifacts/smoke/latest_summary.env"
[[ -f "$summary_file" ]] || { echo "determinism.report.summary_missing: $summary_file" >&2; exit 64; }
# shellcheck disable=SC1091
source "$summary_file"
[[ -n "${RUN_DIR:-}" && -d "$RUN_DIR" ]] || { echo "determinism.report.run_dir_missing" >&2; exit 64; }

report_dir="artifacts/reports"
mkdir -p "$report_dir"
report_md="$report_dir/determinism_report.md"

run_check() {
  local name="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    echo "$name|OK"
  else
    echo "$name|FAIL"
  fi
}

results=()
results+=("$(run_check hash_contract bash scripts/verify_hash_contract.sh "$RUN_DIR")")
results+=("$(run_check trace_contract bash scripts/verify_trace_contract.sh "$RUN_DIR")")
results+=("$(run_check environment_schema bash scripts/verify_environment_schema.sh "$RUN_DIR")")
results+=("$(run_check artifact_bounds bash scripts/verify_artifact_bounds.sh "$RUN_DIR")")
results+=("$(run_check config_contract bash scripts/verify_config_contract.sh)")

status="OK"
for row in "${results[@]}"; do
  IFS='|' read -r _ s <<<"$row"
  if [[ "$s" != "OK" ]]; then
    status="FAIL"
    break
  fi
done

{
  echo "# Determinism Report"
  echo
  echo "RUN_DIR: \\`$RUN_DIR\\`"
  echo
  echo "| Check | Status |"
  echo "|---|---|"
  for row in "${results[@]}"; do
    IFS='|' read -r n s <<<"$row"
    echo "| $n | $s |"
  done
} > "$report_md"

if [[ "$status" != "OK" ]]; then
  echo "determinism.report.failed" >&2
  exit 1
fi

echo "DETERMINISM_REPORT_OK=1"
echo "DETERMINISM_REPORT_PATH=$report_md"
