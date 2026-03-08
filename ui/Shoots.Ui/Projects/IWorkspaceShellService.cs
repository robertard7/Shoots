using System.Threading;
using System.Threading.Tasks;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IWorkspaceShellService
{
    bool OpenFolder(string path);
    Task OpenFolderAsync(string path, CancellationToken ct = default);
    Task CopyTextAsync(string text, CancellationToken ct = default);
}
