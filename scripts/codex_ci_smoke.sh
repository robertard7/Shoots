#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C
export TZ=UTC

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

bash scripts/codex_entrypoint.sh --all
bash scripts/verify_no_flaky_tests.sh
bash scripts/verify_artifact_budgets.sh --require-run
bash scripts/verify_narration_coverage.sh --require-run
