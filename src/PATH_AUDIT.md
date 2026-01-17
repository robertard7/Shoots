# Path Usage Audit (Builder + Runtime)

## Builder
- `Shoots.Builder.Core/BuilderKernel.cs`: derives `artifactsRoot` from `Environment.CurrentDirectory` and writes artifacts beneath `./artifacts/<plan-hash>`. Classification: **temporary scaffolding** (execution output), not contract-level.
- `Shoots.Builder.Cli/Program.cs`: derives `modulesDir` from `Environment.CurrentDirectory` and loads modules from `./modules`. Classification: **temporary scaffolding** (CLI wiring), not contract-level.

## Runtime
- `Shoots.Runtime.Loader/DefaultRuntimeLoader.cs`: enumerates DLLs from a caller-provided directory path. Classification: **data-driven input** (caller provides the path), not contract-level.

## Contract-Level Hardcoded Paths
- **None found.** Planner and hasher are path-agnostic; absolute paths are rejected from plan hash inputs.
