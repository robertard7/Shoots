using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools;

    public ToolRegistry(string catalogPath = "etc/ui.tools.catalog.json")
    {
        CatalogPath = ResolveCatalogPath(catalogPath);

        if (!File.Exists(CatalogPath))
        {
            throw new FileNotFoundException("Tool catalog not found.", CatalogPath);
        }

        var rawCatalog = File.ReadAllText(CatalogPath);
        CatalogHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawCatalog))).ToLowerInvariant();

        var payload = JsonSerializer.Deserialize<ToolCatalogPayload>(rawCatalog, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ToolCatalogPayload(Array.Empty<ToolDefinition>());

        var normalized = payload.Tools
            .Where(static tool => !string.IsNullOrWhiteSpace(tool.Id))
            .OrderBy(static tool => tool.Id, StringComparer.Ordinal)
            .ToList();

        var duplicateIds = normalized
            .GroupBy(static x => x.Id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();

        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException($"Duplicate tool ids in catalog: {string.Join(", ", duplicateIds)}");
        }

        _tools = normalized.ToDictionary(static tool => tool.Id, StringComparer.Ordinal);
    }

    public string CatalogPath { get; }

    public string CatalogHash { get; }

    public bool Contains(string toolId) => _tools.ContainsKey(toolId);

    public ToolDefinition Get(string toolId)
    {
        if (_tools.TryGetValue(toolId, out var definition))
        {
            return definition;
        }

        throw new InvalidOperationException($"Tool '{toolId}' is not registered in ui.tools.catalog.json");
    }

    private static string ResolveCatalogPath(string catalogPath)
    {
        if (Path.IsPathRooted(catalogPath))
        {
            return catalogPath;
        }

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, catalogPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return Path.GetFullPath(catalogPath);
    }

    private sealed record ToolCatalogPayload(IReadOnlyList<ToolDefinition> Tools);
}

public sealed record ToolDefinition(string Id, IReadOnlyList<string> RequiredArgs);

