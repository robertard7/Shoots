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

## Current Planner Dependencies
- `IRuntimeServices` for command metadata only
- `IDelegationPolicy` for deterministic authority decisions

## Known Violations
- None (planner implementation performs no IO, time, env, random, or network access)

## Plan Hash Inputs
The plan hash must include only semantic inputs, in stable order:
- `BuildRequest.CommandId` (normalized)
- `BuildContract.Version`
- `DelegationAuthority` fields (provider id, kind, policy id, allows delegation)
- `BuildRequest.Args` ordered by key (case-insensitive), normalized key/value tokens
- `BuildStep` list (id + description) in order
- `BuildArtifact` list (id + description) in order

The plan hash must exclude non-semantic runtime data such as timestamps, machine/user identifiers,
environment values, and absolute paths.
