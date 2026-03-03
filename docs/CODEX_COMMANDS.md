# CODEX COMMANDS

## Canonical Commands

1. `bash scripts/codex_entrypoint.sh`
2. `bash scripts/codex_entrypoint.sh --ui`
3. `bash scripts/codex_entrypoint.sh --builder`
4. `bash scripts/codex_entrypoint.sh --all`
5. `RUN_TESTS=1 bash scripts/maintenance.sh`
6. `CONFIGURATION=Debug bash scripts/maintenance.sh`
7. `SOLUTION_PATH=<path-to-sln> bash scripts/maintenance.sh`

## Expected Artifacts

### `bash scripts/codex_entrypoint.sh`
- `artifacts/maintenance/failure-fingerprint.json` (on failure)
- `artifacts/builder_loop/**`

### `bash scripts/codex_entrypoint.sh --ui`
- `artifacts/maintenance/failure-fingerprint.json` (on failure)
- `artifacts/**/*.log`
- `artifacts/**/*.trx`

### `bash scripts/codex_entrypoint.sh --builder`
- `artifacts/builder_loop/**`
- `artifacts/golden/**`

### `bash scripts/codex_entrypoint.sh --all`
- `artifacts/maintenance/**`
- `artifacts/builder_loop/**`
- `artifacts/golden/**`
- `artifacts/**/*.log`
- `artifacts/**/*.trx`

### `RUN_TESTS=1 bash scripts/maintenance.sh`
- `artifacts/maintenance/failure-fingerprint.json` (on failure)
- `artifacts/**/*.log`
- `artifacts/**/*.trx`

### `CONFIGURATION=Debug bash scripts/maintenance.sh`
- `artifacts/maintenance/failure-fingerprint.json` (on failure)
- `artifacts/**/*.log`

### `SOLUTION_PATH=<path-to-sln> bash scripts/maintenance.sh`
- `artifacts/maintenance/failure-fingerprint.json` (on failure)
- `artifacts/**/*.log`
