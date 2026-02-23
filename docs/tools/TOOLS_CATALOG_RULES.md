# Tools Catalog Rules

Shoots tool catalog entries must follow deterministic contract rules:

- Tool entries are stable-sorted by `id` using ordinal comparison.
- Output keys are stable and explicit; failures use `error.code` and `error.message`.
- Handlers must enforce bounded output (`max_bytes`/`max_output_bytes`) and bounded runtime (`timeout_ms`).
- File and directory operations are confined to `ToolExecutionContext.RepoRoot`.
- Network tools must check `ToolExecutionContext.AllowNetwork` and fail with `network_disabled` when false.
- Process tools apply `ToolExecutionContext.EnvOverlay` only for the run-local execution context.
