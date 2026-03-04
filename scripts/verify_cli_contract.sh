#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "verify.cli_contract.dotnet_missing" >&2
  exit 127
fi

runner_help="$(dotnet run -c Release --project src/Runtime/Shoots.Runtime.Runner -- --help 2>&1 || true)"
if [[ -z "$runner_help" ]]; then
  echo "verify.cli_contract.runner_help_empty" >&2
  exit 1
fi

dotnet_info="$(dotnet --info 2>&1)"
smoke_help="$(bash scripts/smoke_runner.sh --help 2>&1)"

declare -a required_patterns=(
  "Usage"
  "--scenario"
  "--project"
  "--out"
)

for pattern in "${required_patterns[@]}"; do
  if ! grep -Fq -- "$pattern" <<<"$runner_help"; then
    echo "verify.cli_contract.runner_help_missing: $pattern" >&2
    exit 1
  fi
done

normalize() {
  sed -E 's/[[:space:]]+$//' | sed '/^[[:space:]]*$/d' | tr -d '\r'
}

cli_hash="$(
  {
    echo "## dotnet --info"
    printf '%s\n' "$dotnet_info"
    echo "## runner --help"
    printf '%s\n' "$runner_help"
    echo "## smoke_runner --help"
    printf '%s\n' "$smoke_help"
  } | normalize | sha256sum | awk '{print $1}'
)"

echo "CLI_CONTRACT_OK=1"
echo "CLI_HASH=$cli_hash"
