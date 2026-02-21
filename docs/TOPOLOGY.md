# Shoots Topology

## Integration hub (this repository)
- `ui/`: WPF operator UI and chat intake workflows.
- `src/Host`: host-owned orchestration and resume intent boundary.
- `src/Runtime`: deterministic routing/runtime execution core.
- `src/ProviderAdapters`: managed adapter contracts and provider bridges used by runtime.

## External repositories (not vendored here)
- `Shoots.Provider` (native/C++ provider implementation).
- `Shoots.Engine` (future external engine integration, if adopted).

## Referencing external repos without merging
Use one of:
1. Git submodule pinned to a commit.
2. NuGet package dependency.
3. Versioned binary drop/artifact.

Keep this repository as the integration root; do not duplicate external source trees under `src/`.
