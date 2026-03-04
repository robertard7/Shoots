#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  local code="$1"
  local reason="$2"
  local fixture="$3"
  local run_dir="$4"
  echo "$code: fixture=$fixture run_dir=$run_dir compare=$reason" >&2
  exit 1
}

fixture_root="etc/fixtures/retrieval_golden"
query_file="$fixture_root/query.json"
expected_pack="$fixture_root/expected/context_pack_first30.txt"
expected_hits="$fixture_root/expected/top_hits.ndjson"
expected_stats="$fixture_root/expected/stats.json"

[[ -f "$query_file" && -f "$expected_pack" && -f "$expected_hits" && -f "$expected_stats" ]] || fail "verify.retrieval_golden.missing" "fixture_files" "$fixture_root" "none"

work="artifacts/retrieval_golden"
python - <<'PY2'
import pathlib,shutil
p=pathlib.Path("artifacts/retrieval_golden")
if p.exists():
    shutil.rmtree(p)
(p/"project").mkdir(parents=True,exist_ok=True)
PY2

python - "$query_file" <<'PY'
import json,sys,pathlib,shutil
q=json.loads(pathlib.Path(sys.argv[1]).read_text())
project=pathlib.Path(q['project'])
out=pathlib.Path('artifacts/retrieval_golden/project')
if out.exists(): shutil.rmtree(out)
shutil.copytree(project,out)
plan=json.loads((out/'plan/plan.json').read_text())
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
(out/'plan/plan.json').write_text(json.dumps(plan,indent=2)+"\n")
PY

dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project "$work/project" --out "$work/run" >/dev/null
run_dir="$(find "$work/run" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1)"
[[ -n "$run_dir" && -d "$run_dir" ]] || fail "verify.retrieval_golden.missing" "run_output" "$fixture_root" "none"
echo "verify.retrieval_golden.fixture=$fixture_root"
echo "verify.retrieval_golden.run_dir=$run_dir"

actual_pack="$work/context_pack_first30.txt"
head -n 30 "$run_dir/retrieval/context_pack.txt" > "$actual_pack"
cmp -s "$expected_pack" "$actual_pack" || fail "verify.retrieval_golden.drift" "context_pack" "$fixture_root" "$run_dir"

if ! python - "$run_dir/retrieval/hits.ndjson" "$expected_hits" "$query_file" <<'PY'; then
import json,sys,pathlib
hits=[json.loads(x) for x in pathlib.Path(sys.argv[1]).read_text().splitlines() if x.strip()]
expected=[json.loads(x) for x in pathlib.Path(sys.argv[2]).read_text().splitlines() if x.strip()]
q=json.loads(pathlib.Path(sys.argv[3]).read_text())
topn=int(q.get('requiredTopN',10))
def norm(items):
    out=[]
    for h in items[:topn]:
        out.append({
            'path':h.get('path',''),
            'score':h.get('score',0),
            'tokensMatched':h.get('tokensMatched',0),
            'firstMatchOffset':h.get('firstMatchOffset',0),
            'pathHash':h.get('pathHash','')
        })
    return out
if norm(hits)!=norm(expected):
    raise SystemExit(1)
PY
  fail "verify.retrieval_golden.drift" "hits" "$fixture_root" "$run_dir"
fi

if ! python - "$run_dir/retrieval/stats.json" "$expected_stats" <<'PY'; then
import json,sys,pathlib
actual=json.loads(pathlib.Path(sys.argv[1]).read_text())
expected=json.loads(pathlib.Path(sys.argv[2]).read_text())
keys=['bytesOut','linesOut','filesOut','truncatedFlags']
a={k:actual.get(k) for k in keys}
e={k:expected.get(k) for k in keys}
if a!=e:
    raise SystemExit(1)
PY
  fail "verify.retrieval_golden.drift" "stats" "$fixture_root" "$run_dir"
fi

echo "verify.retrieval_golden.ok"
