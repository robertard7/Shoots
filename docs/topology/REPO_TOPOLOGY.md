# Repo Topology

Shoots is the hub repository containing Contracts, Runtime, Host, ProviderAdapters, and UI.

- `ProviderAdapters` is the managed C# adapter boundary in this repository.
- `Shoots.Provider` is a separate native provider repository and is not duplicated here.

Future integration path:
1. keep native provider in `Shoots.Provider`;
2. integrate via submodule/package/binary drop at release boundaries;
3. keep runtime contracts stable and avoid namespace drift back to `Shoots.Providers.*`.
