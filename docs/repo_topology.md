# Repository Topology

> ⚠️ **ProviderAdapters != Shoots.Provider**
>
> `src/ProviderAdapters` in this repository contains managed adapter/client integrations.
> `Shoots.Provider` is a separate native repository and is not part of this solution.

## Shoots (this repository)

Contains:
- Contracts
- Runtime (Core + Loader + Abstractions)
- ProviderAdapters (managed adapters)
- Host (policy, model routing, run coordination)
- UI

## Separate repositories

- `Shoots.Provider` (native provider runtime): separate repository, not referenced by this solution.
- `Shoots.Engine`: separate repository.
- `Shoots.Host` (standalone, if introduced later): not this repository.
