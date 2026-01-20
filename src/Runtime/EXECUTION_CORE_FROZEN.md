# Execution Core Frozen

The runtime execution core is frozen. Mermaid-derived RouteRules are the sole routing authority.
Tool selection is the only provider responsibility; tool choice never advances the graph.
Provider failures halt routing and are recorded in the routing trace.

## Replay Inspection

Replay summaries are derived from routing traces to aid inspection (provider decisions, failures, tool selections).
Summaries are read-only views that do not affect determinism, routing, or provider behavior.
