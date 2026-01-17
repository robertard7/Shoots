# Project Dependency Map

## Builder projects
- `Shoots.Builder.Core` → `Shoots.Runtime.Abstractions`
- `Shoots.Builder.Cli` → `Shoots.Builder.Core`, `Shoots.Runtime.Abstractions`
- `Shoots.Builder.Tests` → `Shoots.Builder.Core`, `Shoots.Builder.Cli`, `Shoots.Runtime.Abstractions`

## Runtime projects
- `Shoots.Runtime.Abstractions` → (none)
- `Shoots.Runtime.Core` → `Shoots.Runtime.Abstractions`
- `Shoots.Runtime.Loader` → `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`
- `Shoots.Runtime.Language` → `Shoots.Runtime.Abstractions`
- `Shoots.Runtime.Runner` → `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Loader`
- `Shoots.Runtime.Sandbox` → `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`, `Shoots.Runtime.Language`
- `Shoots.Runtime.Tests` → `Shoots.Runtime.Abstractions`, `Shoots.Runtime.Core`, `Shoots.Runtime.Loader`

## Builder → Runtime execution wiring check
- Builder projects reference `Shoots.Runtime.Core` / `Shoots.Runtime.Loader`: **none**.
