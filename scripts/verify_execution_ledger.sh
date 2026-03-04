#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_execution_ledger.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.ledger.run_dir_missing: $run_dir" >&2; exit 64; }

ledger_path=""
for candidate in \
  "$run_dir/ledger.ndjson" \
  "$run_dir/narration/events.ndjson" \
  "$run_dir/trace/events.ndjson"
do
  if [[ -f "$candidate" ]]; then
    ledger_path="$candidate"
    break
  fi
done

[[ -n "$ledger_path" ]] || { echo "verify.ledger.missing: no ledger candidate found in $run_dir" >&2; exit 1; }
[[ -s "$ledger_path" ]] || { echo "verify.ledger.empty: $ledger_path" >&2; exit 1; }

python - "$ledger_path" <<'PY'
import json
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
ascii_tool = re.compile(r'^[A-Za-z0-9._:-]{1,200}$')

expected_index = 0
total_events = 0
tool_events = 0

with path.open('r', encoding='utf-8') as handle:
    for line_no, raw in enumerate(handle, 1):
        line = raw.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError as ex:
            raise SystemExit(f'verify.ledger.invalid_json: line={line_no};error={ex.msg}')
        if not isinstance(event, dict):
            raise SystemExit(f'verify.ledger.event_not_object: line={line_no}')

        idx = event.get('index')
        if not isinstance(idx, int):
            raise SystemExit(f'verify.ledger.bad_index_type: line={line_no}')
        if idx != expected_index:
            raise SystemExit(f'verify.ledger.non_monotonic_index: line={line_no};expected={expected_index};actual={idx}')
        expected_index += 1

        tool_id = event.get('tool_id', event.get('toolId', ''))
        event_type = str(event.get('type', ''))
        if tool_id or event_type.startswith('tool.'):
            tool_events += 1
            if not isinstance(tool_id, str) or not tool_id.strip() or not ascii_tool.match(tool_id):
                raise SystemExit(f'verify.ledger.bad_tool_id: line={line_no}')

            timestamp = event.get('timestamp', event.get('time', event.get('ts', '')))
            if not isinstance(timestamp, str) or not timestamp.strip():
                raise SystemExit(f'verify.ledger.missing_timestamp: line={line_no}')

            input_hash = event.get('input_hash', event.get('inputHash', event.get('requestHash', '')))
            output_hash = event.get('output_hash', event.get('outputHash', event.get('responseHash', '')))
            if not isinstance(input_hash, str) or not re.match(r'^[0-9a-f]{64}$', input_hash):
                raise SystemExit(f'verify.ledger.bad_input_hash: line={line_no}')
            if not isinstance(output_hash, str) or not re.match(r'^[0-9a-f]{64}$', output_hash):
                raise SystemExit(f'verify.ledger.bad_output_hash: line={line_no}')

        total_events += 1

if total_events == 0:
    raise SystemExit('verify.ledger.no_events')

print('LEDGER_OK=1')
print(f'LEDGER_PATH={path}')
print(f'LEDGER_EVENTS={total_events}')
print(f'LEDGER_TOOL_EVENTS={tool_events}')
PY
