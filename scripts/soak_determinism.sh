#!/usr/bin/env bash
set -euo pipefail

runs="${1:-10}"
[[ "$runs" =~ ^[0-9]+$ ]] || { echo "soak.determinism.bad_run_count: $runs" >&2; exit 64; }

hashes_file="$(mktemp)"
cleanup() {
  rm -f "$hashes_file"
}
trap cleanup EXIT

for ((i=1; i<=runs; i++)); do
  bash scripts/validate_determinism.sh --skip-backends >/dev/null
  fp="$(bash scripts/repo_fingerprint.sh | awk -F= '/^REPO_FINGERPRINT=/{print $2}')"
  if [[ -z "$fp" ]]; then
    echo "soak.determinism.missing_fingerprint: run=$i" >&2
    exit 1
  fi
  echo "$fp" >> "$hashes_file"
done

unique_count="$(sort -u "$hashes_file" | wc -l | tr -d ' ')"
if [[ "$unique_count" != "1" ]]; then
  echo "soak.determinism.non_deterministic: unique_hash_count=$unique_count" >&2
  exit 1
fi

echo "SOAK_DETERMINISM_OK=1"
echo "RUN_COUNT=$runs"
echo "UNIQUE_HASH_COUNT=$unique_count"
