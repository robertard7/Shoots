#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

bash scripts/codex_ci_smoke.sh

latest_run="$(find .state/runs -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort | tail -n1 || true)"
if [[ -z "$latest_run" ]]; then
  latest_run="$(find artifacts/builder_loop -type d -path '*/run/*' ! -path '*/run/*/*' -print 2>/dev/null | sort | tail -n1 || true)"
fi

[[ -n "$latest_run" && -d "$latest_run" ]] || { echo "demo.builder.run_missing: no run directory found" >&2; exit 1; }

summary="$latest_run/run_summary.md"
narration="$latest_run/narration/events.ndjson"
stats="$latest_run/retrieval/stats.json"

[[ -f "$summary" ]] || { echo "demo.builder.missing: $summary" >&2; exit 1; }
[[ -f "$narration" ]] || { echo "demo.builder.missing: $narration" >&2; exit 1; }
[[ -f "$stats" ]] || { echo "demo.builder.missing: $stats" >&2; exit 1; }

echo "demo.builder.run_dir=$latest_run"
echo "=== run_summary.md ==="
cat "$summary"
echo "=== narration (top 20) ==="
head -n 20 "$narration"
echo "=== retrieval stats ==="
cat "$stats"
