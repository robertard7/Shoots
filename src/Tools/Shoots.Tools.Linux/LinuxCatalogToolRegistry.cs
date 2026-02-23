using System.Security.Cryptography;
using System.Text;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Tools.Linux;

public sealed class LinuxCatalogToolRegistry : IToolRegistry
{
    private readonly IReadOnlyList<ToolRegistryEntry> _entries;

    public LinuxCatalogToolRegistry(string catalogPath)
    {
        _entries = LinuxToolCatalog.LoadEntries(catalogPath);
        CatalogHash = ComputeHash(_entries);
    }

    public string CatalogHash { get; }

    public IReadOnlyList<ToolRegistryEntry> GetAllTools() => _entries;

    public ToolRegistryEntry? GetTool(ToolId toolId)
        => _entries.FirstOrDefault(e => e.Spec.ToolId == toolId);

    public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => _entries;

    private static string ComputeHash(IReadOnlyList<ToolRegistryEntry> entries)
    {
        var payload = string.Join("|", entries.Select(e => e.Spec.ToolId.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
