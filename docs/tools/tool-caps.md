# Tool catalog caps

Optional per-tool cap fields in `etc/tools.catalog.json`:

- `maxInputBytes`: hard limit for decoded/processed input payload size.
- `maxOutputBytesOverride`: per-tool output-byte ceiling (applied with context max output; lower bound wins).
- `maxResults`: limit for list-style outputs (entries/files/results).
- `defaultTimeoutMs`: default timeout for tools that execute external processes when no explicit timeout is supplied.

Handlers should return deterministic limit failures with:

- `error.code = tool.limit_exceeded`
- stable `error.message` naming the violated cap.
