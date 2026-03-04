#!/usr/bin/env bash
set -euo pipefail

required_commands=(
  bash
  python
  git
  awk
  sed
  grep
  find
  sort
  sha256sum
)

missing=0
for cmd in "${required_commands[@]}"; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "verify.script_dependencies.missing_command: $cmd" >&2
    missing=1
  fi
done

(( missing == 0 )) || exit 1

echo "SCRIPT_DEPENDENCY_OK=1"
echo "SCRIPT_DEPENDENCY_REQUIRED_COUNT=${#required_commands[@]}"
