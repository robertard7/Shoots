# Next Axis Board — Runtime Execution Engine (Single-Axis)

## Scope
- Runtime only. No Builder changes.

## Goals
1. Define minimal execution lifecycle stages (load → validate → execute → report).
2. Add structured execution result shape (no retries, no streaming).
3. Add deterministic error classification for execution outcomes.

## Hard Stop
- No provider integrations.
- No UI or CLI changes.
- No new Builder functionality.
