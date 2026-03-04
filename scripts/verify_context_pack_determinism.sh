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

run_dir="${1:-$(resolve_latest_run)}"
if [[ -z "$run_dir" || ! -d "$run_dir" ]]; then
  echo "verify.context_pack.run_missing: no run directory found"
  exit 0
fi

run_identity="$run_dir/identity.json"
replay_identity="$run_dir/replay/identity.json"
if [[ ! -f "$run_identity" || ! -f "$replay_identity" ]]; then
  echo "verify.context_pack.run_missing: identity pairing artifacts missing"
  exit 0
fi

pair_ok="$(python - <<'PY' "$run_identity" "$replay_identity"
import json,sys
run=json.load(open(sys.argv[1]))
replay=json.load(open(sys.argv[2]))
print('1' if replay.get('replayOfRunId','')==run.get('runId','') else '0')
PY
)"
if [[ "$pair_ok" != "1" ]]; then
  fail "verify.context_pack.run_pairing_invalid" "replay identity does not reference run identity"
fi

cmp_file() {
  local code="$1"
  local left="$2"
  local right="$3"
  [[ -f "$left" && -f "$right" ]] || fail "${code}.missing" "missing compared files: $left $right"
  cmp -s "$left" "$right" || fail "$code" "diverged: $left vs $right"
}

cmp_file "verify.context_pack.diverged" "$run_dir/retrieval/context_pack.txt" "$run_dir/replay/retrieval/context_pack.txt"
cmp_file "verify.retrieval_hits.diverged" "$run_dir/retrieval/hits.ndjson" "$run_dir/replay/retrieval/hits.ndjson"
cmp_file "verify.retrieval_scoring.diverged" "$run_dir/retrieval/scoring.ndjson" "$run_dir/replay/retrieval/scoring.ndjson"

echo "verify.context_pack_determinism.ok"
