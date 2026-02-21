# AI Builder Quickstart

## 1) One-command bring-up (preferred)

### Windows (preflight + smoke + UI)

```powershell
powershell -ExecutionPolicy Bypass -File scripts/start.ps1
```

This runs `first_run_check.ps1`, runs host smoke, launches UI, and writes ops logs under `artifacts/ops/<runid>/`.

### Linux (guards + smoke)

```bash
bash scripts/start.sh
```

This runs topology/version/provider guards and the host smoke harness (no Windows UI).

## 2) Manual flow (Windows)

```powershell
./scripts/first_run_check.ps1
./scripts/run_host_smoke.ps1
./scripts/run_ui.ps1 -Configuration Release
```

## 3) Quick Start flow in UI

1. Open **Chat Intake**.
2. Enter intent text.
3. Select a model.
4. Click **Quick Start**.

## 4) WAITING -> Resume flow

When a decision gate returns WAITING:

- Waiting panel shows gate/node/policy/allowed next nodes.
- Pick tool and optional JSON bindings.
- Choose run mode.
- Click **Resume with selection**.

## 5) Verify deterministic `.state` outputs

- `.state/trace/<workorder>.trace.json`
- `.state/artifacts/<workorder>/`
- `.state/chat-intake-sessions.json`
- `.state/models.catalog.json`

Use `./scripts/clean_state.ps1 -KeepModels` to clear state while preserving model catalog.

## 6) Packaging

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package_all.ps1 -Configuration Release
```

This produces release outputs under `artifacts/release/<version>/` including NuGet packages, UI zip, and ops/smoke logs (if present).
