#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

resolve_latest_run() {
  local candidate=""
  candidate="$(find .state/runs -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -z "$candidate" ]]; then
    candidate="$(find artifacts/builder_loop -type d -path '*/run/*' ! -path '*/run/*/*' -print 2>/dev/null | sort | tail -n 1 || true)"
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
    echo "verify.budget.run_missing: no run directory found" >&2
    exit 1
  fi

  echo "verify.budget.run_missing: no run directory found"
  exit 0
fi

max_context_bytes="${MAX_CONTEXT_PACK_BYTES:-262144}"
max_stream_bytes="${MAX_STEP_STREAM_BYTES:-131072}"
max_narration_bytes="${MAX_NARRATION_BYTES:-262144}"

context_pack="$run_dir/retrieval/context_pack.txt"
if [[ -f "$context_pack" ]]; then
  context_size="$(wc -c < "$context_pack")"
  if (( context_size > max_context_bytes )); then
    fail "verify.budget.context_pack.exceeded" "$context_pack exceeds ${max_context_bytes} bytes"
  fi
fi

narration="$run_dir/narration/events.ndjson"
if [[ -f "$narration" ]]; then
  narration_size="$(wc -c < "$narration")"
  if (( narration_size > max_narration_bytes )); then
    fail "verify.budget.narration.exceeded" "$narration exceeds ${max_narration_bytes} bytes"
  fi
fi

while IFS= read -r stream_file; do
  [[ -z "$stream_file" ]] && continue
  stream_size="$(wc -c < "$stream_file")"
  if (( stream_size > max_stream_bytes )); then
    fail "verify.budget.step_stream.exceeded" "$stream_file exceeds ${max_stream_bytes} bytes"
  fi
done < <(find "$run_dir/steps" -type f \( -name 'stdout.txt' -o -name 'stderr.txt' \) -print 2>/dev/null | sort)

echo "verify.budget.ok: $run_dir"
