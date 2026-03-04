#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

mkdir -p artifacts/flaky
report="artifacts/flaky/flaky-report.json"

project="${FLAKY_TEST_PROJECT:-src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj}"
filter="${FLAKY_TEST_FILTER:-Shoots.Runtime.Tests.RoutingLoopTests.Provider_tool_choice_does_not_change_route_path}"
runs="${FLAKY_TEST_RUNS:-3}"
configuration="${CONFIGURATION:-Release}"

if [[ "$runs" -lt 2 ]]; then
  runs=2
fi

results=()
pass_count=0
fail_count=0

for idx in $(seq 1 "$runs"); do
  log_path="artifacts/flaky/run-${idx}.log"
  set +e
  dotnet test "$project" -c "$configuration" --no-build --filter "FullyQualifiedName~${filter}" >"$log_path" 2>&1
  status=$?
  set -e

  outcome="pass"
  if [[ "$status" -ne 0 ]]; then
    outcome="fail"
    fail_count=$((fail_count + 1))
  else
    pass_count=$((pass_count + 1))
  fi

  results+=("{\"run\":$idx,\"status\":$status,\"outcome\":\"$outcome\",\"log\":\"$log_path\"}")
done

if (( pass_count > 0 && fail_count > 0 )); then
  flaky=true
  error_code="verify.flaky.detected"
  exit_code=1
else
  flaky=false
  error_code=""
  exit_code=0
fi

{
  echo "{"
  echo "  \"project\": \"$project\"," 
  echo "  \"filter\": \"$filter\"," 
  echo "  \"runs\": $runs,"
  echo "  \"passCount\": $pass_count,"
  echo "  \"failCount\": $fail_count,"
  echo "  \"flaky\": $flaky,"
  echo "  \"errorCode\": \"$error_code\"," 
  echo "  \"results\": [$(IFS=,; echo "${results[*]}")]"
  echo "}"
} > "$report"

if [[ "$exit_code" -ne 0 ]]; then
  echo "verify.flaky.detected: inconsistent outcomes for $filter" >&2
  exit "$exit_code"
fi

echo "verify.flaky.ok: $filter"
