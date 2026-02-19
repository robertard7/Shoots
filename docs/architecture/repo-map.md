# Repository Map

- **Shoots (this hub repo)**: runtime, contracts, provider adapters, UI, host boundary.
- **Shoots.Provider (separate repo)**: native provider runtime/runner implementations.

Examples:
- `src/ProviderAdapters/Shoots.ProviderAdapters.Ollama` lives in this hub and is an adapter/client boundary.
- Native/provider runner code belongs in `Shoots.Provider` and is not part of this repository.

Identity semantics:
- `WorkOrderId` = lineage identity.
- `PlanHash` = content identity.
- `PlanId` = persistence key.
