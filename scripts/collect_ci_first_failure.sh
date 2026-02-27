#!/usr/bin/env bash
set -euo pipefail

workflow="ci"
strict=0
branch=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --strict)
      strict=1
      shift
      ;;
    -h|--help)
      cat <<'EOF'
Usage: collect_ci_first_failure.sh [--strict] [branch]

Collect latest CI run URL plus first failing job/step.
- default: missing gh prints message and exits 0
- --strict: missing gh exits 2
EOF
      exit 0
      ;;
    *)
      branch="$1"
      shift
      ;;
  esac
done

if ! command -v gh >/dev/null 2>&1; then
  echo "gh not found; install gh or run this inside GitHub Actions"
  if [[ "$strict" -eq 1 ]]; then
    exit 2
  fi
  exit 0
fi

if [[ -n "$branch" ]]; then
  run_id=$(gh run list --workflow "$workflow" --branch "$branch" --limit 1 --json databaseId --jq '.[0].databaseId // ""')
  run_url=$(gh run list --workflow "$workflow" --branch "$branch" --limit 1 --json url --jq '.[0].url // ""')
else
  run_id=$(gh run list --workflow "$workflow" --limit 1 --json databaseId --jq '.[0].databaseId // ""')
  run_url=$(gh run list --workflow "$workflow" --limit 1 --json url --jq '.[0].url // ""')
fi

if [[ -z "$run_id" ]]; then
  echo "error: no ci runs found" >&2
  exit 1
fi

first_job=$(gh run view "$run_id" --json jobs --jq '.jobs[] | select(.conclusion != null and .conclusion != "success" and .conclusion != "skipped") | .name' | head -n1)
first_step=$(gh run view "$run_id" --json jobs --jq '.jobs[] | select(.conclusion != null and .conclusion != "success" and .conclusion != "skipped") | .steps[] | select(.conclusion != null and .conclusion != "success" and .conclusion != "skipped") | .name' | head -n1)

if [[ -z "$first_job" ]]; then
  first_job="<none>"
fi
if [[ -z "$first_step" ]]; then
  first_step="<none>"
fi

echo "Run URL: $run_url"
echo "First failing job: $first_job"
echo "First failing step: $first_step"
