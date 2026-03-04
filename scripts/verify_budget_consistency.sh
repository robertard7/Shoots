#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

python - <<'PY'
import json, pathlib, sys
root=pathlib.Path('.')
# retrieval golden consistency
q=json.loads((root/'etc/fixtures/retrieval_golden/query.json').read_text())
stats=json.loads((root/'etc/fixtures/retrieval_golden/expected/stats.json').read_text())
if stats.get('bytesOut',0) > q.get('maxTotalBytes',0):
    print('verify.budget_consistency.retrieval_bytes_exceeded')
    raise SystemExit(1)
if stats.get('linesOut',0) > q.get('maxLines',0):
    print('verify.budget_consistency.retrieval_lines_exceeded')
    raise SystemExit(1)
if stats.get('filesOut',0) > q.get('maxFiles',0):
    print('verify.budget_consistency.retrieval_files_exceeded')
    raise SystemExit(1)

# synthesis fixture consistency
plan=json.loads((root/'etc/fixtures/synthesis_limits/project/plan/plan.json').read_text())
for step in plan.get('steps',[]):
    if step.get('kind')=='synthesize_plan.v1':
        args=step.get('args',{})
        for key in ('maxSteps','maxArgsBytes','maxTotalPlanBytes'):
            if int(args.get(key,0)) <= 0:
                print(f'verify.budget_consistency.synthesis_invalid_{key}')
                raise SystemExit(1)

# context pack first30 size sanity
pack=(root/'etc/fixtures/retrieval_golden/expected/context_pack_first30.txt').read_bytes()
if len(pack) == 0:
    print('verify.budget_consistency.context_pack_empty')
    raise SystemExit(1)

print('verify.budget_consistency.ok')
PY
