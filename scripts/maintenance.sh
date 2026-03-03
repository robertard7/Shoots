#!/usr/bin/env bash
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

RUN_TESTS="${RUN_TESTS:-0}"
CONFIGURATION="${CONFIGURATION:-Release}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tests)
      RUN_TESTS=1
      shift
      ;;
    -h|--help)
      cat <<'USAGE'
Usage: bash scripts/maintenance.sh [--tests]

Environment variables:
  RUN_TESTS=1         Run tests after restore/build
  CONFIGURATION=Debug Build/test configuration (default: Release)
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

restore_status=0
build_status=0
tests_status=0
overall_status=0

run_and_capture() {
  local log_file="$1"
  shift
  ( "$@" ) 2>&1 | tee "$log_file"
  return ${PIPESTATUS[0]}
}

echo "==> Restoring solution"
run_and_capture "$restore_log" dotnet restore Shoots.sln
restore_status=$?
if [[ "$restore_status" -ne 0 ]]; then
  overall_status=1
fi

echo "==> Building solution ($CONFIGURATION)"
if [[ "$overall_status" -eq 0 ]]; then
  run_and_capture "$build_log" dotnet build Shoots.sln -c "$CONFIGURATION" --no-restore -p:ContinuousIntegrationBuild=true /bl:"artifacts/maintenance/build-${stamp}.binlog"
  build_status=$?
  if [[ "$build_status" -ne 0 ]]; then
    overall_status=1
  fi
else
  echo "Skipping build because restore failed." | tee "$build_log"
  build_status=99
fi

if [[ "$RUN_TESTS" == "1" ]]; then
  echo "==> Testing solution ($CONFIGURATION)"
  if [[ "$overall_status" -eq 0 ]]; then
    run_and_capture "$tests_log" dotnet test Shoots.sln -c "$CONFIGURATION" --no-build -p:ContinuousIntegrationBuild=true --logger "trx;LogFileName=artifacts/maintenance/tests-${stamp}.trx"
    tests_status=$?
    if [[ "$tests_status" -ne 0 ]]; then
      overall_status=1
    fi
  else
    echo "Skipping tests because restore/build failed." | tee "$tests_log"
    tests_status=99
  fi
else
  echo "RUN_TESTS is not set to 1; skipping tests." | tee "$tests_log"
  tests_status=0
fi

status_word() {
  local code="$1"
  case "$code" in
    0) echo "success" ;;
    99) echo "skipped" ;;
    *) echo "fail (exit ${code})" ;;
  esac
}

cat <<SUMMARY

===== maintenance summary =====
restore: $(status_word "$restore_status")
build:   $(status_word "$build_status")
tests:   $(status_word "$tests_status")

logs:
- $restore_log
- $build_log
- $tests_log
==============================
SUMMARY

exit "$overall_status"
