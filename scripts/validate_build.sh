#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

mkdir -p artifacts

required_sdk="$(python - <<'PY'
import json
from pathlib import Path
print(json.loads(Path('global.json').read_text())['sdk']['version'])
PY
)"

export DOTNET_ROOT="${DOTNET_ROOT:-/root/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

capture_failure() {
  local phase="$1"
  local log_file="$2"
  local first_error_line
  first_error_line="$(rg -n -m1 "error [A-Z]{2,}[0-9]+|: error " "$log_file" || true)"

  {
    echo "## validate_build failure"
    echo "phase=$phase"
    echo "log=$log_file"
    if [ -n "$first_error_line" ]; then
      local line_no
      line_no="$(printf '%s' "$first_error_line" | cut -d: -f1)"
      echo "first_error=$first_error_line"
      echo "excerpt_start=$(( line_no > 10 ? line_no - 10 : 1 ))"
      echo "excerpt_end=$(( line_no + 10 ))"
      sed -n "$(( line_no > 10 ? line_no - 10 : 1 )),$(( line_no + 10 ))p" "$log_file"
    else
      echo "first_error=<none-found>"
      tail -n 80 "$log_file" || true
    fi
    echo
  } >> artifacts/ci-first-failure.md
}

run_step() {
  local phase="$1"
  shift
  local log_file="artifacts/${phase}.log"
  set +e
  "$@" 2>&1 | tee "$log_file"
  local rc=${PIPESTATUS[0]}
  set -e
  if [ "$rc" -ne 0 ]; then
    capture_failure "$phase" "$log_file"
    exit "$rc"
  fi
}

bash tools/codex/restore.sh
bash scripts/verify_dotnet_bootstrap.sh

run_step "build-debug" dotnet build Shoots.sln -c Debug -v minimal --no-restore
run_step "test-debug" dotnet test Shoots.sln -c Debug -v minimal --no-build --no-restore

cat > artifacts/build_summary.env <<SUMMARY
BUILD_OK=1
TEST_OK=1
SDK_VERSION=${required_sdk}
COMMIT=$(git rev-parse HEAD)
SUMMARY

echo "VALIDATE_BUILD_OK=1"
echo "SDK=${required_sdk}"
echo "CONFIG=Debug"
