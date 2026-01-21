using System;
using System.Collections.Generic;
using System.Linq;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.

public interface IProjectWorkspaceProvider
{
    IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces();

    ProjectWorkspace? GetActiveWorkspace();

    void SetActiveWorkspace(ProjectWorkspace workspace);
}

public sealed class ProjectWorkspaceProvider : IProjectWorkspaceProvider
{
    private ProjectWorkspace? _activeWorkspace;
    private readonly List<ProjectWorkspace> _recentWorkspaces;

    public ProjectWorkspaceProvider(IEnumerable<ProjectWorkspace>? recentWorkspaces)
    {
        _recentWorkspaces = recentWorkspaces?.ToList() ?? new List<ProjectWorkspace>();
        _recentWorkspaces = _recentWorkspaces
            .OrderByDescending(workspace => workspace.LastOpenedUtc)
            .ToList();
        _activeWorkspace = _recentWorkspaces.FirstOrDefault();
    }

    public IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces() => _recentWorkspaces.AsReadOnly();

    public ProjectWorkspace? GetActiveWorkspace() => _activeWorkspace;

    public void SetActiveWorkspace(ProjectWorkspace workspace)
    {
        if (workspace is null)
            throw new ArgumentNullException(nameof(workspace));

        var updatedWorkspace = workspace with { LastOpenedUtc = DateTimeOffset.UtcNow };

        _recentWorkspaces.RemoveAll(existing =>
            string.Equals(existing.RootPath, updatedWorkspace.RootPath, StringComparison.OrdinalIgnoreCase));
        _recentWorkspaces.Insert(0, updatedWorkspace);
        if (_recentWorkspaces.Count > ProjectWorkspaceStore.MaxRecentWorkspaces)
            _recentWorkspaces.RemoveRange(ProjectWorkspaceStore.MaxRecentWorkspaces, _recentWorkspaces.Count - ProjectWorkspaceStore.MaxRecentWorkspaces);

        _activeWorkspace = updatedWorkspace;
    }
}
