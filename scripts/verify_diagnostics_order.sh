#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

script="scripts/codex_entrypoint.sh"

line_fp="$(rg -n "failure-fingerprint.json" "$script" | head -n1 | cut -d: -f1)"
line_narr="$(rg -n "latest_narration" "$script" | head -n1 | cut -d: -f1)"
line_summary="$(rg -n "latest_run_summary" "$script" | head -n1 | cut -d: -f1)"
line_stubs="$(rg -n "artifacts/stubs/triage.md" "$script" | head -n1 | cut -d: -f1)"
line_log="$(rg -n "latest_test_log" "$script" | head -n1 | cut -d: -f1)"

if [[ -z "$line_fp" || -z "$line_narr" || -z "$line_summary" || -z "$line_stubs" || -z "$line_log" ]]; then
  echo "diagnostics markers missing"
  exit 1
fi

if (( line_fp < line_narr && line_narr < line_summary && line_summary < line_stubs && line_stubs < line_log )); then
  echo "diagnostics order verified"
  exit 0
fi

echo "diagnostics order invalid: fp=$line_fp narr=$line_narr summary=$line_summary stubs=$line_stubs log=$line_log"
exit 1
