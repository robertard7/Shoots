#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_trace_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
trace_path="$run_dir/trace/events.ndjson"
[[ -d "$run_dir" ]] || { echo "verify.trace_contract.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$trace_path" ]] || { echo "verify.trace_contract.trace_missing: $trace_path" >&2; exit 64; }

python - "$trace_path" <<'PY'
import json
import pathlib
import re
import sys

trace = pathlib.Path(sys.argv[1])
allowed_types = {
    'run.started': {'index', 'type', 'runId', 'scenario'},
    'plan.validated': {'index', 'type', 'planHash'},
    'provider.validated': {'index', 'type', 'providerHash'},
    'environment.validated': {'index', 'type', 'envHash'},
    'run.completed': {'index', 'type', 'status'},
}

ascii_type = re.compile(r'^[A-Za-z0-9._:-]{1,64}$')
seen_types = set()
expected_index = 0
count = 0

with trace.open('r', encoding='utf-8') as fh:
    for line_no, raw in enumerate(fh, 1):
        line = raw.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError as ex:
            raise SystemExit(f'verify.trace_contract.invalid_json: line={line_no};error={ex.msg}')

        if not isinstance(event, dict):
            raise SystemExit(f'verify.trace_contract.event_not_object: line={line_no}')

        index = event.get('index')
        if not isinstance(index, int):
            raise SystemExit(f'verify.trace_contract.bad_index_type: line={line_no}')
        if index != expected_index:
            raise SystemExit(f'verify.trace_contract.non_monotonic_index: line={line_no};expected={expected_index};actual={index}')
        expected_index += 1

        event_type = event.get('type')
        if not isinstance(event_type, str) or not event_type:
            raise SystemExit(f'verify.trace_contract.bad_type: line={line_no}')
        if not ascii_type.match(event_type):
            raise SystemExit(f'verify.trace_contract.type_sanity_failed: line={line_no};type={event_type}')

        if event_type not in allowed_types:
            raise SystemExit(f'verify.trace_contract.unexpected_type: line={line_no};type={event_type}')

        missing = sorted(field for field in allowed_types[event_type] if field not in event)
        if missing:
            raise SystemExit(f"verify.trace_contract.missing_fields: line={line_no};type={event_type};fields={','.join(missing)}")

        seen_types.add(event_type)
        count += 1

if count == 0:
    raise SystemExit('verify.trace_contract.no_events')

print('TRACE_CONTRACT_OK=1')
print(f'TRACE_PATH={trace}')
print('TRACE_KIND=ndjson')
print(f'TRACE_EVENT_COUNT={count}')
print(f"TRACE_EVENT_TYPES={','.join(sorted(seen_types))}")
PY
