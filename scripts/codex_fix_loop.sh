#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

if RUN_TESTS=1 bash scripts/maintenance.sh; then
  exit 0
fi

fingerprint="artifacts/maintenance/failure-fingerprint.json"
if [[ -f "$fingerprint" ]]; then
  echo "Failure fingerprint: $fingerprint"
  cat "$fingerprint"
  echo
  python - <<'PY'
import json, pathlib
p = pathlib.Path('artifacts/maintenance/failure-fingerprint.json')
obj = json.loads(p.read_text())
print('errorCode:', obj.get('errorCode', '<none>'))
print('failingTestFullName:', obj.get('failingTestFullName', '<none>'))
PY
fi

latest_narration="$(ls -1t artifacts/builder_loop/*/run/*/narration/events.ndjson 2>/dev/null | head -n 1 || true)"
if [[ -n "$latest_narration" ]]; then
  echo "Newest narration log: $latest_narration"
  echo "----- narration tail (120 lines) -----"
  tail -n 120 "$latest_narration"
  echo "--------------------------------------"
  echo "----- narration refs (errorCode/stepId/artifactRefs) -----"
  python - "$latest_narration" <<'PY'
import json, pathlib, sys
for line in pathlib.Path(sys.argv[1]).read_text().splitlines()[-120:]:
    try:
        obj = json.loads(line)
    except Exception:
        continue
    data = obj.get('data') or {}
    if 'errorCode' in data or 'stepId' in data or 'artifactRefs' in data:
        print(json.dumps({'code': obj.get('code'), 'phase': obj.get('phase'), 'errorCode': data.get('errorCode'), 'stepId': data.get('stepId'), 'artifactRefs': data.get('artifactRefs')}, separators=(',', ':')))
PY
  echo "---------------------------------------------------------"
fi

latest_log="$(ls -1t artifacts/maintenance/tests-*.log 2>/dev/null | head -n 1 || true)"
if [[ -z "$latest_log" ]]; then
  echo "No tests log found under artifacts/maintenance/."
  exit 1
fi

echo "Newest tests log: $latest_log"
echo "----- failing tests (best effort) -----"
failing_tests="$(sed -nE 's#^\s*Failed\s+([^[:space:]]+).*#\1#p' "$latest_log" | sort -u || true)"
if [[ -n "$failing_tests" ]]; then
  printf '%s\n' "$failing_tests"
else
  echo "<no explicit failing test names found>"
fi

echo "----- tail (120 lines) -----"
tail -n 120 "$latest_log"
echo "----------------------------"

exit 1
