# Semantic Reuse Strategy

Shoots uses semantic reuse only for operator-visible suggestions. Deterministic run artifacts remain the authority for execution, validation, replay, repair, promotion, and release readiness.

## Approved Qdrant Uses
- Surface similar planning hints from prior generated outputs, validation failures, and repair outcomes.
- Retrieve similar validation failures.
- Retrieve prior repair bundles and repair promotions for similar failures.
- Retrieve similar provider diagnostics and retry outcomes.
- Retrieve replay divergence episodes.
- Retrieve baseline drift and regression summaries.

## Deferred Or Rejected Uses
- Qdrant does not decide execution results.
- Qdrant does not override validation outcomes or release readiness.
- Qdrant does not silently mutate code, plans, repairs, or repair bundles.
- Qdrant does not replace exact linked history or current run artifacts.

## Deterministic Safeguards
1. Current run artifacts are read first.
2. Exact linked history is read second.
3. Semantic suggestions are ranked after exact linkage and exact stage/failure matches.
4. If Qdrant is unavailable, Shoots falls back to deterministic local ranking without changing behavior.
5. Similar cases may suggest references, never auto-apply code or repair diffs.
6. Outcome learning and operator playbooks are derived only from recorded validation or repair artifacts.
7. Playbooks remain read-only operator guidance. They never trigger repairs, promotions, or baselines automatically.