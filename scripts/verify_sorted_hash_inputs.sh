#!/usr/bin/env bash
set -euo pipefail

scripts=(
  scripts/verify_fixture_integrity.sh
  scripts/update_fixture_integrity.sh
)

missing=()
for script in "${scripts[@]}"; do
  [[ -f "$script" ]] || { missing+=("$script"); continue; }
  if ! rg -q "sorted\(" "$script"; then
    missing+=("$script")
  fi
done

if (( ${#missing[@]} > 0 )); then
  echo "SORTED_HASH_INPUTS_OK=0"
  echo "verify.sorted_hash_inputs.missing_sort_markers: ${missing[*]}" >&2
  exit 1
fi

echo "SORTED_HASH_INPUTS_OK=1"
echo "SORTED_HASH_INPUT_SCRIPTS=${scripts[*]}"
