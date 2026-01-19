# Provider Contract Frozen

Providers receive only WorkOrder, RouteStep, graph hash, catalog hash, and a tool catalog snapshot.
Providers return a ToolSelectionDecision or null. Providers do not route and do not influence node transitions.
Any provider failure halts routing deterministically and records an error in the trace.
