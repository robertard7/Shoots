# CODEX COMMANDS

## Canonical Commands

1. `bash scripts/codex_entrypoint.sh`
2. `bash scripts/codex_entrypoint.sh --ui`
3. `bash scripts/codex_entrypoint.sh --builder`
4. `bash scripts/codex_entrypoint.sh --stubs`
5. `bash scripts/codex_entrypoint.sh --all`
6. `RUN_TESTS=1 bash scripts/maintenance.sh`
7. `CONFIGURATION=Debug bash scripts/maintenance.sh`
8. `SOLUTION_PATH=<path-to-sln> bash scripts/maintenance.sh`

## Command Matrix

| Command | Exit semantics | Artifact roots | Read first on failure |
|---|---|---|---|
| `bash scripts/codex_entrypoint.sh` | `0` success; non-zero on maintenance or builder loop failure. | `artifacts/maintenance/`, `artifacts/builder_loop/` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/builder_loop/*/run/*/narration/events.ndjson` 3) newest `artifacts/**/*.log` |
| `bash scripts/codex_entrypoint.sh --ui` | `0` success; non-zero on maintenance/build/test failure. | `artifacts/maintenance/`, `artifacts/**/*.log`, `artifacts/**/*.trx` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/**/*.log` 3) newest `artifacts/**/*.trx` |
| `bash scripts/codex_entrypoint.sh --builder` | `0` success; non-zero on builder loop failure. | `artifacts/builder_loop/`, `artifacts/golden/` | 1) `artifacts/maintenance/failure-fingerprint.json` (if present) 2) newest `artifacts/builder_loop/*/run/*/narration/events.ndjson` 3) newest `artifacts/**/*.log` |
| `bash scripts/codex_entrypoint.sh --stubs` | `0` success; non-zero on stub scan execution error. | `artifacts/stubs/` | 1) `artifacts/stubs/stubs.txt` 2) `artifacts/stubs/stubs.ndjson` |
| `bash scripts/codex_entrypoint.sh --all` | `0` success; non-zero on stub scan, maintenance, builder, or UI failure. | `artifacts/stubs/`, `artifacts/maintenance/`, `artifacts/builder_loop/`, `artifacts/golden/`, `artifacts/**/*.log`, `artifacts/**/*.trx` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/builder_loop/*/run/*/narration/events.ndjson` 3) `artifacts/stubs/stubs.txt` 4) newest `artifacts/**/*.log` |
| `RUN_TESTS=1 bash scripts/maintenance.sh` | `0` success; non-zero on restore/build/test failure. | `artifacts/maintenance/`, `artifacts/**/*.log`, `artifacts/**/*.trx` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/**/*.log` |
| `CONFIGURATION=Debug bash scripts/maintenance.sh` | `0` success; non-zero on restore/build/test failure. | `artifacts/maintenance/`, `artifacts/**/*.log` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/**/*.log` |
| `SOLUTION_PATH=<path-to-sln> bash scripts/maintenance.sh` | `0` success; non-zero on restore/build/test failure for selected solution. | `artifacts/maintenance/`, `artifacts/**/*.log` | 1) `artifacts/maintenance/failure-fingerprint.json` 2) newest `artifacts/**/*.log` |

## Artifact Roots

- `artifacts/maintenance/`
- `artifacts/stubs/`
- `artifacts/builder_loop/`
- `artifacts/golden/`
- `artifacts/**/*.log`
- `artifacts/**/*.trx`
- `.state/runs/`
