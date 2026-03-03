#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

bash scripts/find_stubs.sh >/dev/null
bash scripts/triage_stubs.sh >/dev/null

bucket_file="artifacts/stubs/bucket-1.ndjson"
count=0
if [[ -f "$bucket_file" ]]; then
  count="$(wc -l < "$bucket_file" | tr -d ' ')"
fi

if [[ "$count" -gt 0 ]]; then
  echo "blocking stubs detected: $count"
  echo "--- top blocking stubs ---"
  head -n 50 "$bucket_file"
  exit 1
fi

echo "blocking stubs detected: 0"
exit 0
