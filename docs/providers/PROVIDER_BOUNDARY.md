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
