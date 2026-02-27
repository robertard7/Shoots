#!/usr/bin/env bash
set -euo pipefail

workflow="ci"
branch="${1:-}"

if ! command -v gh >/dev/null 2>&1; then
  echo "error: gh CLI is required" >&2
  exit 1
fi

if [[ -n "$branch" ]]; then
  run_json=$(gh run list --workflow "$workflow" --branch "$branch" --limit 1 --json databaseId,url,headBranch,status,conclusion 2>/dev/null)
else
  run_json=$(gh run list --workflow "$workflow" --limit 1 --json databaseId,url,headBranch,status,conclusion 2>/dev/null)
fi

run_id=$(python3 - <<'PY' "$run_json"
import json,sys
runs=json.loads(sys.argv[1])
print(runs[0]["databaseId"] if runs else "")
PY
)

if [[ -z "$run_id" ]]; then
  echo "error: no ci runs found" >&2
  exit 1
fi

run_url=$(python3 - <<'PY' "$run_json"
import json,sys
runs=json.loads(sys.argv[1])
print(runs[0]["url"])
PY
)

jobs_json=$(gh run view "$run_id" --json jobs 2>/dev/null)

python3 - <<'PY' "$run_url" "$jobs_json"
import json,sys
run_url=sys.argv[1]
jobs=json.loads(sys.argv[2]).get("jobs", [])
failed_job=None
failed_step=None
for job in jobs:
    if job.get("conclusion") not in (None, "success", "skipped"):
        failed_job=job
        break
if failed_job:
    for step in failed_job.get("steps", []):
        if step.get("conclusion") not in (None, "success", "skipped"):
            failed_step=step
            break
print(f"Run URL: {run_url}")
if failed_job is None:
    print("First failing job: <none>")
    print("First failing step: <none>")
else:
    print(f"First failing job: {failed_job.get('name')}")
    if failed_step is None:
      print("First failing step: <none>")
    else:
      print(f"First failing step: {failed_step.get('name')}")
PY
