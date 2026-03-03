# CODEX COMMANDS

## Canonical Commands

Blessed command for Codex + CI:

- `bash scripts/codex_entrypoint.sh --all`

Additional supported commands:

1. `bash scripts/codex_entrypoint.sh`
2. `bash scripts/codex_entrypoint.sh --ui`
3. `bash scripts/codex_entrypoint.sh --builder`
4. `bash scripts/codex_entrypoint.sh --stubs`
5. `RUN_TESTS=1 bash scripts/maintenance.sh`
6. `CONFIGURATION=Debug bash scripts/maintenance.sh`
7. `SOLUTION_PATH=<path-to-sln> bash scripts/maintenance.sh`
8. `bash scripts/verify_no_blocking_stubs.sh`
9. `bash scripts/verify_diagnostics_order.sh`

## Command Matrix

| Command | Exit semantics | Artifact roots | Read first on failure |
|---|---|---|---|
| `bash scripts/codex_entrypoint.sh` | `0` success; non-zero on maintenance or builder loop failure. | `artifacts/maintenance/`, `artifacts/builder_loop/` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `.state/runs/**/narration/events.ndjson` (fallback: `artifacts/builder_loop/*/run/*/narration/events.ndjson`) 3) `artifacts/stubs/triage.md` 4) newest `artifacts/**/*.log` |
| `bash scripts/codex_entrypoint.sh --ui` | `0` success; non-zero on maintenance/build/test failure. | `artifacts/maintenance/`, `artifacts/**/*.log`, `artifacts/**/*.trx` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `.state/runs/**/narration/events.ndjson` (fallback: `artifacts/builder_loop/*/run/*/narration/events.ndjson`) 3) `artifacts/stubs/triage.md` 4) newest `artifacts/**/*.log` |
| `bash scripts/codex_entrypoint.sh --builder` | `0` success; non-zero on builder loop failure. | `artifacts/builder_loop/`, `artifacts/golden/` | 1) `artifacts/maintenance/failure-fingerprint.json` (if present) 2) newest `.state/runs/**/narration/events.ndjson` (fallback: `artifacts/builder_loop/*/run/*/narration/events.ndjson`) 3) `artifacts/stubs/triage.md` 4) newest `artifacts/**/*.log` |
| `bash scripts/codex_entrypoint.sh --stubs` | `0` success; non-zero on stub scan execution error. | `artifacts/stubs/` | 1) `artifacts/stubs/triage.md` 2) `artifacts/stubs/stubs.ndjson` |
| `bash scripts/codex_entrypoint.sh --all` | `0` success; non-zero on stub scan, maintenance, builder, or UI failure. | `artifacts/stubs/`, `artifacts/maintenance/`, `artifacts/builder_loop/`, `artifacts/golden/`, `artifacts/**/*.log`, `artifacts/**/*.trx` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `.state/runs/**/narration/events.ndjson` (fallback: `artifacts/builder_loop/*/run/*/narration/events.ndjson`) 3) `artifacts/stubs/triage.md` 4) newest `artifacts/**/*.log` |
| `RUN_TESTS=1 bash scripts/maintenance.sh` | `0` success; non-zero on restore/build/test failure. | `artifacts/maintenance/`, `artifacts/**/*.log`, `artifacts/**/*.trx` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/**/*.log` |
| `CONFIGURATION=Debug bash scripts/maintenance.sh` | `0` success; non-zero on restore/build/test failure. | `artifacts/maintenance/`, `artifacts/**/*.log` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/**/*.log` |
| `SOLUTION_PATH=<path-to-sln> bash scripts/maintenance.sh` | `0` success; non-zero on restore/build/test failure for selected solution. | `artifacts/maintenance/`, `artifacts/**/*.log` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/**/*.log` |
| `bash scripts/verify_no_blocking_stubs.sh` | `0` when no bucket-1 stubs; non-zero when bucket-1 stubs exist. | `artifacts/stubs/` | 1) `artifacts/stubs/bucket-1.ndjson` 2) `artifacts/stubs/triage.md` 3) `artifacts/stubs/stubs.txt` |
| `bash scripts/verify_diagnostics_order.sh` | `0` when codex diagnostics order is canonical; non-zero when drift is detected. | `scripts/codex_entrypoint.sh` | 1) update `print_diagnostics` ordering in `scripts/codex_entrypoint.sh` |

## Artifact Roots

- `artifacts/maintenance/`
- `artifacts/stubs/`
- `artifacts/builder_loop/`
- `artifacts/golden/`
- `artifacts/**/*.log`
- `artifacts/**/*.trx`
- `.state/runs/`
