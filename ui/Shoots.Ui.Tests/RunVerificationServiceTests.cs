using System;
using System.IO;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class RunVerificationServiceTests
{
    [Fact]
    public void Verify_returns_valid_for_fresh_run()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var projectService = new LocalProjectService(root);

        try
        {
            var project = projectService.CreateNewProject("verify-run");
            var planner = new DemoPlanner();
            Assert.True(planner.TryBuildPlan(project, out var plan));

            var registry = new ToolRegistry("etc/ui.tools.catalog.json");
            var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
            var service = new BuilderExecutionService(runtimeBridge, new ArtifactManager(), registry);
            var run = service.Execute(plan, project);

            var verify = RunVerificationService.Verify(run.RunPath);
            Assert.True(verify.Valid);
            Assert.True(verify.ManifestValid);
            Assert.True(verify.ArtifactsValid);
            Assert.True(verify.EnvironmentValid);
            Assert.True(verify.NarratorValid);
            Assert.True(verify.BundleValid);
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
