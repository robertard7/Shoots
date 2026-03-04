#!/usr/bin/env bash
set -euo pipefail

if [[ "${GITHUB_ACTIONS:-}" == "true" || "${CI:-}" == "true" ]]; then
  echo "refresh.retrieval_golden.refused: local-only workflow" >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fixture_root="etc/fixtures/retrieval_golden"
query_file="$fixture_root/query.json"
expected_dir="$fixture_root/expected"
work="artifacts/retrieval_golden_refresh"

before_hashes="$work/before.hashes"
after_hashes="$work/after.hashes"
mkdir -p "$work"

for f in "$expected_dir/context_pack_first30.txt" "$expected_dir/top_hits.ndjson" "$expected_dir/stats.json"; do
  if [[ -f "$f" ]]; then
    sha256sum "$f"
  else
    echo "missing  $f"
  fi
done | sort > "$before_hashes"

python - "$query_file" "$work/project" <<'PY'
import json,sys,pathlib,shutil
q=json.loads(pathlib.Path(sys.argv[1]).read_text())
project=pathlib.Path(q['project'])
out=pathlib.Path(sys.argv[2])
if out.exists(): shutil.rmtree(out)
shutil.copytree(project,out)
plan_path=out/'plan/plan.json'
plan=json.loads(plan_path.read_text())
for step in plan.get('steps',[]):
    if step.get('kind')=='retrieve_context.v1':
        args=step.setdefault('args',{})
        args['queryText']=q['queryText']
        args['includeGlobs']=q.get('includeGlobs',[])
        args['excludeGlobs']=q.get('excludeGlobs',[])
        args['maxFiles']=q.get('maxFiles',args.get('maxFiles',6))
        args['maxTotalBytes']=q.get('maxTotalBytes',args.get('maxTotalBytes',65536))
        args['maxFileBytes']=q.get('maxFileBytes',args.get('maxFileBytes',8192))
        args['maxLines']=q.get('maxLines',args.get('maxLines',600))
plan_path.write_text(json.dumps(plan,indent=2)+'\n')
PY

dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project "$work/project" --out "$work/run" >/dev/null
run_dir="$(find "$work/run" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1)"
[[ -n "$run_dir" && -d "$run_dir" ]] || { echo "refresh.retrieval_golden.failed: missing run output" >&2; exit 1; }

mkdir -p "$expected_dir"
head -n 30 "$run_dir/retrieval/context_pack.txt" > "$expected_dir/context_pack_first30.txt"
python - "$run_dir/retrieval/hits.ndjson" "$query_file" "$expected_dir/top_hits.ndjson" <<'PY'
import json,sys,pathlib
hits=[json.loads(x) for x in pathlib.Path(sys.argv[1]).read_text().splitlines() if x.strip()]
q=json.loads(pathlib.Path(sys.argv[2]).read_text())
out=pathlib.Path(sys.argv[3])
topn=int(q.get('requiredTopN',10))
with out.open('w',encoding='utf-8') as f:
    for h in hits[:topn]:
        f.write(json.dumps(h,separators=(',',':'))+'\n')
PY
python - "$run_dir/retrieval/stats.json" "$expected_dir/stats.json" <<'PY'
import json,sys,pathlib
stats=json.loads(pathlib.Path(sys.argv[1]).read_text())
subset={k:stats.get(k) for k in ['bytesOut','linesOut','filesOut','truncatedFlags']}
pathlib.Path(sys.argv[2]).write_text(json.dumps(subset,indent=2)+'\n')
PY

for f in "$expected_dir/context_pack_first30.txt" "$expected_dir/top_hits.ndjson" "$expected_dir/stats.json"; do
  sha256sum "$f"
done | sort > "$after_hashes"

echo "refresh.retrieval_golden.fixture=$fixture_root"
echo "refresh.retrieval_golden.run_dir=$run_dir"

diff -u "$before_hashes" "$after_hashes" || true
