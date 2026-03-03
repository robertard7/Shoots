#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C
export TZ=UTC

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

read_json_field() {
  local file="$1"
  local field="$2"
  python - <<'PY' "$file" "$field"
import json,sys
obj=json.load(open(sys.argv[1]))
field=sys.argv[2]
value=obj.get(field, '')
print(value if value is not None else '')
PY
}

resolve_latest_run() {
  local candidate=""
  candidate="$(find .state/runs -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -z "$candidate" ]]; then
    candidate="$(find artifacts/builder_loop -type d -path '*/run/*' ! -path '*/run/*/*' -print 2>/dev/null | sort | tail -n 1 || true)"
  fi
  printf '%s' "$candidate"
}

run_dir="${1:-$(resolve_latest_run)}"
if [[ -z "$run_dir" || ! -d "$run_dir" ]]; then
  echo "verify.plan_hash_chain.run_missing: no run directory found"
  exit 0
fi

run_hashes="$run_dir/hashes.json"
plan_hashes="$run_dir/plan/hashes.json"
retrieval_hashes="$run_dir/retrieval/hashes.json"
synthesis_result="$run_dir/plan_synthesis/result.json"

[[ -f "$run_hashes" ]] || fail "verify.plan_hash_chain.hashes_missing" "missing $run_hashes"
[[ -f "$plan_hashes" ]] || fail "verify.plan_hash_chain.plan_missing" "missing $plan_hashes"
[[ -f "$retrieval_hashes" ]] || fail "verify.plan_hash_chain.retrieval_missing" "missing $retrieval_hashes"

run_plan_hash="$(read_json_field "$run_hashes" planHash)"
run_retrieval_hash="$(read_json_field "$run_hashes" retrievalHash)"
plan_plan_hash="$(read_json_field "$plan_hashes" planHash)"
plan_request_hash="$(read_json_field "$plan_hashes" requestHash)"
plan_retrieval_hash="$(read_json_field "$plan_hashes" retrievalHash)"
retrieval_request_hash="$(read_json_field "$retrieval_hashes" requestHash)"
retrieval_hash="$(read_json_field "$retrieval_hashes" retrievalHash)"

[[ -n "$run_plan_hash" ]] || fail "verify.plan_hash_chain.run_plan_hash_missing" "planHash missing in $run_hashes"
[[ -n "$run_retrieval_hash" ]] || fail "verify.plan_hash_chain.run_retrieval_hash_missing" "retrievalHash missing in $run_hashes"
[[ -n "$plan_plan_hash" ]] || fail "verify.plan_hash_chain.plan_plan_hash_missing" "planHash missing in $plan_hashes"
[[ -n "$plan_request_hash" ]] || fail "verify.plan_hash_chain.plan_request_hash_missing" "requestHash missing in $plan_hashes"
[[ -n "$plan_retrieval_hash" ]] || fail "verify.plan_hash_chain.plan_retrieval_hash_missing" "retrievalHash missing in $plan_hashes"
[[ -n "$retrieval_request_hash" ]] || fail "verify.plan_hash_chain.retrieval_request_hash_missing" "requestHash missing in $retrieval_hashes"
[[ -n "$retrieval_hash" ]] || fail "verify.plan_hash_chain.retrieval_hash_missing" "retrievalHash missing in $retrieval_hashes"

if [[ "$plan_retrieval_hash" != "$retrieval_hash" ]]; then
  fail "verify.plan_hash_chain.retrieval_mismatch" "plan retrievalHash != retrieval hash"
fi

if [[ "$run_retrieval_hash" != "$retrieval_hash" ]]; then
  fail "verify.plan_hash_chain.run_retrieval_mismatch" "run retrievalHash != retrieval hash"
fi

if [[ -f "$synthesis_result" ]]; then
  synthesis_plan_hash="$(read_json_field "$synthesis_result" planHash)"
  synthesis_request_hash="$(read_json_field "$synthesis_result" requestHash)"

  [[ -n "$synthesis_plan_hash" ]] || fail "verify.plan_hash_chain.synthesis_plan_hash_missing" "planHash missing in $synthesis_result"
  [[ -n "$synthesis_request_hash" ]] || fail "verify.plan_hash_chain.synthesis_request_hash_missing" "requestHash missing in $synthesis_result"

  if [[ "$synthesis_plan_hash" != "$plan_plan_hash" ]]; then
    fail "verify.plan_hash_chain.synthesis_plan_mismatch" "synthesis planHash != plan/hashes planHash"
  fi

  if [[ "$synthesis_request_hash" != "$plan_request_hash" ]]; then
    fail "verify.plan_hash_chain.synthesis_request_mismatch" "synthesis requestHash != plan/hashes requestHash"
  fi
fi

echo "verify.plan_hash_chain.ok: $run_dir"
