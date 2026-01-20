# Provider Contract Frozen

Providers receive only WorkOrder, RouteStep, graph hash, catalog hash, and a tool catalog snapshot.
Providers return a ToolSelectionDecision or null. Providers do not route and do not influence node transitions.
Any provider failure halts routing deterministically and records an error in the trace.

## Failure Taxonomy

Provider failures are reported with a stable classification:

- Transport
- Timeout
- InvalidOutput
- ContractViolation
- Unknown

Failures are reported only. Classification does not alter routing, traversal, or node advancement.

## Runtime Seal

Seal Version: 0.1.0
Seal Commit: PENDING
Seal Status: Verification pending (local tests prohibited).
Post-seal changes require a new task board and explicit approval.
