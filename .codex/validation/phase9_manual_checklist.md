# Phase 9 Manual Operator Checklist

## Refresh Flow
- Launch Shoots and select model `qwen2.5:0.5b-instruct`.
- Click **Refresh**.
- Confirm Working Now panel activates and shows stage progression.
- Confirm status reaches **Waiting on provider** and then terminal **Completed** or **Failed**.

## Quick Demo Flow
- Click **Quick Demo**.
- Confirm ordered stages progress through:
  - Create project
  - Plan run
  - Execute tools
  - Host run
  - Verification
  - Completed/Failed

## Waiting-State Check
- During a long-running stage, confirm waiting hint appears when progress is stale.
- Confirm waiting hint clears after a new narration/stage/step update.

## Failure-State Check
- Force or observe a failed run.
- Confirm failure diagnostics show exception/message/first frame and log paths.
- Confirm failure state remains visible for completion hold duration.

## Latest Run Folder Check
- After a successful run, confirm latest run path is shown in panel.
- Click **Open Latest Run Folder** and verify folder opens.

## Timeline Toggle Check
- Toggle **Show full timeline** off and confirm only active + recent steps remain.
- Toggle on and confirm full ordered timeline is restored.
