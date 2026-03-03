using System;
using System.Runtime.InteropServices;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class WorkspaceShellServiceTests
{
    [Fact]
    public async System.Threading.Tasks.Task OpenFolderAsync_OnNonWindows_CompletesWithoutThrowing()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var service = new WorkspaceShellService();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var exception = await Record.ExceptionAsync(() => service.OpenFolderAsync(path));

        Assert.Null(exception);
    }
}
