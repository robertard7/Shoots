# Provider Boundary

## Layer split

- **ProviderAdapters (this repo)**: managed C# adapter/client boundary used by Host/Runtime.
- **Shoots.Provider (separate repo)**: native provider implementation/runtime target.

```mermaid
graph LR
  HostCore[Host.Core] --> RuntimeCore[Runtime.Core]
  RuntimeCore --> ProviderAdapters[ProviderAdapters (C# adapters)]
  ProviderAdapters -.protocol boundary.-> NativeProvider[Shoots.Provider (native repo)]
```

- ProviderAdapters are integration stubs/adapters and contracts.
- Shoots.Provider is where native provider execution lives.

## Tool Contract Rules

Tools executed through provider clients must stay contract-based and deterministic:

- Stable output keys (`error.code`, `error.message`, and declared output names).
- Bounded output bytes and bounded runtime (timeouts).
- No absolute paths in list-like outputs; use repository-relative paths.
- No hidden mutable global tool state.

Example catalog snippet:

```json
{
  "id": "linux.fs.ls.v1",
  "requiredAuthority": { "providerKind": "Embedded", "capabilities": ["ToolExecution"] },
  "inputs": [
    { "name": "path", "type": "string", "required": true, "description": "Directory path." },
    { "name": "max_entries", "type": "int", "required": false, "description": "Maximum emitted entries." }
  ],
  "outputs": [
    { "name": "entries", "type": "string", "description": "Newline-delimited relative entries." },
    { "name": "count", "type": "int", "description": "Emitted entry count." }
  ]
}
```

- Tool handlers are provider-agnostic: they execute through `IProviderClient` contracts and do not depend on Ollama.
- Ollama (when configured) supplies selection decisions; execution contracts remain unchanged.

## Patch tool example

Example invocation payload for `linux.text.apply_unified_diff.v1`:

```json
{
  "base_dir_rel": "src",
  "diff_text": "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n",
  "max_files": 50
}
```

The handler must reject path escapes and return deterministic error keys (`error.code`, `error.message`).
