#!/usr/bin/env bash
set -euo pipefail

MAX_MEMORY_MB="${MAX_MEMORY_MB:-2048}"

if [[ "$MAX_MEMORY_MB" != "${MAX_MEMORY_MB//[^0-9]/}" || -z "$MAX_MEMORY_MB" ]]; then
  echo "verify.memory_profile.bad_max_memory_mb: $MAX_MEMORY_MB" >&2
  exit 64
fi

if [[ -f artifacts/smoke/latest_summary.env ]]; then
  # shellcheck disable=SC1091
  source artifacts/smoke/latest_summary.env
fi

run_dir="${RUN_DIR:-}"
[[ -n "$run_dir" && -d "$run_dir" ]] || { echo "verify.memory_profile.run_dir_missing" >&2; exit 64; }

if ! command -v /usr/bin/time >/dev/null 2>&1; then
  echo "verify.memory_profile.time_missing" >&2
  exit 64
fi

mem_log="artifacts/memory_profile.txt"
mkdir -p artifacts

/usr/bin/time -f '%M' -o "$mem_log" bash scripts/replay_runner.sh "$run_dir" >/dev/null 2>&1
peak_kb="$(cat "$mem_log" | tr -d '[:space:]')"
[[ "$peak_kb" =~ ^[0-9]+$ ]] || { echo "verify.memory_profile.bad_peak_kb: $peak_kb" >&2; exit 1; }
peak_mb="$(( (peak_kb + 1023) / 1024 ))"

if (( peak_mb > MAX_MEMORY_MB )); then
  echo "verify.memory_profile.exceeded: peak_mb=$peak_mb;max_mb=$MAX_MEMORY_MB" >&2
  exit 1
fi

echo "MEMORY_PROFILE_OK=1"
echo "PEAK_MEMORY_MB=$peak_mb"
