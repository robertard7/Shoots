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

            var registry = new ToolRegistry("etc/ui.tools.catalog.json");
            var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
            var service = new BuilderExecutionService(runtimeBridge, new ArtifactManager(), registry);
            var result = service.Execute(plan, project);

            Assert.Equal(RunStates.Completed, result.Run.Status);
            Assert.Equal(registry.CatalogHash, result.Run.ToolCatalogHash);
            Assert.False(string.IsNullOrWhiteSpace(result.Run.PlanHash));
            Assert.True(Directory.Exists(result.RunPath));
            Assert.True(File.Exists(result.RunJsonPath));
            Assert.True(File.Exists(result.ArtifactJsonPath));
            Assert.True(File.Exists(Path.Combine(result.RunPath, "narrator.jsonl")));
            Assert.True(File.Exists(Path.Combine(result.RunPath, "environment.json")));
            Assert.True(File.Exists(Path.Combine(result.RunPath, "artifacts", "manifest.json")));
            Assert.True(File.Exists(Path.Combine(project.WorkspacePath, "artifacts", "demo", "output.txt")));

            var verify = new ArtifactManager().VerifyArtifacts(result.RunPath);
            Assert.True(verify.Ok);
            Assert.Empty(verify.Errors);
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
