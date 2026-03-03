#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

print_diagnostics() {
  if [[ -f artifacts/maintenance/failure-fingerprint.json ]]; then
    cat artifacts/maintenance/failure-fingerprint.json
  fi

  latest_narration="$(ls -1t artifacts/builder_loop/*/run/*/narration/events.ndjson 2>/dev/null | head -n 1 || true)"
  if [[ -n "$latest_narration" ]]; then
    tail -n 120 "$latest_narration"
  fi

  latest_test_log="$(find artifacts -type f -name "*.log" -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -n "$latest_test_log" ]]; then
    tail -n 120 "$latest_test_log"
  fi
}

run_ui() {
  RUN_TESTS=1 bash scripts/maintenance.sh --tests
  dotnet build ui/Shoots.Ui/Shoots.Ui.csproj -c Release --no-restore
  dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Release --no-build
}

run_builder() {
  bash scripts/builder_loop.sh
}

run_default() {
  bash scripts/codex_fix_loop.sh
  bash scripts/builder_loop.sh
}

mode="${1:---default}"

if [[ "$mode" == "--ui" ]]; then
  run_ui || { print_diagnostics; exit 1; }
  echo "codex entrypoint ui mode completed"
  exit 0
fi

if [[ "$mode" == "--builder" ]]; then
  run_builder || { print_diagnostics; exit 1; }
  echo "codex entrypoint builder mode completed"
  exit 0
fi

if [[ "$mode" == "--all" ]]; then
  RUN_TESTS=1 bash scripts/maintenance.sh --tests || { print_diagnostics; exit 1; }
  run_builder || { print_diagnostics; exit 1; }
  run_ui || { print_diagnostics; exit 1; }
  echo "codex entrypoint all mode completed"
  exit 0
fi

run_default || { print_diagnostics; exit 1; }
echo "codex entrypoint completed"
