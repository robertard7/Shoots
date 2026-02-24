# Ollama Provider Boundary: Decision Only

Ollama is currently wired as a **decision provider only**.

## Provider capability matrix
| Provider | Role | Tool execution |
|---|---|---|
| Embedded | deterministic execution | yes |
| Ollama | tool selection decisions | no |
| Shoots.Engine (future) | tool selection decisions | no (same execution boundary) |

## What Ollama does
- Produces `ToolSelectionDecision` for `SelectTool` route gates.
- Never runs tools directly.
- Never mutates repository state.

## What executes tools
- Tool execution is handled by the embedded provider client (`EmbeddedToolProviderClient`).
- Runtime routes tool invocations to the provider execution envelope kind `Tool`.

## Model catalog override workflow
1. Optional local override: `etc/models.catalog.local.json` (gitignored).
2. Fallback template: `etc/models.catalog.template.json`.
3. When both exist, local override wins.

## Future swap path
1. Keep `IProviderClient` execution path unchanged.
2. Replace Ollama decision provider with an embedded local model decision provider.
3. Preserve `ToolSelectionDecision` contract and binding validation.
