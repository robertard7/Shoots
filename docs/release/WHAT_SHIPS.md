# What Ships in Shoots Release Artifacts

## Canonical commands

- Windows bring-up (preflight + smoke + UI):
  - `pwsh -NoLogo -NoProfile -File scripts/start.ps1`
- Linux smoke/guards:
  - `bash scripts/start.sh`
- Windows packaging:
  - `pwsh -NoLogo -NoProfile -File scripts/package_all.ps1 -Configuration Release`

## Output locations

- Ops logs per run:
  - `artifacts/ops/<runid>/start.log`
  - `artifacts/ops/<runid>/smoke.log`
  - `artifacts/ops/<runid>/env.json`
- Release bundle:
  - `artifacts/release/<version>/shoots-release-bundle.zip`
- Release layout:
  - `artifacts/release/<version>/nuget/`
  - `artifacts/release/<version>/ui/Shoots.Ui.zip`
  - `artifacts/release/<version>/ops/`
- Runtime state:
  - `.state/trace/<workorder>.trace.json`
  - `.state/artifacts/<workorder>/`
  - `.state/chat-intake-sessions.json`
  - `.state/models.catalog.json`

## Timeout safety

Test and CI script paths include explicit timeout guards to prevent hang/spin failure modes.

## Golden flow checklist

- Run starts and enters `WAITING`.
- Re-run without resume intent is blocked.
- Resume with decision injection is explicit.
- Run reaches `COMPLETE`.
