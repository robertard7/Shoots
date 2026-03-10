using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
            Assert.False(string.IsNullOrWhiteSpace(result.Run.EnvironmentHash));
            Assert.False(string.IsNullOrWhiteSpace(result.Run.ManifestHash));
            Assert.False(string.IsNullOrWhiteSpace(result.Run.NarratorHash));
            Assert.True(Directory.Exists(result.RunPath));
            Assert.True(File.Exists(result.RunJsonPath));
            Assert.True(File.Exists(result.ArtifactJsonPath));
            Assert.True(File.Exists(Path.Combine(result.RunPath, "narrator.jsonl")));
            Assert.True(File.Exists(Path.Combine(result.RunPath, "environment.json")));
            Assert.True(File.Exists(Path.Combine(result.RunPath, "evidence_bundle.json")));
            Assert.True(File.Exists(Path.Combine(result.RunPath, RunReplayService.MetadataFileName)));
            Assert.True(File.Exists(Path.Combine(result.RunPath, RunReplayService.TimelineFileName)));
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

    [Fact]
    public void Execute_persists_replay_metadata_and_replay_matches_saved_run()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var projectService = new LocalProjectService(root);

        try
        {
            var project = projectService.CreateNewProject("builder-replay-test");
            var planner = new DemoPlanner();
            Assert.True(planner.TryBuildPlan(project, out var plan));

            var registry = new ToolRegistry("etc/ui.tools.catalog.json");
            var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
            var service = new BuilderExecutionService(runtimeBridge, new ArtifactManager(), registry);
            var result = service.Execute(plan, project, provider: "ollama");

            var metadataPath = Path.Combine(result.RunPath, RunReplayService.MetadataFileName);
            var timelinePath = Path.Combine(result.RunPath, RunReplayService.TimelineFileName);

            var metadata = JsonSerializer.Deserialize<PersistedRunMetadata>(File.ReadAllText(metadataPath));
            Assert.NotNull(metadata);
            Assert.Equal(result.Run.RunId, metadata!.RunId);
            Assert.Equal("ollama", metadata.Provider);
            Assert.Contains(metadata.StageFlow, stage => stage.StageName == "provider");
            Assert.Contains(metadata.StageFlow, stage => stage.StageName == "verification");
            Assert.Single(metadata.ProviderAttempts);
            Assert.Equal("ready", metadata.ProviderAttempts[0].Outcome);

            var timeline = JsonSerializer.Deserialize<RunStageRecord[]>(File.ReadAllText(timelinePath));
            Assert.NotNull(timeline);
            Assert.Equal(metadata.StageFlow.Count, timeline!.Length);

            var replay = RunReplayService.ReplayFromRunPath(result.RunPath);
            Assert.True(replay.IsMatch);
            Assert.Empty(replay.Mismatches);
            Assert.Equal(result.Run.RunId, replay.Run.RunId);
            Assert.True(replay.Metadata.ArtifactPaths.ContainsKey("verification_report.json"));
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
    public void Replay_reports_mismatch_when_timeline_diverges_from_saved_run()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var projectService = new LocalProjectService(root);

        try
        {
            var project = projectService.CreateNewProject("builder-replay-mismatch");
            var planner = new DemoPlanner();
            Assert.True(planner.TryBuildPlan(project, out var plan));

            var registry = new ToolRegistry("etc/ui.tools.catalog.json");
            var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
            var service = new BuilderExecutionService(runtimeBridge, new ArtifactManager(), registry);
            var result = service.Execute(plan, project);

            var metadataPath = Path.Combine(result.RunPath, RunReplayService.MetadataFileName);
            var metadata = JsonSerializer.Deserialize<PersistedRunMetadata>(File.ReadAllText(metadataPath));
            Assert.NotNull(metadata);

            var diverged = metadata! with
            {
                StageFlow = metadata.StageFlow
                    .Where(stage => !string.Equals(stage.StageName, plan.Steps[0].StepId, StringComparison.Ordinal))
                    .ToArray()
            };

            File.WriteAllText(metadataPath, JsonSerializer.Serialize(diverged, new JsonSerializerOptions { WriteIndented = true }));

            var replay = RunReplayService.ReplayFromRunPath(result.RunPath);
            Assert.False(replay.IsMatch);
            Assert.Contains(replay.Mismatches, mismatch => mismatch.Contains("stage flow diverged", StringComparison.Ordinal));
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
