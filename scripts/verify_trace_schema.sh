#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_trace_schema.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
trace_path="$run_dir/trace/events.ndjson"
[[ -d "$run_dir" ]] || { echo "verify.trace.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$trace_path" ]] || { echo "verify.trace.missing: $trace_path" >&2; exit 64; }
[[ -s "$trace_path" ]] || { echo "verify.trace.empty: $trace_path" >&2; exit 1; }

python - "$trace_path" <<'PY'
import json, pathlib, sys

trace = pathlib.Path(sys.argv[1])
count = 0
required = ('index', 'type')
with trace.open('r', encoding='utf-8') as fh:
    for line_no, raw in enumerate(fh, 1):
        line = raw.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError as ex:
            raise SystemExit(f"verify.trace.invalid_json: line={line_no};error={ex.msg}")

        for key in required:
            if key not in obj:
                raise SystemExit(f"verify.trace.missing_field: line={line_no};field={key}")

        if not isinstance(obj.get('type'), str) or not obj['type']:
            raise SystemExit(f"verify.trace.bad_type: line={line_no}")
        count += 1

if count == 0:
    raise SystemExit('verify.trace.no_events')

print('TRACE_SCHEMA_OK=1')
print(f'TRACE_PATH={trace}')
print('TRACE_KIND=ndjson')
print(f'TRACE_EVENT_COUNT={count}')
PY
