using System.Threading;
using System.Threading.Tasks;

namespace Shoots.UI.Projects;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IWorkspaceShellService
{
    bool OpenFolder(string path);
    Task OpenFolderAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Copies text using the host shell clipboard integration.
    /// Implementations must handle platform/threading requirements internally.
    /// </summary>
    Task CopyTextAsync(string text, CancellationToken ct = default);
}
