using System.Collections.Generic;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IProjectWorkspaceStore
{
    IReadOnlyList<ProjectWorkspace> LoadRecentWorkspaces();

    void SaveRecentWorkspaces(IEnumerable<ProjectWorkspace> workspaces);
}
