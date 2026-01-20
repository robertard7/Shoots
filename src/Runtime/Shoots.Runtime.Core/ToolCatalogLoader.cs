using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

// Explicit runtime binding.
// This file MUST NOT expose Contracts snapshots.
using RuntimeSnapshot = Shoots.Runtime.Abstractions.ToolCatalogSnapshot;

namespace Shoots.Runtime.Core;

public sealed class ToolCatalogLoader
{
    public RuntimeSnapshot Load(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("tool catalog json is required", nameof(json));

        var document = JsonSerializer.Deserialize<ToolCatalogDocument>(
            json,
            JsonOptions());

        if (document is null || document.Tools is null)
            throw new ArgumentException("tool catalog json is invalid", nameof(json));

        var entries = document.Tools
            .Select(CreateEntry)
            .ToArray();

        return new RuntimeSnapshot(
            ComputeHash(json),
            entries);
    }

    private static ToolRegistryEntry CreateEntry(ToolCatalogTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.ToolId))
            throw new ArgumentException("tool id is required");

        if (tool.Authority is null)
            throw new ArgumentException("tool authority is required");

        var providerKind = Enum.Parse<ProviderKind>(
            tool.Authority.ProviderKind,
            ignoreCase: true);

        var capabilities = ProviderCapabilities.None;

        if (tool.Authority.Capabilities is not null)
        {
            foreach (var name in tool.Authority.Capabilities)
            {
                if (Enum.TryParse<ProviderCapabilities>(
                        name,
                        ignoreCase: true,
                        out var parsed))
                {
                    capabilities |= parsed;
                }
            }
        }

        var inputs = tool.Inputs?
            .Select(i => new ToolInputSpec(
                i.Name,
                i.Type,
                i.Required,
                i.Description ?? string.Empty))
            .ToList()
            ?? new List<ToolInputSpec>();

        var outputs = tool.Outputs?
            .Select(o => new ToolOutputSpec(
                o.Name,
                o.Type,
                o.Description ?? string.Empty))
            .ToList()
            ?? new List<ToolOutputSpec>();

        var spec = new ToolSpec(
            new ToolId(tool.ToolId),
            tool.Description ?? string.Empty,
            new ToolAuthorityScope(providerKind, capabilities),
            inputs,
            outputs,
            tool.Tags ?? Array.Empty<string>());

        return new ToolRegistryEntry(spec);
    }

    private static string ComputeHash(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json.Trim());
        return Convert
            .ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private sealed record ToolCatalogDocument(
        IReadOnlyList<ToolCatalogTool> Tools);

    private sealed record ToolCatalogTool(
        string ToolId,
        string? Description,
        ToolCatalogAuthority Authority,
        IReadOnlyList<ToolCatalogInput>? Inputs,
        IReadOnlyList<ToolCatalogOutput>? Outputs,
        IReadOnlyList<string>? Tags);

    private sealed record ToolCatalogAuthority(
        string ProviderKind,
        IReadOnlyList<string>? Capabilities);

    private sealed record ToolCatalogInput(
        string Name,
        string Type,
        string? Description,
        bool Required);

    private sealed record ToolCatalogOutput(
        string Name,
        string Type,
        string? Description);
}
