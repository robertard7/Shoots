using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools;

    public ToolRegistry(string catalogPath = "etc/ui.tools.catalog.json")
    {
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("Tool catalog not found.", catalogPath);
        }

        var payload = JsonSerializer.Deserialize<ToolCatalogPayload>(File.ReadAllText(catalogPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ToolCatalogPayload(Array.Empty<ToolDefinition>());

        _tools = payload.Tools
            .Where(static tool => !string.IsNullOrWhiteSpace(tool.Id))
            .ToDictionary(static tool => tool.Id, StringComparer.Ordinal);
    }

    public bool Contains(string toolId) => _tools.ContainsKey(toolId);

    public ToolDefinition Get(string toolId)
    {
        if (_tools.TryGetValue(toolId, out var definition))
        {
            return definition;
        }

        throw new InvalidOperationException($"Tool '{toolId}' is not registered in ui.tools.catalog.json");
    }

    private sealed record ToolCatalogPayload(IReadOnlyList<ToolDefinition> Tools);
}

public sealed record ToolDefinition(string Id, IReadOnlyList<string> RequiredArgs);
