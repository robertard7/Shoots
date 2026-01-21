namespace Shoots.UI.Projects;

public interface IProjectWorkspaceProvider
{
    IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces();

    ProjectWorkspace? GetActiveWorkspace();

    void SetActiveWorkspace(ProjectWorkspace workspace);
}

public sealed class ProjectWorkspaceProvider : IProjectWorkspaceProvider
{
    private ProjectWorkspace? _activeWorkspace;
    private readonly IReadOnlyList<ProjectWorkspace> _recentWorkspaces;

    public ProjectWorkspaceProvider()
    {
        _recentWorkspaces = Array.Empty<ProjectWorkspace>();
    }

    public IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces() => _recentWorkspaces;

    public ProjectWorkspace? GetActiveWorkspace() => _activeWorkspace;

    public void SetActiveWorkspace(ProjectWorkspace workspace)
    {
        _activeWorkspace = workspace;
    }
}
