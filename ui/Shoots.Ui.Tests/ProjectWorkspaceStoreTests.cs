using System;
using System.IO;
using System.Linq;
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
    public void WorkspaceLoadOrderIsDeterministicByNameThenId()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ProjectWorkspaceStore(root);
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            new ProjectWorkspace("zeta", "path-z", now, ProjectId: "b"),
            new ProjectWorkspace("alpha", "path-a", now.AddHours(-2), ProjectId: "c"),
            new ProjectWorkspace("alpha", "path-a2", now.AddHours(-1), ProjectId: "a")
        };

        try
        {
            store.SaveRecentWorkspaces(items);
            var loaded = store.LoadRecentWorkspaces();

            Assert.Equal(new[] { "alpha", "alpha", "zeta" }, loaded.Select(x => x.Name).ToArray());
            Assert.Equal(new[] { "a", "c", "b" }, loaded.Select(x => x.ProjectId).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectIdsRemainStableAcrossSaveAndLoad()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ProjectWorkspaceStore(root);
        var project = new ProjectWorkspace(
            Name: "Stable",
            RootPath: "path-stable",
            LastOpenedUtc: DateTimeOffset.UtcNow,
            ProjectId: "abc123",
            CreatedUtc: DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"),
            SelectedEnvironmentId: "linux-container",
            SelectedProviderKind: "Ollama",
            SelectedProviderEndpoint: "http://localhost:11434");

        try
        {
            store.SaveRecentWorkspaces(new[] { project });
            var loaded = store.LoadRecentWorkspaces();

            Assert.Single(loaded);
            Assert.Equal("abc123", loaded[0].ProjectId);
            Assert.Equal("linux-container", loaded[0].SelectedEnvironmentId);
            Assert.Equal("Ollama", loaded[0].SelectedProviderKind);
            Assert.Equal("http://localhost:11434", loaded[0].SelectedProviderEndpoint);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
