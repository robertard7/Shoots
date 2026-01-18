# Project Dependency Map

## Builder projects
- `Shoots.Builder.Core` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Builder.Cli` → `Shoots.Builder.Core`, `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Builder.Tests` → `Shoots.Builder.Core`, `Shoots.Builder.Cli`, `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`

## Contracts projects
- `Shoots.Contracts.Core` → (none)

## Runtime projects
- `Shoots.Runtime.Abstractions` → `Shoots.Contracts.Core`
- `Shoots.Runtime.Core` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Runtime.Loader` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`
- `Shoots.Runtime.Language` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Runtime.Runner` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Loader`
- `Shoots.Runtime.Sandbox` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`, `Shoots.Runtime.Language`
- `Shoots.Runtime.Tests` → `Shoots.Contracts.Core`, `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`, `Shoots.Runtime.Loader`

## Builder → Runtime execution wiring check
- Builder projects reference `Shoots.Runtime.Core` / `Shoots.Runtime.Loader`: **none**.
