#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

print_diagnostics() {
  if [[ -f artifacts/maintenance/failure-fingerprint.json ]]; then
    cat artifacts/maintenance/failure-fingerprint.json
  fi

  latest_narration="$(find .state/runs -type f -path '*/narration/events.ndjson' -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -z "$latest_narration" ]]; then
    latest_narration="$(ls -1t artifacts/builder_loop/*/run/*/narration/events.ndjson 2>/dev/null | head -n 1 || true)"
  fi
  if [[ -n "$latest_narration" ]]; then
    tail -n 120 "$latest_narration"
  fi

  latest_run_summary="$(find .state/runs -type f -path '*/run_summary.md' -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -z "$latest_run_summary" ]]; then
    latest_run_summary="$(ls -1t artifacts/builder_loop/*/run/*/run_summary.md 2>/dev/null | head -n 1 || true)"
  fi
  if [[ -n "$latest_run_summary" ]]; then
    tail -n 120 "$latest_run_summary"
  fi

  if [[ -f artifacts/stubs/triage.md ]]; then
    tail -n 120 artifacts/stubs/triage.md
  elif [[ -f artifacts/stubs/stubs.txt ]]; then
    tail -n 120 artifacts/stubs/stubs.txt
  fi

  latest_test_log="$(find artifacts -type f -name "*.log" -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -n "$latest_test_log" ]]; then
    tail -n 120 "$latest_test_log"
  fi
}

run_ui() {
  RUN_TESTS=1 RUN_UI=1 bash scripts/maintenance.sh --tests --ui
}

run_builder() {
  bash scripts/builder_loop.sh
}

run_retrieval() {
  RUN_TESTS=1 bash scripts/maintenance.sh --tests
  dotnet test src/Contracts/Shoots.Contracts.Core.Tests/Shoots.Contracts.Core.Tests.csproj -c Release
}

run_stubs() {
  bash scripts/find_stubs.sh
  bash scripts/triage_stubs.sh
}

run_default() {
  bash scripts/codex_fix_loop.sh
  bash scripts/builder_loop.sh
}

mode="${1:---default}"
run_ui_requested="${RUN_UI:-}"

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

if [[ "$mode" == "--stubs" ]]; then
  run_stubs || { print_diagnostics; exit 1; }
  echo "codex entrypoint stubs mode completed"
  exit 0
fi

if [[ "$mode" == "--retrieval" ]]; then
  run_retrieval || { print_diagnostics; exit 1; }
  echo "codex entrypoint retrieval mode completed"
  exit 0
fi

if [[ "$mode" == "--all" ]]; then
  run_stubs || { print_diagnostics; exit 1; }
  RUN_TESTS=1 bash scripts/maintenance.sh --tests || { print_diagnostics; exit 1; }
  run_builder || { print_diagnostics; exit 1; }
  run_retrieval || { print_diagnostics; exit 1; }

  run_ui_in_all=0
  if [[ -n "$run_ui_requested" ]]; then
    if [[ "$run_ui_requested" == "1" ]]; then
      run_ui_in_all=1
    fi
  elif [[ "${ENABLE_WINDOWS_CI:-0}" == "1" ]]; then
    run_ui_in_all=1
  fi

  if [[ "$run_ui_in_all" == "1" ]]; then
    run_ui || { print_diagnostics; exit 1; }
  else
    echo "Skipping --ui in --all (set RUN_UI=1 or ENABLE_WINDOWS_CI=1 to enable)."
  fi

  echo "codex entrypoint all mode completed"
  exit 0
fi

run_default || { print_diagnostics; exit 1; }
echo "codex entrypoint completed"
