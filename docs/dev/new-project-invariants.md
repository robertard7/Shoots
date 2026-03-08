# New Project Invariants

`Start New Project` must create the following deterministically under:

`%LOCALAPPDATA%\Shoots.UI\workspaces\<CreatedUtc:yyyyMMdd-HHmmssZ>_<ProjectId>`

Required files and folders:

- `project.json`
- `plans/`
- `runs/`
- `artifacts/`
- `notes/`

Behavioral invariants:

- UI emits trace `BEGIN StartNewProject run_id=<id>` and `END StartNewProject run_id=<id> (result=...)` for every click.
- Creation is atomic via temp workspace under `.tmp/` and move to final path.
- On failure, temp workspace is cleaned and UI shows a visible error message plus `ui.log` path.
- On success, `CurrentProject` is set and recents are updated in `%LOCALAPPDATA%\Shoots.UI\recent-projects.json`.
