#!/usr/bin/env bash
set -uo pipefail

export LC_ALL=C
export LANG=C
export TZ=UTC

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

RUN_TESTS="${RUN_TESTS:-0}"
CONFIGURATION="${CONFIGURATION:-Release}"
SOLUTION_PATH="${SOLUTION_PATH:-}"
RUN_UI="${RUN_UI:-}"

if [[ -z "$RUN_UI" ]]; then
  if [[ "${OS:-}" == "Windows_NT" ]]; then
    RUN_UI=1
  else
    RUN_UI=0
  fi
fi

if [[ -z "$SOLUTION_PATH" ]]; then
  if [[ "${OS:-}" == "Windows_NT" ]]; then
    SOLUTION_PATH="Shoots.sln"
  else
    SOLUTION_PATH="src/Runtime/Shoots.Runtime.sln"
  fi
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tests)
      RUN_TESTS=1
      shift
      ;;
    --ui)
      RUN_UI=1
      shift
      ;;
    -h|--help)
      cat <<'USAGE'
Usage: bash scripts/maintenance.sh [--tests] [--ui]

Environment variables:
  RUN_TESTS=1          Run tests after restore/build
  CONFIGURATION=Debug  Build/test configuration (default: Release)
  SOLUTION_PATH=...    Override solution path (default: Shoots.sln on Windows, src/Runtime/Shoots.Runtime.sln otherwise)
  RUN_UI=1             Build/test UI projects (default: 1 on Windows, 0 otherwise)
USAGE
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

mkdir -p artifacts/maintenance
stamp="$(date +%Y%m%d-%H%M%S)"
restore_log="artifacts/maintenance/restore-${stamp}.log"
build_log="artifacts/maintenance/build-${stamp}.log"
tests_log="artifacts/maintenance/tests-${stamp}.log"
ui_build_log="artifacts/maintenance/ui-build-${stamp}.log"
ui_tests_log="artifacts/maintenance/ui-tests-${stamp}.log"
fingerprint_file="artifacts/maintenance/failure-fingerprint.json"
flaky_file="artifacts/maintenance/flaky-test.md"

restore_status=0
build_status=0
tests_status=99
ui_build_status=99
ui_tests_status=99
overall_status=0
error_code=""

ensure_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    return 0
  fi

  if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
    echo "dotnet not found on GitHub Actions runner." >&2
    return 127
  fi

  local install_root="${HOME}/.dotnet"
  local install_script="${install_root}/dotnet-install.sh"
  mkdir -p "$install_root"

  if [[ ! -f "$install_script" ]]; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$install_script"
    chmod +x "$install_script"
  fi

  "$install_script" --channel 8.0 --install-dir "$install_root" --no-path
  export PATH="$install_root:$PATH"

  if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet installation did not provide dotnet on PATH." >&2
    return 127
  fi
}

run_and_capture() {
  local log_file="$1"
  shift
  ( "$@" ) 2>&1 | tee "$log_file"
  return ${PIPESTATUS[0]}
}

status_word() {
  local code="$1"
  case "$code" in
    0) echo "success" ;;
    99) echo "skipped" ;;
    *) echo "fail (exit ${code})" ;;
  esac
}

json_escape() {
  python -c 'import json,sys; print(json.dumps(sys.stdin.read()))'
}

set_error_code() {
  if [[ -n "$error_code" ]]; then
    return
  fi

  case "$1" in
    restore) error_code="maintenance.restore.failed" ;;
    build) error_code="maintenance.build.failed" ;;
    tests) error_code="maintenance.tests.failed" ;;
    *) error_code="maintenance.unknown.failed" ;;
  esac
}

write_failure_fingerprint() {
  local source_log=""
  local failing_project=""
  local failing_test=""
  local exception=""
  local message=""
  local excerpt=""

  if [[ "$tests_status" -ne 0 && "$tests_status" -ne 99 ]]; then
    source_log="$tests_log"
    set_error_code tests
  elif [[ "$build_status" -ne 0 && "$build_status" -ne 99 ]]; then
    source_log="$build_log"
    set_error_code build
  elif [[ "$restore_status" -ne 0 ]]; then
    source_log="$restore_log"
    set_error_code restore
  else
    rm -f "$fingerprint_file"
    return
  fi

  if [[ -f "$source_log" ]]; then
    failing_project="$(sed -nE 's#.*(src/[^[:space:]]+\.(csproj|sln)).*#\1#p' "$source_log" | tail -n1)"
    failing_test="$(sed -nE 's#.*Failed[[:space:]]+([^[:space:]]+).*#\1#p' "$source_log" | tail -n1)"
    if [[ -z "$failing_test" ]]; then
      failing_test="$(sed -nE 's#.*(\b[A-Za-z0-9_.]+\.[A-Za-z0-9_]+\([^)]+\)).*#\1#p' "$source_log" | tail -n1)"
    fi
    exception="$(sed -nE 's#.*([A-Za-z0-9_.]+Exception).*#\1#p' "$source_log" | tail -n1)"
    message="$(grep -E "error|failed|exception" -i "$source_log" | tail -n1 || true)"
    excerpt="$(tail -n 200 "$source_log")"
  fi

  {
    echo "{"
    echo "  \"errorCode\": $(printf '%s' "$error_code" | json_escape),"
    echo "  \"failingProject\": $(printf '%s' "$failing_project" | json_escape),"
    echo "  \"failingTestFullName\": $(printf '%s' "$failing_test" | json_escape),"
    echo "  \"exceptionType\": $(printf '%s' "$exception" | json_escape),"
    echo "  \"exceptionMessage\": $(printf '%s' "$message" | json_escape),"
    echo "  \"excerptLast200Lines\": $(printf '%s' "$excerpt" | json_escape),"
    echo "  \"sourceLog\": $(printf '%s' "$source_log" | json_escape)"
    echo "}"
  } > "$fingerprint_file"

  if [[ "$error_code" == "maintenance.tests.failed" ]]; then
    local commit_sha="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
    {
      echo "# Flaky Test Capture"
      echo
      echo "- commit: ${commit_sha}"
      echo "- failingTest: ${failing_test:-unknown}"
      echo "- errorCode: ${error_code}"
      echo "- assertion: ${message:-unknown}"
      echo "- sourceLog: ${source_log:-unknown}"
    } > "$flaky_file"
  fi
}

ensure_dotnet || exit $?

echo "==> Restoring solution"
run_and_capture "$restore_log" dotnet restore "$SOLUTION_PATH"
restore_status=$?
if [[ "$restore_status" -ne 0 ]]; then
  overall_status=1
  set_error_code restore
fi

echo "==> Verifying UI contract drift guard"
if [[ "$overall_status" -eq 0 ]]; then
  run_and_capture "$build_log" bash scripts/verify_ui_contracts.sh
  ui_contract_status=$?
  if [[ "$ui_contract_status" -ne 0 ]]; then
    overall_status=1
    set_error_code build
  fi
fi

echo "==> Building solution ($CONFIGURATION)"
if [[ "$overall_status" -eq 0 ]]; then
  run_and_capture "$build_log" dotnet build "$SOLUTION_PATH" -c "$CONFIGURATION" --no-restore -p:ContinuousIntegrationBuild=true /bl:"artifacts/maintenance/build-${stamp}.binlog"
  build_status=$?
  if [[ "$build_status" -ne 0 ]]; then
    overall_status=1
    set_error_code build
  fi
else
  echo "Skipping build because restore failed." | tee "$build_log"
  build_status=99
fi

if [[ "$RUN_TESTS" == "1" ]]; then
  echo "==> Verifying codex diagnostics order"
  run_and_capture "$tests_log" bash scripts/verify_diagnostics_order.sh
  diagnostics_status=$?
  if [[ "$diagnostics_status" -ne 0 ]]; then
    tests_status=$diagnostics_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying blocking stubs"
  run_and_capture "$tests_log" bash scripts/verify_no_blocking_stubs.sh
  stub_status=$?
  if [[ "$stub_status" -ne 0 ]]; then
    tests_status=$stub_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying step envelopes"
  run_and_capture "$tests_log" bash scripts/verify_step_envelopes.sh
  step_envelope_status=$?
  if [[ "$step_envelope_status" -ne 0 ]]; then
    tests_status=$step_envelope_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying artifact budgets"
  run_and_capture "$tests_log" bash scripts/verify_artifact_budgets.sh
  artifact_budget_status=$?
  if [[ "$artifact_budget_status" -ne 0 ]]; then
    tests_status=$artifact_budget_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying narration coverage"
  run_and_capture "$tests_log" bash scripts/verify_narration_coverage.sh
  narration_coverage_status=$?
  if [[ "$narration_coverage_status" -ne 0 ]]; then
    tests_status=$narration_coverage_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying slice decisions"
  run_and_capture "$tests_log" bash scripts/verify_slice_decisions.sh
  slice_decisions_status=$?
  if [[ "$slice_decisions_status" -ne 0 ]]; then
    tests_status=$slice_decisions_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying plan evidence"
  run_and_capture "$tests_log" bash scripts/verify_plan_evidence.sh
  plan_evidence_status=$?
  if [[ "$plan_evidence_status" -ne 0 ]]; then
    tests_status=$plan_evidence_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying provider auditability"
  run_and_capture "$tests_log" bash scripts/verify_provider_audit.sh
  provider_audit_status=$?
  if [[ "$provider_audit_status" -ne 0 ]]; then
    tests_status=$provider_audit_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying plan hash chain"
  run_and_capture "$tests_log" bash scripts/verify_plan_hash_chain.sh
  plan_hash_chain_status=$?
  if [[ "$plan_hash_chain_status" -ne 0 ]]; then
    tests_status=$plan_hash_chain_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying retrieval context determinism"
  run_and_capture "$tests_log" bash scripts/verify_context_pack_determinism.sh
  context_determinism_status=$?
  if [[ "$context_determinism_status" -ne 0 ]]; then
    tests_status=$context_determinism_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying retrieval quality"
  run_and_capture "$tests_log" bash scripts/verify_retrieval_quality.sh
  retrieval_quality_status=$?
  if [[ "$retrieval_quality_status" -ne 0 ]]; then
    tests_status=$retrieval_quality_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying retrieval golden fixture"
  run_and_capture "$tests_log" bash scripts/verify_retrieval_golden.sh
  retrieval_golden_status=$?
  if [[ "$retrieval_golden_status" -ne 0 ]]; then
    tests_status=$retrieval_golden_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Verifying no flaky tests"
  run_and_capture "$tests_log" bash scripts/verify_no_flaky_tests.sh
  flaky_status=$?
  if [[ "$flaky_status" -ne 0 ]]; then
    tests_status=$flaky_status
    overall_status=1
    set_error_code tests
  fi

  echo "==> Testing solution ($CONFIGURATION)"
  if [[ "$overall_status" -eq 0 ]]; then
    run_and_capture "$tests_log" dotnet test "$SOLUTION_PATH" -c "$CONFIGURATION" --no-build -p:ContinuousIntegrationBuild=true --logger "trx;LogFileName=artifacts/maintenance/tests-${stamp}.trx"
    tests_status=$?
    if [[ "$tests_status" -ne 0 ]]; then
      overall_status=1
      set_error_code tests
    fi
  else
    echo "Skipping tests because restore/build failed." | tee "$tests_log"
    tests_status=99
  fi
else
  echo "RUN_TESTS is not set to 1; skipping tests." | tee "$tests_log"
  tests_status=99
fi

if [[ "$RUN_UI" == "1" ]]; then
  echo "==> Restoring UI projects"
  if [[ "$overall_status" -eq 0 ]]; then
    run_and_capture "$ui_build_log" dotnet restore ui/Shoots.Ui/Shoots.Ui.csproj
    ui_restore_status=$?
    if [[ "$ui_restore_status" -ne 0 ]]; then
      overall_status=1
      set_error_code restore
    fi
  fi

  echo "==> Building UI projects ($CONFIGURATION)"
  if [[ "$overall_status" -eq 0 ]]; then
    run_and_capture "$ui_build_log" dotnet build ui/Shoots.Ui/Shoots.Ui.csproj -c "$CONFIGURATION" --no-restore -p:ContinuousIntegrationBuild=true
    ui_build_status=$?
    if [[ "$ui_build_status" -ne 0 ]]; then
      overall_status=1
      set_error_code build
    fi
  else
    echo "Skipping UI build because previous stage failed." | tee "$ui_build_log"
    ui_build_status=99
  fi

  if [[ "$RUN_TESTS" == "1" ]]; then
    echo "==> Testing UI projects ($CONFIGURATION)"
    if [[ "$overall_status" -eq 0 ]]; then
      run_and_capture "$ui_tests_log" dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c "$CONFIGURATION" --no-build -p:ContinuousIntegrationBuild=true
      ui_tests_status=$?
      if [[ "$ui_tests_status" -ne 0 ]]; then
        overall_status=1
        set_error_code tests
      fi
    else
      echo "Skipping UI tests because previous stage failed." | tee "$ui_tests_log"
      ui_tests_status=99
    fi
  fi
else
  echo "RUN_UI is not set to 1; skipping UI build/test." | tee "$ui_build_log"
fi

write_failure_fingerprint

cat <<SUMMARY

===== maintenance summary =====
restore: $(status_word "$restore_status")
build:   $(status_word "$build_status")
tests:   $(status_word "$tests_status")
ui_build: $(status_word "$ui_build_status")
ui_tests: $(status_word "$ui_tests_status")
errorCode: ${error_code:-none}

logs:
- $restore_log
- $build_log
- $tests_log
- $ui_build_log
- $ui_tests_log

solution:
- $SOLUTION_PATH

failure fingerprint:
- $fingerprint_file
- $flaky_file
==============================
SUMMARY

exit "$overall_status"
