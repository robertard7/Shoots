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
