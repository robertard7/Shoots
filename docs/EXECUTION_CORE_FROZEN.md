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
Seal Status: Verification pending. (See docs/VERIFICATION_POLICY.md)
Post-seal changes require a new task board and explicit approval.

## Decision Gates Law

Decision gates are policy-driven and deterministic via `RouteRule.DecisionPolicy`:

- `Hard` (default): missing decision transitions routing to `Waiting` and returns control to host/UI.
- `Bypass`: uses `RouteRule.FallbackToolSelection` when configured; if fallback is missing, routing behaves as `Hard`.
- `Error`: missing decision halts deterministically with `route_decision_required`.

`Waiting` is terminal for the current run and must not spin the routing loop.

Routing loop enforces a deterministic step budget (`256` default, configurable per `RoutingLoop`) and halts with `route_step_budget_exceeded` when exceeded.

### Policy Examples

Hard (default):

```json
{
  "NodeId": "select",
  "Intent": "SelectTool",
  "Owner": "Ai",
  "AllowedOutputKind": "tool.selection",
  "NodeKind": "Start",
  "AllowedNextNodes": ["terminate"],
  "DecisionPolicy": "Hard"
}
```

Bypass with fallback:

```json
{
  "NodeId": "select",
  "Intent": "SelectTool",
  "Owner": "Ai",
  "AllowedOutputKind": "tool.selection",
  "NodeKind": "Start",
  "AllowedNextNodes": ["terminate"],
  "DecisionPolicy": "Bypass",
  "FallbackToolSelection": {
    "ToolId": { "Value": "tools.echo" },
    "Bindings": { "name": "alpha" }
  }
}
```

Error:

```json
{
  "NodeId": "select",
  "Intent": "SelectTool",
  "Owner": "Ai",
  "AllowedOutputKind": "tool.selection",
  "NodeKind": "Start",
  "AllowedNextNodes": ["terminate"],
  "DecisionPolicy": "Error"
}
```

### Host Flow

1. Execute runtime once.
2. If `ExecutionEnvelope.Waiting` is populated, present `DecisionPromptKey` and policy context in UI.
3. Collect decision and re-execute from persisted envelope state.
