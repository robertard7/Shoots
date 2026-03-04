#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

for f in artifacts/smoke/version.txt artifacts/smoke/command.txt artifacts/smoke/env.txt; do
  [[ -s "$f" ]] || fail "verify.smoke_stamp.missing" "missing or empty $f"
done

echo "verify.smoke_stamp.ok"
