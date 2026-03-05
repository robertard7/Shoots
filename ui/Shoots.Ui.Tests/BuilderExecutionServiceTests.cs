using System;
using System.IO;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderExecutionServiceTests
{
    [Fact]
    public void Execute_creates_run_json_and_artifact_json()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var projectService = new LocalProjectService(root);

        try
        {
            var project = projectService.CreateNewProject("builder-test");
            var planner = new DemoPlanner();
            Assert.True(planner.TryBuildPlan(project, out var plan));

            var service = new BuilderExecutionService(new ToolExecutionService(), new ArtifactManager());
            var result = service.Execute(plan, project);

            Assert.Equal("completed", result.Run.Status);
            Assert.True(Directory.Exists(result.RunPath));
            Assert.True(File.Exists(result.RunJsonPath));
            Assert.True(File.Exists(result.ArtifactJsonPath));
            Assert.True(File.Exists(Path.Combine(project.WorkspacePath, "artifacts", "demo", "output.txt")));
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
