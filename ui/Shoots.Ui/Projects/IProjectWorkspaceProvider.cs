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

    void RemoveWorkspace(ProjectWorkspace workspace);

    void UpdateWorkspace(ProjectWorkspace workspace);
}

public sealed class ProjectWorkspaceProvider : IProjectWorkspaceProvider
{
    private readonly IProjectWorkspaceStore _store;
    private ProjectWorkspace? _activeWorkspace;
    private readonly List<ProjectWorkspace> _recentWorkspaces;
    private bool _isLoaded;

    public ProjectWorkspaceProvider(IProjectWorkspaceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recentWorkspaces = new List<ProjectWorkspace>();
    }

    public IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces()
    {
        EnsureLoaded();
        return _recentWorkspaces.AsReadOnly();
    }

    public ProjectWorkspace? GetActiveWorkspace()
    {
        EnsureLoaded();
        return _activeWorkspace;
    }

    public void SetActiveWorkspace(ProjectWorkspace workspace)
    {
        if (workspace is null)
            throw new ArgumentNullException(nameof(workspace));

        EnsureLoaded();
        var updatedWorkspace = workspace with { LastOpenedUtc = DateTimeOffset.UtcNow };

        _recentWorkspaces.RemoveAll(existing =>
            string.Equals(existing.RootPath, updatedWorkspace.RootPath, StringComparison.OrdinalIgnoreCase));
        _recentWorkspaces.Insert(0, updatedWorkspace);
        if (_recentWorkspaces.Count > ProjectWorkspaceStore.MaxRecentWorkspaces)
            _recentWorkspaces.RemoveRange(ProjectWorkspaceStore.MaxRecentWorkspaces, _recentWorkspaces.Count - ProjectWorkspaceStore.MaxRecentWorkspaces);

        _activeWorkspace = updatedWorkspace;
        _store.SaveRecentWorkspaces(_recentWorkspaces);
    }

    public void RemoveWorkspace(ProjectWorkspace workspace)
    {
        if (workspace is null)
            throw new ArgumentNullException(nameof(workspace));

        EnsureLoaded();
        var removed = _recentWorkspaces.RemoveAll(existing =>
            string.Equals(existing.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _activeWorkspace = _recentWorkspaces.FirstOrDefault();
            _store.SaveRecentWorkspaces(_recentWorkspaces);
        }
    }

    public void UpdateWorkspace(ProjectWorkspace workspace)
    {
        if (workspace is null)
            throw new ArgumentNullException(nameof(workspace));

        EnsureLoaded();
        var index = _recentWorkspaces.FindIndex(existing =>
            string.Equals(existing.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return;

        _recentWorkspaces[index] = workspace;

        if (_activeWorkspace is not null &&
            string.Equals(_activeWorkspace.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            _activeWorkspace = workspace;
        }

        _store.SaveRecentWorkspaces(_recentWorkspaces);
    }

    private void EnsureLoaded()
    {
        if (_isLoaded)
            return;

        _recentWorkspaces.Clear();
        _recentWorkspaces.AddRange(_store.LoadRecentWorkspaces()
            .OrderByDescending(workspace => workspace.LastOpenedUtc));
        _activeWorkspace = _recentWorkspaces.FirstOrDefault();
        _isLoaded = true;
    }
}
