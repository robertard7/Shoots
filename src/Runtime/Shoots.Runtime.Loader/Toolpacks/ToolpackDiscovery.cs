using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Runtime.Loader.Toolpacks;

public sealed class ToolpackDiscovery
{
    public IReadOnlyList<ToolpackManifest> Discover(string toolpacksRoot)
    {
        if (string.IsNullOrWhiteSpace(toolpacksRoot))
            throw new ArgumentException("toolpacks root is required", nameof(toolpacksRoot));

        if (!Directory.Exists(toolpacksRoot))
            return Array.Empty<ToolpackManifest>();

        var manifests = new List<ToolpackManifest>();

        var paths = Directory.EnumerateFiles(toolpacksRoot, "toolpack.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var json = File.ReadAllText(path);
            manifests.Add(ParseManifest(json, path));
        }

        return manifests.AsReadOnly();
    }

    private static ToolpackManifest ParseManifest(string json, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("toolpack manifest is required", nameof(json));

        using var document = JsonDocument.Parse(json);
        ValidateManifest(document.RootElement, sourcePath);

        var parsed = JsonSerializer.Deserialize<ToolpackManifestDocument>(json, JsonOptions());

        if (parsed is null)
            throw new ArgumentException("toolpack manifest is invalid", nameof(json));

        var tier = Enum.Parse<ToolTier>(parsed.Tier, ignoreCase: true);
        var capabilities = parsed.Capabilities?
            .Select(name => Enum.Parse<ToolpackCapability>(name, ignoreCase: true))
            .ToArray()
            ?? Array.Empty<ToolpackCapability>();
        var tools = parsed.Tools ?? Array.Empty<ToolpackTool>();

        return new ToolpackManifest(
            parsed.ToolpackId,
            parsed.Version,
            tier,
            parsed.Description,
            capabilities,
            tools,
            parsed.Requires,
            parsed.RiskNotes,
            sourcePath);
    }

    private static void ValidateManifest(JsonElement root, string sourcePath)
    {
        RequireProperty(root, "toolpackId", JsonValueKind.String, sourcePath);
        RequireProperty(root, "version", JsonValueKind.String, sourcePath);
        RequireProperty(root, "tier", JsonValueKind.String, sourcePath);
        RequireProperty(root, "description", JsonValueKind.String, sourcePath);
        RequireProperty(root, "riskNotes", JsonValueKind.String, sourcePath);
        RequireProperty(root, "capabilities", JsonValueKind.Array, sourcePath);

        var capabilities = root.GetProperty("capabilities");
        foreach (var capability in capabilities.EnumerateArray())
        {
            if (capability.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"capabilities must be strings in {sourcePath}");
        }

        var tools = RequireProperty(root, "tools", JsonValueKind.Array, sourcePath);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"tool entry must be an object in {sourcePath}");

            RequireProperty(tool, "toolId", JsonValueKind.String, sourcePath);
            RequireProperty(tool, "authority", JsonValueKind.Object, sourcePath);
            var authority = tool.GetProperty("authority");
            RequireProperty(authority, "providerKind", JsonValueKind.String, sourcePath);
        }

        if (root.TryGetProperty("requires", out var requires) && requires.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"requires must be an object in {sourcePath}");
    }

    private static JsonElement RequireProperty(JsonElement root, string name, JsonValueKind kind, string sourcePath)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new ArgumentException($"missing {name} in {sourcePath}");

        if (value.ValueKind != kind)
            throw new ArgumentException($"invalid {name} in {sourcePath}");

        return value;
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private sealed record ToolpackManifestDocument(
        string ToolpackId,
        string Version,
        string Tier,
        string Description,
        IReadOnlyList<string>? Capabilities,
        IReadOnlyList<ToolpackTool>? Tools,
        JsonElement? Requires,
        string RiskNotes
    );
}
