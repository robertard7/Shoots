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
