# Project Dependency Map

## Builder projects
- `Shoots.Builder.Core` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Builder.Cli` → `Shoots.Builder.Core`, `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Builder.Tests` → `Shoots.Builder.Core`, `Shoots.Builder.Cli`, `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`

## Contracts projects
- `Shoots.Contracts.Core` → (none)


## Provider adapter projects
- `Shoots.ProviderAdapters.Abstractions` → (none)
- `Shoots.ProviderAdapters.Bridge` → `Shoots.ProviderAdapters.Abstractions`, `Shoots.ProviderAdapters.Embedded`, `Shoots.ProviderAdapters.Fake`, `Shoots.ProviderAdapters.Ollama`, `Shoots.ProviderAdapters.Null`
- `Shoots.ProviderAdapters.Embedded` → `Shoots.ProviderAdapters.Abstractions`, `Shoots.Runtime.Abstractions`
- `Shoots.ProviderAdapters.Fake` → `Shoots.ProviderAdapters.Abstractions`, `Shoots.Runtime.Abstractions`
- `Shoots.ProviderAdapters.Null` → `Shoots.ProviderAdapters.Abstractions`, `Shoots.Runtime.Abstractions`
- `Shoots.ProviderAdapters.Ollama` → `Shoots.ProviderAdapters.Abstractions`, `Shoots.Runtime.Abstractions`
- `Shoots.ProviderAdapters.Ollama.Tests` → `Shoots.ProviderAdapters.Ollama`

## Runtime projects
- `Shoots.Runtime.Abstractions` → `Shoots.Contracts.Core`
- `Shoots.Runtime.Core` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.ProviderAdapters.Abstractions`, `Shoots.ProviderAdapters.Bridge`, `Shoots.ProviderAdapters.Null`
- `Shoots.Runtime.Loader` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`, `Shoots.ProviderAdapters.Bridge`
- `Shoots.Runtime.Language` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Runtime.Runner` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Loader`
- `Shoots.Runtime.Sandbox` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`, `Shoots.Runtime.Language`
- `Shoots.Runtime.Tests` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`, `Shoots.Runtime.Loader`, `Shoots.ProviderAdapters.Abstractions`, `Shoots.ProviderAdapters.Bridge`, `Shoots.ProviderAdapters.Null`

## Builder → Runtime execution wiring check
- Builder projects reference `Shoots.Runtime.Core` / `Shoots.Runtime.Loader`: **none**.
