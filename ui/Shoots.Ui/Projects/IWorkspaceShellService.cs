namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IWorkspaceShellService
{
    bool OpenFolder(string path);
}
