using System;
using System.IO;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class LocalProjectServiceTests
{
    [Fact]
    public void CreateNewProject_creates_workspace_project_file_and_subfolders()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new LocalProjectService(root);

        try
        {
            var project = service.CreateNewProject("demo");

            Assert.True(Directory.Exists(project.WorkspacePath));
            Assert.True(File.Exists(project.ProjectFilePath));
            Assert.True(Directory.Exists(Path.Combine(project.WorkspacePath, "plans")));
            Assert.True(Directory.Exists(Path.Combine(project.WorkspacePath, "runs")));
            Assert.True(Directory.Exists(Path.Combine(project.WorkspacePath, "artifacts")));
            Assert.True(Directory.Exists(Path.Combine(project.WorkspacePath, "notes")));
            Assert.True(File.Exists(Path.Combine(project.WorkspacePath, ".shoots", "create.log")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LoadProject_reads_project_fields()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new LocalProjectService(root);

        try
        {
            var created = service.CreateNewProject("alpha");
            var loaded = service.LoadProject(created.ProjectFilePath);

            Assert.Equal(created.ProjectId, loaded.ProjectId);
            Assert.Equal(created.Name, loaded.Name);
            Assert.Equal(created.WorkspacePath, loaded.WorkspacePath);
            Assert.Equal(created.ProjectFilePath, loaded.ProjectFilePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RunDemoPlan_creates_run_output()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new LocalProjectService(root);

        try
        {
            var project = service.CreateNewProject("demo");
            var runPath = service.RunDemoPlan(project);

            Assert.True(Directory.Exists(runPath));
            Assert.True(File.Exists(Path.Combine(project.WorkspacePath, "plans", "demo.mmd")));
            Assert.True(File.Exists(Path.Combine(runPath, "result.txt")));

            var secondRun = service.RunDemoPlan(project);
            Assert.EndsWith("000002", secondRun.Replace('\\', '/'));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
