#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fixture="etc/fixtures/retrieval_quality/basic.json"
project="$(python - <<'PY' "$fixture"
import json,sys
j=json.load(open(sys.argv[1]))
print(j['project'])
PY
)"
required_top_n="$(python - <<'PY' "$fixture"
import json,sys
j=json.load(open(sys.argv[1]))
print(j['requiredTopN'])
PY
)"
mapfile -t required_paths < <(python - <<'PY' "$fixture"
import json,sys
j=json.load(open(sys.argv[1]))
for p in j['requiredPaths']:
    print(p)
PY
)

out="artifacts/retrieval_quality"
mkdir -p "$out"
dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project "$project" --out "$out" >/dev/null
run_dir="$(find "$out" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1)"
hits="$run_dir/retrieval/hits.ndjson"

python - <<'PY' "$hits" "$required_top_n" "${required_paths[@]}"
import json,sys
hits=[json.loads(x) for x in open(sys.argv[1],encoding='utf-8').read().splitlines() if x.strip()]
maxn=int(sys.argv[2])
required=sys.argv[3:]
top={h.get('path','') for h in hits[:maxn]}
if not hits:
    print('verify.retrieval_quality.no_hits')
    raise SystemExit(0)
missing=[p for p in required if p not in top]
if missing:
    print('verify.retrieval_quality.missing_required: '+','.join(missing))
    raise SystemExit(1)
print('verify.retrieval_quality.ok')
PY
