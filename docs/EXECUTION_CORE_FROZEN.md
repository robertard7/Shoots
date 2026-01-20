# Execution Core Frozen

The runtime execution core is frozen. Mermaid-derived RouteRules are the sole routing authority.
Tool selection is the only provider responsibility; tool choice never advances the graph.
Provider failures halt routing and are recorded in the routing trace.

## Replay Inspection

Replay summaries are derived from routing traces to aid inspection (provider decisions, failures, tool selections).
Summaries are read-only views that do not affect determinism, routing, or provider behavior.

## Error Correlation

Runtime errors include derived correlation identifiers that match trace entries.
Correlation identifiers are deterministic and do not introduce new routing inputs.

## Runtime Seal

Seal Version: 0.1.0
Seal Commit: PENDING
Seal Status: Verification pending.
Post-seal changes require a new task board and explicit approval.
