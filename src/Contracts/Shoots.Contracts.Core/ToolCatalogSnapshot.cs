namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic tool catalog snapshot provided to decision providers.
/// </summary>
public sealed record ToolCatalogSnapshot(
    string CatalogHash,
    IReadOnlyList<ToolSpec> Tools
);
