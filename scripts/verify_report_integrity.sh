#!/usr/bin/env bash
set -euo pipefail

summary_file="artifacts/smoke/latest_summary.env"
[[ -f "$summary_file" ]] || { echo "verify.report_integrity.summary_missing: $summary_file" >&2; exit 64; }

bash scripts/determinism_report.sh >/dev/null
report_path="artifacts/reports/determinism_report.md"
[[ -f "$report_path" ]] || { echo "verify.report_integrity.report_missing" >&2; exit 1; }

norm_hash() {
  sed 's/[[:space:]]\+$//' "$1" | tr -d '\r' | sha256sum | awk '{print $1}'
}

first_hash="$(norm_hash "$report_path")"
bash scripts/determinism_report.sh >/dev/null
second_hash="$(norm_hash "$report_path")"

[[ "$first_hash" == "$second_hash" ]] || { echo "verify.report_integrity.hash_mismatch: $first_hash != $second_hash" >&2; exit 1; }

echo "REPORT_INTEGRITY_OK=1"
echo "REPORT_SHA256=$first_hash"
