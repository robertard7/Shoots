# CONTEXT BUDGETS

Canonical deterministic budget table.

| Domain | Fields | Defaults / Source |
|---|---|---|
| Slice caps | `maxFiles`, `maxTotalBytes`, `maxBytesPerFile`, `lineCap` | From fixture step args and `RepoSliceRequest` normalization |
| Retrieval caps | `ContextBudget.MaxBytes`, `MaxLines`, `MaxFiles`, `MaxTokensEstimate`, `MaxFileBytes`, `MaxLinesPerFile` | Defaults in retrieval contracts (`120000`, `2000`, `12`, `null`, `12000`, `400`) |
| Context pack format | Header order (`runId`, `planHash`, `retrievalHash`, budget fields), per-hit ordering, tie-break fields | Deterministic format in `RetrievalService.BuildContextPack` |
| Synthesis caps | `MaxSteps`, `MaxArgsBytes`, `MaxTotalPlanBytes` | Defaults in plan synthesis contracts (`3`, `4096`, `64000`) |
| Artifact ceilings | Step envelopes and retrieval artifacts | Enforced via `verify_artifact_budgets.sh`, `verify_step_envelopes.sh` |

## Consistency verifier

Use `bash scripts/verify_budget_consistency.sh` to check fixture and golden artifacts remain within declared budget ceilings.
