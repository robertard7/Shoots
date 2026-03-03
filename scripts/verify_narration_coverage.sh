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

run_dir="${1:-$(resolve_latest_run)}"
if [[ -z "$run_dir" || ! -d "$run_dir" ]]; then
  echo "verify.narration.run_missing: no run directory found"
  exit 0
fi

narration="$run_dir/narration/events.ndjson"
[[ -f "$narration" ]] || fail "verify.narration.missing" "missing $narration"

python - "$narration" <<'PY'
import json, pathlib, sys
p = pathlib.Path(sys.argv[1])
lines = [json.loads(x) for x in p.read_text().splitlines() if x.strip()]
codes = [x.get('code') for x in lines]
run_required = [
    'startup.begin','startup.end',
    'retrieval.begin','retrieval.result',
    'builder.synthesis.start','builder.synthesis.end',
    'builder.execute.start','builder.execute.end'
]
replay_required = ['replay.begin', 'replay.inputs', 'replay.hash.compare', 'replay.result']

if any(code in codes for code in run_required):
    missing = [c for c in run_required if c not in codes]
    if missing:
        print(f"verify.narration.required_missing: {','.join(missing)}")
        raise SystemExit(1)

    step_begin = sum(1 for c in codes if c == 'execute.step.begin')
    step_end = sum(1 for c in codes if c == 'execute.step.end')
    if step_begin == 0 or step_begin != step_end:
        print(f"verify.narration.step_mismatch: begin={step_begin} end={step_end}")
        raise SystemExit(1)
else:
    missing = [c for c in replay_required if c not in codes]
    if missing:
        print(f"verify.narration.replay_required_missing: {','.join(missing)}")
        raise SystemExit(1)

for item in lines:
    code = item.get('code') or ''
    if code.endswith('.failed'):
        if item.get('errorCode') in (None, ''):
            print(f"verify.narration.failure_missing_error_code: {code}")
            raise SystemExit(1)

print('verify.narration.ok')
PY
