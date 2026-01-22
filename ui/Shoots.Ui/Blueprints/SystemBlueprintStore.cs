using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Blueprints;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface ISystemBlueprintStore
{
    IReadOnlyList<SystemBlueprint> LoadForWorkspace(string workspaceRoot);

    void SaveForWorkspace(string workspaceRoot, IEnumerable<SystemBlueprint> blueprints);
}

public sealed class SystemBlueprintStore : ISystemBlueprintStore
{
    public const string FolderName = "Blueprints";

    private readonly string _rootDirectory;

    public SystemBlueprintStore(string? baseDirectory = null)
    {
        _rootDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shoots",
            FolderName);
    }

    public IReadOnlyList<SystemBlueprint> LoadForWorkspace(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Array.Empty<SystemBlueprint>();

        var path = GetWorkspacePath(workspaceRoot);
        if (!File.Exists(path))
            return Array.Empty<SystemBlueprint>();

        try
        {
            var json = File.ReadAllText(path);
            var blueprints = JsonSerializer.Deserialize<List<SystemBlueprint>>(json, JsonOptions())
                ?? new List<SystemBlueprint>();

            return blueprints
                .OrderByDescending(blueprint => blueprint.CreatedUtc)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<SystemBlueprint>();
        }
    }

    public void SaveForWorkspace(string workspaceRoot, IEnumerable<SystemBlueprint> blueprints)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("workspace root is required", nameof(workspaceRoot));
        if (blueprints is null)
            throw new ArgumentNullException(nameof(blueprints));

        Directory.CreateDirectory(_rootDirectory);

        var path = GetWorkspacePath(workspaceRoot);
        var payload = blueprints.ToList();
        var json = JsonSerializer.Serialize(payload, JsonOptions());
        File.WriteAllText(path, json);
    }

    private string GetWorkspacePath(string workspaceRoot)
    {
        var hash = ComputeHash(workspaceRoot.Trim());
        return Path.Combine(_rootDirectory, $"{hash}.json");
    }

    private static string ComputeHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
