#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

bash scripts/codex_fix_loop.sh
bash scripts/builder_loop.sh

echo "codex entrypoint completed"
