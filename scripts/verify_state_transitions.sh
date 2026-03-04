#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_state_transitions.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
trace_path="$run_dir/trace/events.ndjson"
[[ -f "$trace_path" ]] || { echo "verify.transitions.trace_missing: $trace_path" >&2; exit 64; }

readarray -t values < <(python - "$trace_path" <<'PY'
import json
import pathlib
import sys

trace = pathlib.Path(sys.argv[1])
allowed = {
    None: {'run.started'},
    'run.started': {'plan.validated'},
    'plan.validated': {'provider.validated'},
    'provider.validated': {'environment.validated'},
    'environment.validated': {'run.completed'},
    'run.completed': set(),
}

prev = None
count = 0
for line_no, raw in enumerate(trace.read_text(encoding='utf-8').splitlines(), 1):
    line = raw.strip()
    if not line:
        continue
    event = json.loads(line)
    event_type = event.get('type')
    if event_type not in allowed:
        raise SystemExit(f'verify.transitions.unknown_type: line={line_no};type={event_type}')
    next_allowed = allowed.get(prev, set())
    if event_type not in next_allowed:
        raise SystemExit(f'verify.transitions.invalid_transition: line={line_no};from={prev};to={event_type}')
    prev = event_type
    count += 1

if prev != 'run.completed':
    raise SystemExit('verify.transitions.not_completed')

print(count)
PY
)

echo "STATE_TRANSITIONS_OK=1"
echo "TRANSITION_COUNT=${values[0]}"
