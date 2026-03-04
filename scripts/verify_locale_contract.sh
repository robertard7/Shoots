#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_locale_contract.sh <RUN_DIR>
USAGE
}

if [[ $# -ne 1 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
[[ -d "$run_dir" ]] || { echo "verify.locale.run_dir_missing: $run_dir" >&2; exit 64; }

effective_locale="${LC_ALL:-${LANG:-C}}"
if [[ "$effective_locale" != "C" && "$effective_locale" != "C.UTF-8" ]]; then
  echo "verify.locale.bad_effective_locale: $effective_locale" >&2
  exit 1
fi

env_path="$run_dir/environment.json"
if [[ -f "$env_path" ]]; then
  env_locale="$(python - "$env_path" <<'PY'
import json, pathlib, sys
obj = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
print(obj.get('locale', ''))
PY
)"
  if [[ -n "$env_locale" && "$env_locale" != "$effective_locale" ]]; then
    echo "verify.locale.environment_mismatch: $env_locale != $effective_locale" >&2
    exit 1
  fi
fi

echo "LOCALE_CONTRACT_OK=1"
echo "LOCALE_USED=$effective_locale"
