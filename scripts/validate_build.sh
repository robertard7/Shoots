#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

mkdir -p artifacts

python_cmd="$(command -v python3 || command -v python || true)"
if [ -z "$python_cmd" ]; then
  echo "validate_build.python.missing: python3/python is required" >&2
  exit 1
fi

required_sdk="$($python_cmd - <<'PY'
required_sdk="$(python - <<'PY'
import json
from pathlib import Path
print(json.loads(Path('global.json').read_text())['sdk']['version'])
PY
)"

config="Debug"
warnings_as_errors=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --warnings-as-errors)
      warnings_as_errors=1
      shift
      ;;
    --config)
      config="$2"
      shift 2
      ;;
    *)
      echo "validate_build.unknown_arg=$1" >&2
      exit 2
      ;;
  esac
done

export DOTNET_ROOT="${DOTNET_ROOT:-/root/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

msbuild_warn=()
if [[ "$warnings_as_errors" -eq 1 ]]; then
  msbuild_warn=(-warnaserror)
fi

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

write_warning_reports() {
  $python_cmd - <<'PY'
  python - <<'PY'
from pathlib import Path
import re
from collections import Counter, defaultdict

logs = [Path('artifacts/build-debug.log'), Path('artifacts/test-debug.log')]
warn_re = re.compile(r"warning\s+([A-Z]{2,}\d+)")
proj_re = re.compile(r"\[([^\]]+\.csproj)\]")

build_count = 0
test_count = 0
codes = Counter()
projects = defaultdict(Counter)

for log in logs:
    if not log.exists():
        continue
    for line in log.read_text(errors='replace').splitlines():
        m = warn_re.search(line)
        if not m:
            continue
        code = m.group(1)
        codes[code] += 1
        pm = proj_re.search(line)
        if pm:
            projects[code][pm.group(1)] += 1
        if log.name.startswith('build-'):
            build_count += 1
        elif log.name.startswith('test-'):
            test_count += 1

baseline = Path('artifacts/warnings_baseline.txt')
with baseline.open('w', encoding='utf-8') as f:
    f.write('warnings.build.count=%d\n' % build_count)
    f.write('warnings.test.count=%d\n' % test_count)
    f.write('warnings.total.count=%d\n' % (build_count + test_count))
    f.write('warnings.top5=')
    top = codes.most_common(5)
    f.write(','.join(f'{code}:{count}' for code, count in top))
    f.write('\n')

report = Path('artifacts/warnings_report.md')
with report.open('w', encoding='utf-8') as f:
    f.write('# Warnings report\n\n')
    if not codes:
        f.write('No warnings found.\n')
    else:
        f.write('| warning code | count | projects |\n')
        f.write('|---|---:|---|\n')
        for code, count in sorted(codes.items(), key=lambda item: (-item[1], item[0])):
            proj_summary = ', '.join(f'{proj} ({n})' for proj, n in sorted(projects[code].items()))
            f.write(f'| {code} | {count} | {proj_summary} |\n')
PY
}

bash tools/codex/restore.sh
bash scripts/verify_dotnet_bootstrap.sh

run_step "build-debug" dotnet build Shoots.sln -c "$config" -v minimal --no-restore "${msbuild_warn[@]}"
run_step "test-debug" dotnet test Shoots.sln -c "$config" -v minimal --no-build --no-restore "${msbuild_warn[@]}"

write_warning_reports

cat > artifacts/build_summary.env <<SUMMARY
BUILD_OK=1
TEST_OK=1
SDK_VERSION=${required_sdk}
COMMIT=$(git rev-parse HEAD)
SUMMARY

echo "VALIDATE_BUILD_OK=1"
echo "SDK=${required_sdk}"
echo "CONFIG=${config}"
