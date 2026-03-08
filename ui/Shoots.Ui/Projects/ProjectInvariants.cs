using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Shoots.UI.Projects;

public sealed record ProjectInvariantResult(
    bool Ok,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra,
    IReadOnlyList<string> Errors
);

public static class ProjectInvariants
{
    private static readonly string[] RequiredFolders = { "plans", "runs", "artifacts", "notes" };

    public static ProjectInvariantResult Verify(string workspacePath)
    {
        var missing = new List<string>();
        var extra = new List<string>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return new ProjectInvariantResult(false, missing, extra, new[] { "workspacePath is empty" });
        }

        if (!Directory.Exists(workspacePath))
        {
            return new ProjectInvariantResult(false, missing, extra, new[] { $"workspace missing: {workspacePath}" });
        }

        var requiredSet = new HashSet<string>(RequiredFolders, StringComparer.Ordinal);

        var projectFilePath = Path.Combine(workspacePath, "project.json");
        if (!File.Exists(projectFilePath))
        {
            missing.Add("project.json");
        }

        foreach (var folder in RequiredFolders)
        {
            if (!Directory.Exists(Path.Combine(workspacePath, folder)))
            {
                missing.Add(folder);
            }
        }

        foreach (var directory in Directory.GetDirectories(workspacePath).Select(Path.GetFileName))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (directory.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!requiredSet.Contains(directory))
            {
                extra.Add(directory);
            }
        }

        return new ProjectInvariantResult(missing.Count == 0 && errors.Count == 0, missing, extra, errors);
    }
}
