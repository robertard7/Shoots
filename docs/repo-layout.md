# Shoots Repository Layout

## Hub model

- **Shoots** is the hub repository and source of truth for runtime, contracts, UI, docs, and engineering scripts.
- **Shoots.Provider** is a separate repository for native provider runtime/host integrations.
- **Shoots UI** lives in this repository under `ui/`.
- **Shoots.Meta** is intentionally not created yet; use `docs/` and `eng/` in this repo.

## Optional external dependency wiring

If external repos are needed, keep wiring inside this hub repository:

- place optional submodules under `eng/submodules/`
- keep all integration points documented in `docs/`
- avoid introducing new top-level repositories until runtime/UI flow is stable
