Startup Walkthrough (First Run)
===============================

Purpose
-------
This walkthrough is a literal script for first-run startup. Follow each step in order.

Entry Path: Start Something New
-------------------------------
1. Click "New Project".
2. In the Startup tab, click "Start something new".
3. In Startup Chat, answer the prompt "Question: What primary language should the project use?" with one of the listed languages.
4. Answer the prompt "Question: Project name (optional). Reply with a name or \"skip\"." with either a name or "skip".
5. Answer the prompt "Question: Provide a 1–2 sentence description." with a single sentence or two sentences.
6. Verify the "Summary:" block lists Provider, Mode, Language, Project name, Sandbox path, and Files.
7. Reply "confirm" to create the project.
8. Verify the chat reports the project path and README.intent.md creation.

Entry Path: Continue an Existing Project
----------------------------------------
1. Click "New Project".
2. In the Startup tab, click "Continue an existing project".
3. In Startup Chat, answer the prompt "Question: Provide the path to the existing project." with a valid folder path.
4. Verify any warning about being outside the sandbox (if applicable).
5. Reply "confirm" to attach in read-only mode.
6. Verify the chat reports read-only attach and no file changes.

Entry Path: Just Explore an Idea
--------------------------------
1. Click "New Project".
2. In the Startup tab, click "Just explore an idea".
3. Verify the chat shows "Explore mode active. Type \"promote\" to start a project."
4. Reply "promote" to re-enter the startup flow.
5. Continue with the Start Something New path from step 3.


Deterministic startup commands
------------------------------
- Restore: `bash tools/codex/restore.sh`
- Determinism pipeline: `bash scripts/validate_determinism.sh --skip-backends`
- Run UI (Linux/macOS shell): `bash scripts/run_ui_local.sh`
- Run UI (Windows PowerShell): `pwsh -File .\scripts\run_ui_local.ps1`
- Repo snapshot: `bash scripts/dev_snapshot.sh`

Failure triage matrix
---------------------
- Restore failures: run `bash tools/codex/restore.sh`; inspect `dotnet --info` output and SDK install.
- Smoke failures: run `bash scripts/smoke_runner.sh --skip-backends`; inspect `artifacts/smoke/latest_summary.env`.
- Replay failures: run `bash scripts/replay_runner.sh "$RUN_DIR"`; if mismatch persists run `bash scripts/replay_diff.sh "$RUN_DIR"` for stable diff sections.
- Fixture drift: run `bash scripts/verify_fixture_integrity.sh`; for intentional updates use `ALLOW_FIXTURE_UPDATE=1 bash scripts/update_fixture_integrity.sh`.
- Backend unreachable: run `bash scripts/smoke_backends.sh --ollama "$OLLAMA_HOST" --qdrant "$QDRANT_URL"` or `bash scripts/smoke_ui_backends.sh`.
