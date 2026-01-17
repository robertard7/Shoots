# Path Usage Audit (Builder + Runtime)

## Builder
- `Shoots.Builder.Core/BuilderKernel.cs`: writes artifacts beneath a caller-provided `artifactsRoot`. Classification: **data-driven input** (caller provides the path), not contract-level.

## Runtime
- `Shoots.Runtime.Loader/DefaultRuntimeLoader.cs`: enumerates DLLs from a caller-provided directory path. Classification: **data-driven input** (caller provides the path), not contract-level.
- `Shoots.Runtime.Runner/Program.cs`: reads a caller-provided plan path and validates it. Classification: **data-driven input** (caller provides the path), not contract-level.

## Contract-Level Hardcoded Paths
- **None found.** Planner and hasher are path-agnostic; absolute paths are rejected from plan text inputs.
