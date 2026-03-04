#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

mapfile -t runs < <(find artifacts/builder_loop -type d -path '*/run/*' ! -path '*/run/*/*' -print 2>/dev/null | sort)
if (( ${#runs[@]} < 2 )); then
  echo "verify.context_pack.run_missing: need at least two runs"
  exit 0
fi

prev="${runs[-2]}"
curr="${runs[-1]}"

cmp_file() {
  local code="$1"
  local rel="$2"
  local a="$prev/$rel"
  local b="$curr/$rel"
  [[ -f "$a" && -f "$b" ]] || fail "${code}.missing" "missing $rel in compared runs"
  cmp -s "$a" "$b" || fail "$code" "$rel diverged between runs"
}

cmp_file "verify.context_pack.diverged" "retrieval/context_pack.txt"
cmp_file "verify.retrieval_hits.diverged" "retrieval/hits.ndjson"
cmp_file "verify.retrieval_scoring.diverged" "retrieval/scoring.ndjson"

echo "verify.context_pack_determinism.ok"
