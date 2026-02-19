# No-Spin Law

## Law

- WAITING is terminal per run.
- UI/host must never auto-rerun from WAITING.
- Resume is explicit and progress-signaled (injected decision digest or explicit override/discard mode).

## Operational rules

1. Mermaid graph remains routing authority.
2. Intake/host controls tool selection intent only.
3. Re-run attempts without progress signal are blocked.
4. Step-budget breaches halt deterministically with diagnostics.

## CI tripwires

- Runtime hang probe runs with blame-hang timeout.
- UI tests run on Windows runner.

## Identity semantics

- `WorkOrderId` = lineage identity.
- `PlanHash` = content identity.
- `PlanId` = persistence key only.
- Mermaid graph owns transitions; provider/model selection never chooses next node.
