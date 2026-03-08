using System;
using System.IO;

namespace Shoots.UI.Builder;

public static class SafePath
{
    public static string ResolveUnderWorkspace(string workspacePath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new InvalidOperationException("Workspace path is required.");
        }

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new InvalidOperationException("Path is required.");
        }

        var workspaceFullPath = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(Path.IsPathRooted(candidatePath)
            ? candidatePath
            : Path.Combine(workspaceFullPath, candidatePath));

        var prefix = workspaceFullPath + Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolvedPath, workspaceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes workspace: '{candidatePath}'.");
        }

        return resolvedPath;
    }
}
