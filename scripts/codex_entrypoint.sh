#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

mode="${1:-default}"

if [[ "$mode" == "--ui" ]]; then
  RUN_TESTS=1 bash scripts/maintenance.sh --tests
  dotnet build ui/Shoots.Ui/Shoots.Ui.csproj -c Release --no-restore
  dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Release --no-build
  if [[ -f artifacts/maintenance/failure-fingerprint.json ]]; then
    cat artifacts/maintenance/failure-fingerprint.json
  fi
  latest_narration="$(ls -1t artifacts/builder_loop/*/run/*/narration/events.ndjson 2>/dev/null | head -n 1 || true)"
  if [[ -n "$latest_narration" ]]; then
    tail -n 120 "$latest_narration"
  fi
  echo "codex entrypoint ui mode completed"
  exit 0
fi

bash scripts/codex_fix_loop.sh
bash scripts/builder_loop.sh

echo "codex entrypoint completed"
