# Stabilization Status (Builder/Runtime Separation)

## W1 — Builder is planner-only
- Builder CLI references only `Shoots.Builder.Core` and `Shoots.Runtime.Abstractions`.
- Boundary tests include the Builder CLI assembly and forbid runtime execution assemblies.

## W2 — Runtime runner is self-contained
- Runtime runner reads a plan JSON, validates authority + AI steps, verifies hash, then exits.
- No planner logic exists in runtime assemblies.

## X1 — Contract freeze checkpoint
- Canonical contracts (`BuildRequest`, `BuildPlan`, `BuildStep`, `BuildArtifact`, `DelegationAuthority`) remain frozen.
- Any future changes must be additive and justified.

## X2 — Hashing rules frozen
- Hash inputs are documented and limited to semantic plan inputs.
- No runtime-only data flows into hashing.
