#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_trace_ordering.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
trace_path="$run_dir/trace/events.ndjson"
[[ -f "$trace_path" ]] || { echo "verify.trace_ordering.trace_missing: $trace_path" >&2; exit 64; }

readarray -t out < <(python - "$trace_path" <<'PY'
import json
import pathlib
import sys

trace = pathlib.Path(sys.argv[1])
allowed = [
    'run.started',
    'plan.validated',
    'provider.validated',
    'environment.validated',
    'run.completed',
]
order = {v: i for i, v in enumerate(allowed)}

count = 0
prev_index = -1
prev_order = -1
for line_no, raw in enumerate(trace.read_text(encoding='utf-8').splitlines(), 1):
    line = raw.strip()
    if not line:
        continue
    e = json.loads(line)
    idx = e.get('index')
    if not isinstance(idx, int):
        raise SystemExit(f'verify.trace_ordering.bad_index_type: line={line_no}')
    if idx != prev_index + 1:
        raise SystemExit(f'verify.trace_ordering.non_consecutive_index: line={line_no};expected={prev_index+1};actual={idx}')
    prev_index = idx

    et = e.get('type')
    if et not in order:
        raise SystemExit(f'verify.trace_ordering.unknown_type: line={line_no};type={et}')
    cur_order = order[et]
    if cur_order < prev_order:
        raise SystemExit(f'verify.trace_ordering.out_of_order: line={line_no};type={et}')
    prev_order = cur_order
    count += 1

if count == 0:
    raise SystemExit('verify.trace_ordering.no_events')

print('TRACE_ORDERING_OK=1')
print(f'TRACE_EVENT_COUNT={count}')
PY
)

printf '%s
' "${out[@]}"
