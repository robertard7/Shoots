namespace Shoots.Runtime.Abstractions;

public sealed record ToolCatalogSnapshot(
    string Hash,
    IReadOnlyList<ToolRegistryEntry> Entries
);
