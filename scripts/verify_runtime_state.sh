#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_runtime_state.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.runtime_state.run_dir_missing: $run_dir" >&2; exit 64; }

runtime_state_path=""
for candidate in \
  "$run_dir/runtime_state.json" \
  "$run_dir/state/runtime_state.json" \
  "$run_dir/runtime/state.json"
do
  if [[ -f "$candidate" ]]; then
    runtime_state_path="$candidate"
    break
  fi
done

[[ -n "$runtime_state_path" ]] || { echo "verify.runtime_state.missing: no runtime state file found in $run_dir" >&2; exit 1; }
[[ -s "$runtime_state_path" ]] || { echo "verify.runtime_state.empty: $runtime_state_path" >&2; exit 1; }

python - "$runtime_state_path" <<'PY'
import json
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
raw = path.read_text(encoding='utf-8')

transient_key = re.compile(r'(timestamp|created_at|updated_at|time)$', re.IGNORECASE)

order_fail = [False]

def hook(pairs):
    keys = [k for k, _ in pairs if isinstance(k, str)]
    if keys != sorted(keys):
        order_fail[0] = True
    return dict(pairs)

obj = json.loads(raw, object_pairs_hook=hook)
if order_fail[0]:
    raise SystemExit('verify.runtime_state.unsorted_keys')

stack = [([], obj)]
while stack:
    path_parts, node = stack.pop()
    if isinstance(node, dict):
        for k, v in node.items():
            if transient_key.search(k):
                dotted = '.'.join(path_parts + [k])
                raise SystemExit(f'verify.runtime_state.transient_field: {dotted}')
            stack.append((path_parts + [k], v))
    elif isinstance(node, list):
        for idx, item in enumerate(node):
            stack.append((path_parts + [str(idx)], item))

print('RUNTIME_STATE_OK=1')
print(f'RUNTIME_STATE_PATH={path}')
PY
