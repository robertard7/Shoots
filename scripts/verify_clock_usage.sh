#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_clock_usage.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.clock.run_dir_missing: $run_dir" >&2; exit 64; }
[[ -f "$run_dir/hashes.json" ]] || { echo "verify.clock.hashes_missing: $run_dir/hashes.json" >&2; exit 64; }

readarray -t clock_values < <(python - "$run_dir" <<'PY'
import json
import pathlib
import re
import sys

run_dir = pathlib.Path(sys.argv[1])
hashes = json.loads((run_dir / 'hashes.json').read_text(encoding='utf-8'))
trace_path = run_dir / 'trace' / 'events.ndjson'
manifest_path = run_dir / 'manifest.json'

if not trace_path.exists():
    raise SystemExit(f'verify.clock.trace_missing: {trace_path}')
if not manifest_path.exists():
    raise SystemExit(f'verify.clock.manifest_missing: {manifest_path}')

if not hashes.get('traceHash'):
    raise SystemExit('verify.clock.trace_hash_missing')

stamp_key = re.compile(r'(timestamp|created_at|updated_at|time)$', re.IGNORECASE)
found = []
for path in (trace_path, manifest_path, run_dir / 'environment.json'):
    if not path.exists():
        continue
    data = json.loads(path.read_text(encoding='utf-8')) if path.suffix == '.json' else None
    if path.name.endswith('.ndjson'):
        for line in path.read_text(encoding='utf-8').splitlines():
            line = line.strip()
            if not line:
                continue
            evt = json.loads(line)
            if isinstance(evt, dict):
                for key in evt.keys():
                    if stamp_key.search(str(key)):
                        found.append(f'{path.name}:{key}')
    elif isinstance(data, dict):
        stack = [([], data)]
        while stack:
            pref, obj = stack.pop()
            if isinstance(obj, dict):
                for k, v in obj.items():
                    if stamp_key.search(str(k)):
                        found.append(f"{path.name}:{'.'.join(pref+[str(k)])}")
                    stack.append((pref + [str(k)], v))
            elif isinstance(obj, list):
                for i, v in enumerate(obj):
                    stack.append((pref + [str(i)], v))

for key in hashes.keys():
    if stamp_key.search(str(key)):
        raise SystemExit(f'verify.clock.timestamp_in_hash_contract: {key}')

print(','.join(sorted(set(found))) if found else 'none')
PY
)

echo "CLOCK_CONTRACT_OK=1"
echo "TIMESTAMP_FIELDS=${clock_values[0]}"
