#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

resolve_latest_run() {
  local candidate=""
  candidate="$(find .state/runs -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -z "$candidate" ]]; then
    candidate="$(find artifacts/builder_loop -type d -path '*/run/*' ! -path '*/run/*/*' -print 2>/dev/null | sort | tail -n 1 || true)"
  fi
  printf '%s' "$candidate"
}

require_run=0
if [[ "${1:-}" == "--require-run" ]]; then
  require_run=1
  shift
fi

run_dir="${1:-$(resolve_latest_run)}"
if [[ -z "$run_dir" || ! -d "$run_dir" ]]; then
  if (( require_run == 1 )); then
    fail "verify.plan_evidence.run_missing" "no run directory found"
  fi
  echo "verify.plan_evidence.run_missing: no run directory found"
  exit 0
fi

hits="$run_dir/retrieval/hits.ndjson"
evidence="$run_dir/plan_synthesis/evidence.ndjson"
plan="$run_dir/plan/plan.json"

[[ -f "$hits" ]] || fail "verify.plan_evidence.hits_missing" "missing $hits"
[[ -f "$evidence" ]] || fail "verify.plan_evidence.evidence_missing" "missing $evidence"
[[ -f "$plan" ]] || fail "verify.plan_evidence.plan_missing" "missing $plan"

python - "$hits" "$evidence" "$plan" <<'PY'
import json,sys
hits=[json.loads(x) for x in open(sys.argv[1],encoding='utf-8').read().splitlines() if x.strip()]
ev=[json.loads(x) for x in open(sys.argv[2],encoding='utf-8').read().splitlines() if x.strip()]
plan=json.load(open(sys.argv[3],encoding='utf-8'))
step_ids={s.get('stepId','') for s in plan.get('steps',[])}
hit_ids={h.get('hitId','') for h in hits}
steps=plan.get('steps',[])
if not ev:
    if steps:
        print('verify.plan_evidence.empty: no evidence rows')
        raise SystemExit(1)
    print('verify.plan_evidence.ok: empty evidence allowed for empty plan')
    raise SystemExit(0)
for row in ev:
    if row.get('stepId','') not in step_ids:
        print(f"verify.plan_evidence.step_missing: {row.get('stepId','')}")
        raise SystemExit(1)
    if row.get('hitId','') not in hit_ids:
        print(f"verify.plan_evidence.hit_missing: {row.get('hitId','')}")
        raise SystemExit(1)
print('verify.plan_evidence.ok')
PY
