# Validation orchestration policy

Shoots serializes validation stages when they share repo-root side effects. The runner never assumes smoke, integrity, or broader validation scripts are safe to overlap.

## Why smoke and integrity never run in parallel
- Smoke validation uses the live repo workspace and writes run artifacts that operators inspect afterward.
- Integrity validation runs `git clean -xfd -e .codex/`, clears NuGet caches when possible, and rebuilds the solution from the repo root.
- Running them together can invalidate smoke artifacts mid-run, so Shoots always serializes smoke before integrity.

## Stage classes
- Building UI: repo-mutating. Workspace impact: touches build outputs. Manual build validation can run in an isolated copied workspace.
- Running UI tests: repo-mutating. Workspace impact: touches build outputs. Manual UI test validation can run in an isolated copied workspace.
- Running smoke validation: exclusive, repo-mutating. Workspace impact: touches build outputs, rewrites artifacts. Smoke validation stays on the repo root because it verifies real workspace/run artifact behavior.
- Running integrity validation: exclusive, workspace-cleaning, repo-mutating. Workspace impact: touches build outputs, clears caches, rewrites artifacts. Integrity validation stays on the repo root because it intentionally cleans caches and restore artifacts.

## Isolated workspace mode
- Build UI project: This action can run in isolated workspace mode when the operator enables it.
- Run UI tests: This action can run in isolated workspace mode when the operator enables it.
- Smoke, integrity, and the full validation loop stay on the repo root. Isolated mode does not try to virtualize cache cleaning or artifact-verification behavior.

## Full validation sequence
- Building UI -> Running UI tests -> Running smoke validation -> Running integrity validation
- Dependencies are declared in code so the same order is enforced in UI actions, logs, and persisted orchestration artifacts.
