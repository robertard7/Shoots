#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_log_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.log_contract.run_dir_missing: $run_dir" >&2; exit 64; }

summary_path="$run_dir/run_summary.md"
trace_path="$run_dir/trace/events.ndjson"

[[ -f "$summary_path" ]] || { echo "verify.log_contract.summary_missing: $summary_path" >&2; exit 1; }
[[ -s "$summary_path" ]] || { echo "verify.log_contract.summary_empty: $summary_path" >&2; exit 1; }
[[ -f "$trace_path" ]] || { echo "verify.log_contract.trace_missing: $trace_path" >&2; exit 1; }
[[ -s "$trace_path" ]] || { echo "verify.log_contract.trace_empty: $trace_path" >&2; exit 1; }

python - "$summary_path" "$trace_path" <<'PY'
import json
import pathlib
import re
import sys

summary = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8').splitlines()
trace = pathlib.Path(sys.argv[2])

header_ok = any(line.startswith('# Run Summary') or line.startswith('## Run Summary') for line in summary)
if not header_ok:
    raise SystemExit('verify.log_contract.summary_header_missing')

line_re = re.compile(r'^[A-Za-z0-9 _\-:`/().,]+$')
for idx, line in enumerate(summary, 1):
    if line and not line_re.match(line):
        raise SystemExit(f'verify.log_contract.summary_line_invalid: line={idx}')

count = 0
for line_no, raw in enumerate(trace.read_text(encoding='utf-8').splitlines(), 1):
    line = raw.strip()
    if not line:
        continue
    event = json.loads(line)
    if not isinstance(event, dict):
        raise SystemExit(f'verify.log_contract.trace_event_not_object: line={line_no}')
    if 'type' not in event:
        raise SystemExit(f'verify.log_contract.trace_missing_type: line={line_no}')
    count += 1

if count == 0:
    raise SystemExit('verify.log_contract.trace_no_events')

print('LOG_CONTRACT_OK=1')
print(f'LOG_SUMMARY_PATH={sys.argv[1]}')
print(f'LOG_TRACE_PATH={sys.argv[2]}')
print(f'LOG_EVENT_COUNT={count}')
PY
