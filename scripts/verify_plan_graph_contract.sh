#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_plan_graph_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.plan_graph.run_dir_missing: $run_dir" >&2; exit 64; }

plan_graph_path=""
for candidate in \
  "$run_dir/plan_graph.mmd" \
  "$run_dir/plan/graph.mmd" \
  "$run_dir/plan/plan_graph.mmd" \
  "$run_dir/graph/plan.mmd"
do
  if [[ -f "$candidate" ]]; then
    plan_graph_path="$candidate"
    break
  fi
done

[[ -n "$plan_graph_path" ]] || { echo "verify.plan_graph.missing: no plan graph file found in $run_dir" >&2; exit 1; }
[[ -s "$plan_graph_path" ]] || { echo "verify.plan_graph.empty: $plan_graph_path" >&2; exit 1; }

readarray -t graph_values < <(python - "$plan_graph_path" <<'PY'
import hashlib
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
raw = path.read_text(encoding='utf-8')
normalized = '\n'.join(line.rstrip() for line in raw.replace('\r\n', '\n').replace('\r', '\n').split('\n')).strip() + '\n'
sha = hashlib.sha256(normalized.encode('utf-8')).hexdigest()

nodes = []
node_re = re.compile(r'^\s*([A-Za-z0-9_:-]+)\s*\[')
for line in normalized.splitlines():
    m = node_re.match(line)
    if m:
        nodes.append(m.group(1))

if nodes:
    first_seen = []
    seen = set()
    for n in nodes:
        if n not in seen:
            seen.add(n)
            first_seen.append(n)
    if first_seen != sorted(first_seen):
        raise SystemExit('verify.plan_graph.node_order_not_deterministic')

print(sha)
print(len(nodes))
PY
)

echo "PLAN_GRAPH_OK=1"
echo "PLAN_GRAPH_PATH=$plan_graph_path"
echo "PLAN_GRAPH_SHA256=${graph_values[0]}"
echo "PLAN_GRAPH_NODES=${graph_values[1]}"
