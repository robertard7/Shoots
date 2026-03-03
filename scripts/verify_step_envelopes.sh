#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

error_code=""
fail() {
  error_code="$1"
  echo "$1: $2" >&2
  exit 1
}

resolve_latest_run() {
  local candidate=""
  candidate="$(find .state/runs -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -z "$candidate" ]]; then
    candidate="$(find artifacts/builder_loop -type d -path '*/run/*' -print 2>/dev/null | sort | tail -n 1 || true)"
  fi
  printf '%s' "$candidate"
}

require_run=0
if [[ "${1:-}" == "--require-run" ]]; then
  require_run=1
  shift
fi

run_dir="${1:-$(resolve_latest_run)}"
if [[ -z "$run_dir" || ! -d "$run_dir" ]]; then
  if (( require_run == 1 )); then
    fail "verify.step_envelope.run_missing" "no run directory found"
  fi
  echo "step envelope verification skipped: no run directory found"
  exit 0
fi

summary_path="$run_dir/steps/summary.ndjson"
if [[ ! -f "$summary_path" ]]; then
  fail "verify.step_envelope.summary_missing" "missing $summary_path"
fi

required=(request.json result.json stdout.txt stderr.txt exit.json hashes.json)
max_bytes="${MAX_STEP_ARTIFACT_BYTES:-262144}"

step_dirs="$(find "$run_dir/steps" -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort || true)"
if [[ -z "$step_dirs" ]]; then
  fail "verify.step_envelope.steps_missing" "no step directories under $run_dir/steps"
fi

while IFS= read -r step_dir; do
  [[ -z "$step_dir" ]] && continue
  for file in "${required[@]}"; do
    path="$step_dir/$file"
    if [[ ! -f "$path" ]]; then
      fail "verify.step_envelope.file_missing" "missing $path"
    fi
    size="$(wc -c < "$path")"
    if (( size > max_bytes )); then
      fail "verify.step_envelope.file_too_large" "$path exceeds ${max_bytes} bytes"
    fi
  done
done <<< "$step_dirs"

echo "step envelope verification passed for $run_dir"
