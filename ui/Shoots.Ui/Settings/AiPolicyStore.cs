using System;
using System.IO;
using System.Text.Json;
using Shoots.Contracts.Core.AI;

namespace Shoots.UI.Settings;

public interface IAiPolicyStore
{
    AiPolicySettings Load(string? workspaceRoot);

    void Save(string? workspaceRoot, AiPolicySettings settings);
}

public sealed record AiPolicySettings(
    AiAccessRole AccessRole,
    AiPresentationPolicy Policy
);

public sealed class AiPolicyStore : IAiPolicyStore
{
    public const string FileName = "ai-policy.json";
    private const string SettingsFolderName = ".shoots";
    private readonly string _defaultPath;

    public AiPolicyStore(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shoots");

        _defaultPath = Path.Combine(root, FileName);
    }

    public AiPolicySettings Load(string? workspaceRoot)
    {
        var workspacePath = ResolveWorkspacePath(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(workspacePath) && File.Exists(workspacePath))
            return LoadFromPath(workspacePath);

        if (File.Exists(_defaultPath))
            return LoadFromPath(_defaultPath);

        return CreateDefault();
    }

    public void Save(string? workspaceRoot, AiPolicySettings settings)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        var path = ResolveWorkspacePath(workspaceRoot) ?? _defaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, JsonOptions());
        File.WriteAllText(path, json);
    }

    private static AiPolicySettings LoadFromPath(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AiPolicySettings>(json, JsonOptions());
            return settings ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    private static AiPolicySettings CreateDefault()
    {
        var policy = new AiPresentationPolicy(
            AiVisibilityMode.Visible,
            AllowAiPanelToggle: true,
            AllowCopyExport: true);

        return new AiPolicySettings(AiAccessRole.Developer, policy);
    }

    private static string? ResolveWorkspacePath(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return null;

        return Path.Combine(workspaceRoot, SettingsFolderName, FileName);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
