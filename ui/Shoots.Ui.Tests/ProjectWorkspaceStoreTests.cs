using System;
using System.IO;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ProjectWorkspaceStoreTests
{
    [Fact]
    public void InvalidWorkspacesJsonLoadsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ProjectWorkspaceStore(root);
        File.WriteAllText(Path.Combine(root, ProjectWorkspaceStore.FileName), "not json");

        try
        {
            var result = store.LoadRecentWorkspaces();

            Assert.Empty(result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceLoadOrderPrefersMostRecent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ProjectWorkspaceStore(root);
        var provider = new ProjectWorkspaceProvider(store);
        var older = new ProjectWorkspace("Older", "path-a", DateTimeOffset.UtcNow.AddHours(-2));
        var newer = new ProjectWorkspace("Newer", "path-b", DateTimeOffset.UtcNow.AddHours(-1));
        store.SaveRecentWorkspaces(new[] { older, newer });

        try
        {
            var active = provider.GetActiveWorkspace();

            Assert.NotNull(active);
            Assert.Equal("Newer", active?.Name);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
