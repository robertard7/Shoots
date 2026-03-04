#!/usr/bin/env bash
set -euo pipefail

scripts=(
  scripts/smoke_runner.sh
  scripts/smoke_backends.sh
  scripts/smoke_ui_backends.sh
  scripts/validate_determinism.sh
  scripts/verify_hash_contract.sh
  scripts/verify_fixture_integrity.sh
  scripts/verify_trace_schema.sh
  scripts/verify_trace_contract.sh
  scripts/verify_manifest_contract.sh
  scripts/verify_environment_schema.sh
  scripts/verify_artifact_bounds.sh
  scripts/verify_smoke_artifacts.sh
  scripts/collect_failure_bundle.sh
  scripts/verify_failure_bundle_schema.sh
)

status=0
for script in "${scripts[@]}"; do
  if [[ ! -f "$script" ]]; then
    echo "verify.portability.missing_script: $script" >&2
    status=1
    continue
  fi

  if [[ "$(head -n1 "$script")" != "#!/usr/bin/env bash" ]]; then
    echo "verify.portability.bad_shebang: $script" >&2
    status=1
  fi

  if ! rg -q '^set -euo pipefail$' "$script"; then
    echo "verify.portability.missing_strict_mode: $script" >&2
    status=1
  fi

  if rg -q -- '--files0-from|sed -z|sort -z' "$script"; then
    echo "verify.portability.gnu_only_flag: $script" >&2
    status=1
  fi
done

if [[ "$status" -ne 0 ]]; then
  echo "SCRIPTS_PORTABILITY_OK=0"
  exit 1
fi

echo "SCRIPTS_PORTABILITY_OK=1"
echo "SCRIPTS_PORTABILITY_COUNT=${#scripts[@]}"
