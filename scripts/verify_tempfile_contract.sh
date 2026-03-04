#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_tempfile_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.tempfile.run_dir_missing: $run_dir" >&2; exit 64; }

scan_roots=("$run_dir")
[[ -d artifacts ]] && scan_roots+=("artifacts")
[[ -d .state ]] && scan_roots+=(".state")
[[ -d runs ]] && scan_roots+=("runs")

pattern='(\.tmp$|\.temp$|~$|\.swp$|\.swo$|\.bak$|\.orig$)'

hits="$(find "${scan_roots[@]}" -type f | rg -n "$pattern" -S || true)"
if [[ -n "$hits" ]]; then
  echo "verify.tempfile.unexpected_files:" >&2
  echo "$hits" >&2
  exit 1
fi

echo "TEMPFILE_CONTRACT_OK=1"
echo "TEMPFILE_COUNT=0"
