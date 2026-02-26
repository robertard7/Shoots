using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Tools.Linux;

public static class LinuxToolCatalog
{
    public static IReadOnlyList<ToolRegistryEntry> LoadEntries(string path)
    {
        using var stream = File.OpenRead(path);
        var catalog = JsonSerializer.Deserialize<ToolCatalogDocument>(stream, JsonOptions())
            ?? throw new InvalidOperationException("tools catalog is required");

        return catalog.Tools
            .OrderBy(tool => tool.Id, StringComparer.Ordinal)
            .Select(ToEntry)
            .ToList();
    }

    public static IReadOnlyList<ToolSpec> LoadSpecs(string path)
        => LoadEntries(path).Select(entry => entry.Spec).ToList();

    private static ToolRegistryEntry ToEntry(ToolCatalogTool tool)
    {
        var providerKind = Enum.Parse<ProviderKind>(tool.RequiredAuthority.ProviderKind, ignoreCase: true);
        var capabilities = ProviderCapabilities.None;
        foreach (var value in tool.RequiredAuthority.Capabilities)
        {
            if (Enum.TryParse<ProviderCapabilities>(value, ignoreCase: true, out var cap))
                capabilities |= cap;
        }

        var spec = new ToolSpec(
            new ToolId(tool.Id),
            tool.Description,
            new ToolAuthorityScope(providerKind, capabilities),
            tool.Inputs.Select(i => new ToolInputSpec(i.Name, i.Type, i.Required, i.Description)).ToArray(),
            tool.Outputs.Select(o => new ToolOutputSpec(o.Name, o.Type, o.Description)).ToArray(),
            tool.Tags.ToArray());

        return new ToolRegistryEntry(spec);
    }

    private static JsonSerializerOptions JsonOptions()
        => new() { PropertyNameCaseInsensitive = true };
}

public sealed record ToolCatalogDocument(IReadOnlyList<ToolCatalogTool> Tools);
public sealed record ToolCatalogTool(
    string Id,
    string Description,
    ToolCatalogAuthority RequiredAuthority,
    IReadOnlyList<ToolCatalogInput> Inputs,
    IReadOnlyList<ToolCatalogOutput> Outputs,
    IReadOnlyList<string> Tags,
    int? MaxInputBytes = null,
    int? MaxOutputBytesOverride = null,
    int? MaxResults = null,
    int? DefaultTimeoutMs = null);
public sealed record ToolCatalogAuthority(string ProviderKind, IReadOnlyList<string> Capabilities);
public sealed record ToolCatalogInput(string Name, string Type, bool Required, string Description);
public sealed record ToolCatalogOutput(string Name, string Type, string Description);
