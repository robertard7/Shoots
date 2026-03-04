#!/usr/bin/env bash
set -euo pipefail

first_log="$(mktemp)"
second_log="$(mktemp)"
cleanup() { rm -f "$first_log" "$second_log"; }
trap cleanup EXIT

bash scripts/validate_determinism.sh --skip-backends >"$first_log"
fp1="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"
run1="$(awk -F= '/^RUN_DIR=/{print $2}' "$first_log" | tail -n1)"

SHOOTS_FORCE_PARALLEL=1 bash scripts/validate_determinism.sh --skip-backends >"$second_log"
fp2="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"
run2="$(awk -F= '/^RUN_DIR=/{print $2}' "$second_log" | tail -n1)"

[[ -n "$fp1" && -n "$fp2" ]] || { echo "probe.parallel.fp_missing" >&2; exit 1; }
[[ "$fp1" == "$fp2" ]] || { echo "probe.parallel.fp_mismatch: $fp1 != $fp2" >&2; exit 1; }

if [[ -n "$run1" ]]; then bash scripts/verify_trace_ordering.sh "$run1" >/dev/null; fi
if [[ -n "$run2" ]]; then bash scripts/verify_trace_ordering.sh "$run2" >/dev/null; fi

echo "PARALLEL_DETERMINISM_OK=1"
