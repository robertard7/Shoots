# Ollama Provider Boundary: Decision Only

Ollama is currently wired as a **decision provider only**.

## What Ollama does
- Produces `ToolSelectionDecision` for `SelectTool` route gates.
- Never runs tools directly.
- Never mutates repository state.

## What executes tools
- Tool execution is handled by the embedded provider client (`EmbeddedToolProviderClient`).
- Runtime routes tool invocations to the provider execution envelope kind `Tool`.

## Why this boundary exists
- Keeps deterministic tool execution in-process and contract-validated.
- Allows swapping decision brains without changing execution safety.

## Future swap path
1. Keep `IProviderClient` execution path unchanged.
2. Replace Ollama decision provider with an embedded local model decision provider.
3. Preserve `ToolSelectionDecision` contract and binding validation.
