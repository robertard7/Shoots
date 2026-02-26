using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Tools.Linux;

public static class LinuxToolCatalog
{
    public static IReadOnlyList<ToolRegistryEntry> LoadEntries(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is required", nameof(path));

        using var stream = File.OpenRead(path);
        var catalog = JsonSerializer.Deserialize<ToolCatalogDocument>(stream, JsonOptions())
            ?? throw new InvalidOperationException("tools catalog is required");

        if (catalog.Tools is null)
            throw new InvalidOperationException("tools catalog is required");

        return catalog.Tools
            .OrderBy(tool => tool.Id, StringComparer.Ordinal)
            .Select(ToEntry)
            .ToList();
    }

    public static IReadOnlyList<ToolSpec> LoadSpecs(string path)
        => LoadEntries(path).Select(entry => entry.Spec).ToList();

    private static ToolRegistryEntry ToEntry(ToolCatalogTool tool)
    {
        if (tool is null)
            throw new ArgumentNullException(nameof(tool));

        var providerKind = ParseProviderKind(tool.RequiredAuthority?.ProviderKind);
        var capabilities = ParseCapabilities(tool.RequiredAuthority?.Capabilities);

        var spec = new ToolSpec(
            new ToolId(Require(tool.Id, "tool id")),
            Require(tool.Description, "tool description"),
            new ToolAuthorityScope(providerKind, capabilities),
            (tool.Inputs ?? Array.Empty<ToolCatalogInput>())
                .Select(i => new ToolInputSpec(
                    Require(i.Name, "input name"),
                    Require(i.Type, "input type"),
                    i.Required,
                    Require(i.Description, "input description")))
                .ToArray(),
            (tool.Outputs ?? Array.Empty<ToolCatalogOutput>())
                .Select(o => new ToolOutputSpec(
                    Require(o.Name, "output name"),
                    Require(o.Type, "output type"),
                    Require(o.Description, "output description")))
                .ToArray(),
            (tool.Tags ?? Array.Empty<string>()).ToArray());

        return new ToolRegistryEntry(spec);
    }

    private static ProviderKind ParseProviderKind(string? value)
    {
        // default to Local if missing
        if (string.IsNullOrWhiteSpace(value))
            return ProviderKind.Local;

        // Catalog may use "Embedded" to mean "local, in-process provider"
        if (string.Equals(value, "Embedded", StringComparison.OrdinalIgnoreCase))
            return ProviderKind.Local;

        return Enum.Parse<ProviderKind>(value, ignoreCase: true);
    }

    private static ProviderCapabilities ParseCapabilities(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return ProviderCapabilities.None;

        var caps = ProviderCapabilities.None;

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (Enum.TryParse<ProviderCapabilities>(raw, ignoreCase: true, out var cap))
                caps |= cap;
        }

        return caps;
    }

    private static string Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{label} is required");

        return value;
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