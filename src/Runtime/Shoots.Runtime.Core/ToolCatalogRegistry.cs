using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class ToolCatalogRegistry : IToolRegistry
{
    private readonly IReadOnlyList<ToolRegistryEntry> _entries;
    private readonly Dictionary<ToolId, ToolRegistryEntry> _index;

    public ToolCatalogRegistry(ToolCatalogSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        CatalogHash = snapshot.Hash;
        _entries = snapshot.Entries.ToArray();
        _index = _entries.ToDictionary(entry => entry.Spec.ToolId);
    }

    public string CatalogHash { get; }

    public IReadOnlyList<ToolRegistryEntry> GetAllTools() => _entries;

    public ToolRegistryEntry? GetTool(ToolId toolId)
    {
        return _index.TryGetValue(toolId, out var entry) ? entry : null;
    }

    public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => _entries;
}
