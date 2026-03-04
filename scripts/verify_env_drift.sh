#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/verify_env_drift.sh <RUN_DIR> [BASELINE_RUN_DIR]
USAGE
}

if [[ $# -lt 1 || $# -gt 2 ]]; then
  usage >&2
  exit 64
fi

run_dir="$1"
baseline_run_dir="${2:-}"
[[ -d "$run_dir" ]] || { echo "verify.env_drift.run_dir_missing: $run_dir" >&2; exit 64; }

current_env="$run_dir/environment.json"
[[ -f "$current_env" ]] || { echo "verify.env_drift.environment_missing: $current_env" >&2; exit 64; }

if [[ -z "$baseline_run_dir" && -f artifacts/smoke/latest_summary.env ]]; then
  # shellcheck disable=SC1091
  source artifacts/smoke/latest_summary.env
  if [[ -n "${RUN_DIR:-}" && "${RUN_DIR}" != "$run_dir" ]]; then
    baseline_run_dir="$RUN_DIR"
  fi
fi

drift_fields="none"
if [[ -n "$baseline_run_dir" ]]; then
  baseline_env="$baseline_run_dir/environment.json"
  [[ -f "$baseline_env" ]] || { echo "verify.env_drift.baseline_environment_missing: $baseline_env" >&2; exit 64; }

  drift_fields="$(python - "$current_env" "$baseline_env" <<'PY'
import json, pathlib, re, sys
cur = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
base = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8'))

fields = []
for key in ("os", "kernel"):
    if str(cur.get(key, "")) != str(base.get(key, "")):
        fields.append(key)

def sdk_version(dotnet_info: str) -> str:
    m = re.search(r"Version:\s*([0-9]+(?:\.[0-9]+){1,3})", dotnet_info)
    return m.group(1) if m else ""

if sdk_version(str(cur.get("dotnet_info", ""))) != sdk_version(str(base.get("dotnet_info", ""))):
    fields.append("dotnet_sdk")

if (cur.get("locale") or "") != (base.get("locale") or ""):
    fields.append("locale")
if (cur.get("timezone") or "") != (base.get("timezone") or ""):
    fields.append("timezone")

print(','.join(sorted(set(fields))) if fields else 'none')
PY
)"
fi

echo "ENV_DRIFT_OK=1"
echo "ENV_DRIFT_FIELDS=$drift_fields"
echo "ENV_DRIFT_RUN_DIR=$run_dir"
if [[ -n "$baseline_run_dir" ]]; then
  echo "ENV_DRIFT_BASELINE_RUN_DIR=$baseline_run_dir"
fi
