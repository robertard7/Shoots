# Shoots.Runtime Stability Policy

Shoots.Runtime is intended to remain stable and unchanged once finalized.
All extensibility must occur outside the runtime core.

## What must never change (v1 line)

- `Shoots.Runtime.Abstractions` remains CONTRACT-ONLY:
  - no logic
  - no helpers
  - no parsing
  - no execution
  - no dependencies on Core/Loader/Language

- All command execution flows through `RuntimeEngine.Execute`
  - no alternate execution entrypoints
  - no module-to-module dispatch
  - no implicit routing

- Modules are pure handlers:
  - input: `RuntimeRequest`, `RuntimeContext`, `IRuntimeServices`
  - output: `RuntimeResult` with structured output only

- No partial runtime state:
  - module discovery is safe
  - registry/catalog build is deterministic
  - engine starts in a valid state or not at all

## Versioning rules

- PATCH: bugfix only, no semantic changes
- MINOR: additive only, backward compatible
- MAJOR: breaking changes (new runtime line)

External modules must declare compatibility via manifest metadata and are rejected if incompatible.

## Error ID stability

Error IDs are part of the runtime contract.
- Do not rename error IDs
- Do not change meanings of existing IDs
- Add new IDs for new conditions

## Command ID stability

Command IDs are immutable once published.
- no aliases
- no fuzzy matching
- no fallback execution
- new behavior = new command ID
