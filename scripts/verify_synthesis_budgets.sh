#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

fixture_root="etc/fixtures/synthesis_limits/project"
[[ -d "$fixture_root" ]] || fail "verify.synthesis_limits.missing" "missing fixture project"

work="artifacts/synthesis_limits"
python - <<'PY'
import pathlib, shutil
p=pathlib.Path('artifacts/synthesis_limits')
if p.exists():
    shutil.rmtree(p)
p.mkdir(parents=True, exist_ok=True)
PY

run_case() {
  local case_name="$1"
  local expected_code="$2"
  local max_steps="$3"
  local max_args_bytes="$4"
  local max_total_plan_bytes="$5"

  local case_dir="$work/$case_name"
  python - "$fixture_root" "$case_dir/project" "$max_steps" "$max_args_bytes" "$max_total_plan_bytes" <<'PY'
import json, pathlib, shutil, sys
src=pathlib.Path(sys.argv[1])
dst=pathlib.Path(sys.argv[2])
if dst.exists():
    shutil.rmtree(dst)
shutil.copytree(src,dst)
plan_path=dst/'plan/plan.json'
plan=json.loads(plan_path.read_text())
for step in plan.get('steps',[]):
    if step.get('kind')=='synthesize_plan.v1':
        args=step.setdefault('args',{})
        args['maxSteps']=int(sys.argv[3])
        args['maxArgsBytes']=int(sys.argv[4])
        args['maxTotalPlanBytes']=int(sys.argv[5])
plan_path.write_text(json.dumps(plan, indent=2)+"\n")
PY

  mkdir -p "$case_dir"
  local status=0
  dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- run --scenario builder_smoke --project "$case_dir/project" --out "$case_dir/run" >"$case_dir/out.log" 2>"$case_dir/err.log" || status=$?
  if [[ "$status" -eq 0 ]]; then
    fail "verify.synthesis_limits.unexpected_success" "$case_name expected failure with $expected_code"
  fi

  local run_dir
  run_dir="$(find "$case_dir/run" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1 || true)"
  [[ -n "$run_dir" && -f "$run_dir/narration/events.ndjson" ]] || fail "verify.synthesis_limits.missing" "$case_name missing run narration"

  if ! python - "$run_dir/narration/events.ndjson" "$expected_code" <<'PY'; then
import json, pathlib, sys
events=[json.loads(x) for x in pathlib.Path(sys.argv[1]).read_text().splitlines() if x.strip()]
expected=sys.argv[2]
for e in events:
    if e.get('code')=='builder.synthesis.failed':
        details=e.get('details') or ''
        if expected in details:
            raise SystemExit(0)
raise SystemExit(1)
PY
    fail "$expected_code" "$case_name missing expected synthesis failure code"
  fi

  echo "verify.synthesis_limits.ok: case=$case_name code=$expected_code"
}

run_case steps_exceeded builder.synthesis.steps_exceeded 1 4096 64000
run_case args_exceeded builder.synthesis.args_exceeded 16 1 64000
run_case plan_exceeded builder.synthesis.plan_exceeded 16 4096 64
