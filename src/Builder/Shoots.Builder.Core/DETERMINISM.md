# Deterministic Planner Rules

The builder planner **must** be pure and deterministic. It may only use the inputs
provided by `BuildRequest`, `IRuntimeServices`, and `IDelegationPolicy` without
observing any external state.

## Determinism Checklist
- No filesystem reads or writes
- No environment variable reads
- No clock/time usage
- No randomness
- No network access
- No global/static mutable state
- Planner requires `BuildRequest.WorkOrder` and `BuildRequest.Args["plan.graph"]` Mermaid graph input and fails when missing.
- Planner validates routing completeness (single start node, terminal terminate intent, all intents present).

## Current Planner Dependencies
- `IRuntimeServices` for command metadata only
- `IDelegationPolicy` for deterministic authority decisions

## Known Violations
- None (planner implementation performs no IO, time, env, random, or network access)

## Plan Hash Inputs
The plan hash must include only semantic inputs, in stable order:
- `BuildRequest.CommandId` (normalized)
- `BuildContract.Version`
- `BuildRequest.WorkOrder` fields (id, original request, goal, constraints, success criteria)
- `DelegationAuthority` fields (provider id, kind, policy id, allows delegation)
- `BuildRequest.Args` ordered by key (case-insensitive), normalized key/value tokens
- `BuildRequest.Args["plan.graph"]` Mermaid graph text (normalized)
- `BuildRequest.RouteRules` (node id, intent, owner, allowed output kind) ordered by node id
- `BuildStep` list (id + description) in order
- `AiBuildStep` prompt + output schema when present
- `RouteStep` node id + intent + owner + work order id when present
- `BuildArtifact` list (id + description) in order

The plan hash must exclude non-semantic runtime data such as timestamps, machine/user identifiers,
environment values, and absolute paths.
