#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C
export TZ=UTC

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

mkdir -p artifacts/smoke
{
  echo "commit=$(git rev-parse HEAD)"
  echo "dotnet=$(dotnet --version)"
  echo "uname=$(uname -a)"
  echo "date_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > artifacts/smoke/version.txt

printf '%s\n' "bash scripts/codex_ci_smoke.sh" > artifacts/smoke/command.txt
{
  printf 'CONFIGURATION=%s\n' "${CONFIGURATION:-}"
  printf 'OLLAMA_HOST=%s\n' "${OLLAMA_HOST:-}"
  printf 'QDRANT_URL=%s\n' "${QDRANT_URL:-}"
  printf 'RUN_TESTS=%s\n' "${RUN_TESTS:-}"
  printf 'RUN_UI=%s\n' "${RUN_UI:-}"
  printf 'SOLUTION_PATH=%s\n' "${SOLUTION_PATH:-}"
} | sort > artifacts/smoke/env.txt

bash scripts/verify_smoke_stamp.sh
bash scripts/codex_entrypoint.sh --all
bash scripts/verify_no_flaky_tests.sh
bash scripts/verify_artifact_budgets.sh --require-run
bash scripts/verify_narration_coverage.sh --require-run
bash scripts/verify_slice_decisions.sh --require-run
bash scripts/verify_plan_evidence.sh --require-run
bash scripts/verify_provider_audit.sh --require-run

baseline_count="$(find .state/runs artifacts/builder_loop -type f -path '*/retrieval/context_pack.txt' -print 2>/dev/null | wc -l | tr -d ' ')"
if [[ "$baseline_count" == "0" ]]; then
  bash scripts/verify_context_pack_determinism.sh || true
else
  bash scripts/verify_context_pack_determinism.sh
fi
bash scripts/verify_retrieval_quality.sh
bash scripts/verify_retrieval_golden.sh
