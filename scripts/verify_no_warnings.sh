#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

bash scripts/validate_build.sh --warnings-as-errors

echo "NO_WARNINGS_OK=1"
