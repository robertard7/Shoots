using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed class ProjectWorkspaceStore : IProjectWorkspaceStore
{
    public const int MaxRecentWorkspaces = 10;
    public const string FileName = "workspaces.json";

    private readonly string _storePath;

    public ProjectWorkspaceStore(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shoots");
        _storePath = Path.Combine(root, FileName);
    }

    public string StorePath => _storePath;

    public IReadOnlyList<ProjectWorkspace> LoadRecentWorkspaces()
    {
        if (!File.Exists(_storePath))
            return Array.Empty<ProjectWorkspace>();

        try
        {
            var json = File.ReadAllText(_storePath);
            var workspaces = JsonSerializer.Deserialize<List<ProjectWorkspace>>(json, JsonOptions())
                ?? new List<ProjectWorkspace>();

            return workspaces
                .OrderByDescending(workspace => workspace.LastOpenedUtc)
                .Take(MaxRecentWorkspaces)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<ProjectWorkspace>();
        }
    }

    public void SaveRecentWorkspaces(IEnumerable<ProjectWorkspace> workspaces)
    {
        if (workspaces is null)
            throw new ArgumentNullException(nameof(workspaces));

        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var payload = workspaces
            .Take(MaxRecentWorkspaces)
            .ToList();
        var json = JsonSerializer.Serialize(payload, JsonOptions());
        File.WriteAllText(_storePath, json);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
