# Shoots.ProviderAdapters.Ollama

Ollama is used as an AI decision provider that returns `ToolSelectionDecision` values.

Tool contracts (`ToolSpec`, `ToolInvocation`, `ToolResult`) and tool handler execution are independent of Ollama, so the decision provider can be replaced by an embedded/local model without changing tool implementations.
