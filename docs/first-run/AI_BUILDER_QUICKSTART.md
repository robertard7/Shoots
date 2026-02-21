# AI Builder Quickstart

## 1. Clone and launch (Windows)

```powershell
git clone <repo-url>
cd Shoots
./scripts/run_ui.ps1 -Configuration Release
```

## 2. Quick Start flow

1. Open **Chat Intake**.
2. Enter intent text.
3. Click **Quick Start** (creates WorkOrder, previews plan, runs).

## 3. WAITING flow

When a decision gate returns WAITING:

- Waiting panel shows gate/node/policy/allowed-next-nodes.
- Pick a tool and optional JSON bindings.
- Choose run mode.
- Click **Resume with selection**.

## 4. Confirm no auto-rerun

- WAITING state remains idle until you click resume.
- Re-running without progress emits blocked host marker in trace.

## 5. Trace and artifacts

- Use Trace tab to inspect runtime markers.
- Use Artifacts tab for plan artifact listing/path copy.
