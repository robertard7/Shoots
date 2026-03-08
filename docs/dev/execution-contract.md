# UI Runtime Execution Contract (`ui-runtime-v1`)

This document freezes the canonical execution shapes used across the Shoots UI execution stack.

## Contract marker
- `ExecutionContract.Version`: `ui-runtime-v1`

## Canonical models
- `ExecutionRequest`
- `ExecutionPlan`
- `ExecutionStep`
- `ExecutionResult`
- `ToolInvocationRecord`
- `ExecutionEvidence`

Implementation source:
- `ui/Shoots.Ui/Builder/ExecutionContracts.cs`

## Drift policy
Any shape change to the canonical models must include:
1. Contract snapshot test updates.
2. Explicit reviewer signoff.
3. Version bump if not backward-compatible.
