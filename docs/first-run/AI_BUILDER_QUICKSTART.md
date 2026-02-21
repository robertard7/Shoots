# AI Builder Quickstart

## 1. Clone and preflight (Windows)

```powershell
git clone <repo-url>
cd Shoots
./scripts/first_run_check.ps1
```

## 2. Launch UI

```powershell
./scripts/run_ui.ps1 -Configuration Release
```

## 3. Quick Start flow

1. Open **Chat Intake**.
2. Enter intent text.
3. Select a model (or click **Refresh** / **Reset catalog** in the Model row if catalog errors are shown).
4. Click **Quick Start** (creates WorkOrder, previews plan, runs).

## 4. WAITING flow

When a decision gate returns WAITING:

- Waiting panel shows gate/node/policy/allowed-next-nodes.
- Pick a tool and optional JSON bindings.
- Choose run mode.
- Click **Resume with selection**.

## 5. Confirm no auto-rerun

- WAITING state remains idle until you click resume.
- Re-running without progress emits blocked host marker in trace.

## 6. Trace and artifacts

- Use Trace tab to inspect runtime markers.
- Use Artifacts tab for plan artifact listing/path copy.

## Smoke checklist (expected states)

- Quick Start creates a new session row with WorkOrderId and status transition from Draft -> PlanReady.
- WAITING displays gate details and does not continue until Resume is clicked.
- Resume with selection updates session status and appends host trace markers.
- Trace tab shows decision/host markers; Artifacts tab shows artifact list/paths.


## Verify deterministic `.state/` outputs
- In **Trace** tab, click **Copy Trace Path** and confirm the copied value points to `.state/trace/<workorder>.trace.json`.
- In **Artifacts** tab, click **Copy path** and confirm it points to `.state/artifacts/<workorder>/` (folder exists even if empty).
- Session list state is persisted in `.state/chat-intake-sessions.json`.
- Model catalog is read from `.state/models.catalog.json` (created from template on first run).


## Optional reset
Use `./scripts/clean_state.ps1 -KeepModels` to clear trace/artifact/session state between smoke runs.


## 7. Host smoke harness (cross-platform)
```bash
./scripts/run_host_smoke.sh
```
```powershell
./scripts/run_host_smoke.ps1
```
