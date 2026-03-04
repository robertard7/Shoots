#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

resolve_latest_run() {
  local candidate=""
  candidate="$(find .state/runs -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort | tail -n 1 || true)"
  if [[ -z "$candidate" ]]; then
    candidate="$(find artifacts/builder_loop -type d -path '*/run/*' ! -path '*/run/*/*' -print 2>/dev/null | sort | tail -n 1 || true)"
  fi
  printf '%s' "$candidate"
}

require_run=0
if [[ "${1:-}" == "--require-run" ]]; then
  require_run=1
  shift
fi

run_dir="${1:-$(resolve_latest_run)}"
if [[ -z "$run_dir" || ! -d "$run_dir" ]]; then
  if (( require_run == 1 )); then
    fail "verify.provider.audit.missing_artifacts" "no run directory found"
  fi
  echo "verify.provider.audit.run_missing: no run directory found"
  exit 0
fi

request="$run_dir/provider/request.json"
result="$run_dir/provider/result.json"
narration="$run_dir/narration/events.ndjson"

[[ -s "$request" && -s "$result" ]] || fail "verify.provider.audit.missing_artifacts" "missing provider request/result artifacts"
[[ -s "$narration" ]] || fail "verify.provider.audit.missing_narration" "missing narration events"

python - "$narration" <<'PY'
import json,sys
path=sys.argv[1]
codes=[json.loads(x).get('code','') for x in open(path,encoding='utf-8').read().splitlines() if x.strip()]
required=['provider.resolve.start','provider.resolve.end','provider.invoke.start','provider.invoke.end']
missing=[c for c in required if c not in codes]
if missing:
    print('verify.provider.audit.missing_narration: '+','.join(missing))
    raise SystemExit(1)
pos={c:codes.index(c) for c in required}
if not (pos['provider.resolve.start'] < pos['provider.resolve.end'] < pos['provider.invoke.start'] < pos['provider.invoke.end']):
    print('verify.provider.audit.order: invalid provider narration order')
    raise SystemExit(1)
print('verify.provider.audit.ok')
PY
