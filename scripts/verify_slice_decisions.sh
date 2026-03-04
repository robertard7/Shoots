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
    fail "verify.slice_decisions.run_missing" "no run directory found"
  fi
  echo "verify.slice_decisions.run_missing: no run directory found"
  exit 0
fi

decisions="$run_dir/slice/decisions.ndjson"
[[ -f "$decisions" ]] || fail "verify.slice_decisions.missing" "missing $decisions"

hits="$run_dir/retrieval/hits.ndjson"
python - "$decisions" "$hits" <<'PY'
import json,sys
rows=[json.loads(x) for x in open(sys.argv[1],encoding='utf-8').read().splitlines() if x.strip()]
hits=[json.loads(x) for x in open(sys.argv[2],encoding='utf-8').read().splitlines() if x.strip()] if len(sys.argv)>2 and __import__('os').path.exists(sys.argv[2]) else []
paths=[r.get('path','') for r in rows]
if paths!=sorted(paths):
    print('verify.slice_decisions.unsorted: decisions are not path sorted')
    raise SystemExit(1)
selected=[r for r in rows if r.get('includeMatch') and not r.get('excludeMatch') and (r.get('rejectedReason') in ('',None))]
if hits and not selected:
    print('verify.slice_decisions.selected_missing: no selected files in decisions')
    raise SystemExit(1)
selected_paths={r.get('path','') for r in selected}
for hit in hits:
    hp=hit.get('path','')
    if hp and hp not in selected_paths:
        print(f'verify.slice_decisions.hit_not_selected: {hp}')
        raise SystemExit(1)
print('verify.slice_decisions.ok')
PY
