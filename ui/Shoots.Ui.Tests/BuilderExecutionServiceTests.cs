using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Xunit;

namespace Shoots.UI.Tests;

[CollectionDefinition("Builder proof serial", DisableParallelization = true)]
public sealed class BuilderProofSerialCollectionDefinition
{
}

[Collection("Builder proof serial")]
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
            Assert.True(File.Exists(Path.Combine(result.RunPath, RunReplayService.ReplayDiffFileName)));
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
            Assert.True(File.Exists(Path.Combine(result.RunPath, RunReplayService.ReplayDiffFileName)));
            var diff = JsonSerializer.Deserialize<ReplayDiffResult>(File.ReadAllText(Path.Combine(result.RunPath, RunReplayService.ReplayDiffFileName)));
            Assert.NotNull(diff);
            Assert.Contains(diff!.StageDiffs, stage => stage.DiffKind == "missing_step" || stage.DiffKind == "extra_step" || stage.DiffKind == "stage_mismatch");
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
    public void Replay_writes_replay_error_when_artifacts_are_corrupt()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var runPath = Path.Combine(root, "runs", "000001");
        Directory.CreateDirectory(runPath);

        try
        {
            File.WriteAllText(Path.Combine(runPath, "run.json"), "{}");
            File.WriteAllText(Path.Combine(runPath, RunReplayService.MetadataFileName), "{}");
            File.WriteAllText(Path.Combine(runPath, RunReplayService.TimelineFileName), "[]");

            var ex = Assert.Throws<InvalidDataException>(() => RunReplayService.ReplayFromRunPath(runPath));
            Assert.Contains("run_metadata.json is incomplete", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(runPath, RunReplayService.ReplayErrorFileName)));
            var error = JsonSerializer.Deserialize<ReplayErrorRecord>(File.ReadAllText(Path.Combine(runPath, RunReplayService.ReplayErrorFileName)));
            Assert.NotNull(error);
            Assert.Equal("replay.artifacts.invalid", error!.Code);
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
    public async Task RunBuilderProofMatrixAsync_with_fake_runner_writes_artifacts_and_followup_linkage()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");

            Assert.Equal("passed_with_routing", run.FinalClassification);
            Assert.Equal("sufficient_with_repair_loop", run.ModelFloorVerdict);
            Assert.True(File.Exists(BuilderExecutionService.BuilderProofMatrixArtifactPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderProofSummaryPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelFloorVerdictPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelFloorSummaryPath(run.RunFolder)));

            var proofCase = Assert.Single(run.CaseResults, result => string.Equals(result.TargetId, "test-project", StringComparison.Ordinal));
            Assert.Equal("recovered_with_guidance", proofCase.FinalClassification);
            Assert.True(File.Exists(proofCase.ValidationResultPath));
            Assert.True(File.Exists(proofCase.FollowupIntakePath));
            Assert.True(File.Exists(proofCase.FollowupPlanPath));
            Assert.True(File.Exists(proofCase.RepairPrepBundlePath));
            Assert.True(File.Exists(proofCase.RepairBundlePath));
            Assert.True(File.Exists(proofCase.RecoveryValidationResultPath));
            Assert.True(File.Exists(proofCase.FollowupExecutionOutcomePath));

            var outcome = JsonSerializer.Deserialize<ValidationFollowupExecutionOutcome>(File.ReadAllText(proofCase.FollowupExecutionOutcomePath));
            Assert.NotNull(outcome);
            Assert.Equal("resolved", outcome!.OutcomeClassification);

            var boundaryCase = Assert.Single(run.CaseResults, result => string.Equals(result.TargetId, "bounded-refactor", StringComparison.Ordinal));
            Assert.Equal("failed_after_followup", boundaryCase.FinalClassification);
            Assert.Equal("task_out_of_scope_for_floor", boundaryCase.RoutingRecommendationState);
            Assert.Equal("reject_band", boundaryCase.TrustBand);
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
    public async Task RunBuilderProofMatrixAsync_marks_repeated_failures_beyond_model_floor_after_three_failed_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new AlwaysFailingProofCommandRunner());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");

            var proofCase = Assert.Single(run.CaseResults, result => string.Equals(result.TargetId, "test-project", StringComparison.Ordinal));
            Assert.Equal("failed_after_followup", proofCase.FinalClassification);
            Assert.Equal("beyond_model_floor", proofCase.RepeatedFailureClassification);
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
    public async Task RunBuilderProofMatrixAsync_loads_latest_run_and_verdict_from_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            var latestRun = BuilderExecutionService.LoadLatestBuilderProofRun(root);
            var latestVerdict = BuilderExecutionService.LoadLatestBuilderModelFloorVerdict(root);

            Assert.NotNull(latestRun);
            Assert.NotNull(latestVerdict);
            Assert.Equal(run.ProofRunId, latestRun!.ProofRunId);
            Assert.Equal(run.RunFolder, latestRun.RunFolder);
            Assert.Equal("sufficient_with_repair_loop", latestVerdict!.Verdict);
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
    public async Task RunBuilderProofMatrixAsync_writes_failure_patterns_external_proof_and_floor_guidance_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");

            Assert.True(File.Exists(BuilderExecutionService.BuilderModelFloorFailurePatternsPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderExternalProofRunPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderExternalProofSummaryPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderExternalFloorVerdictPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderExternalFloorSummaryPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelFloorPolicyPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelFloorPolicySummaryPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelTrustBandsPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelScopeSummaryPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelRoutingRecommendationPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelEscalationDecisionPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelRoutingPlanPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderStrongerTierAvailabilityPath(run.RunFolder)));

            var failurePatterns = BuilderExecutionService.LoadBuilderProofFailurePatternSummary(run.RunFolder);
            Assert.NotNull(failurePatterns);
            Assert.Equal(3, failurePatterns!.Entries.Count);
            Assert.Contains(failurePatterns.Entries, pattern => string.Equals(pattern.TargetId, "test-project", StringComparison.Ordinal) &&
                                                               string.Equals(pattern.FailureCategory, "partial_implementation_gap", StringComparison.Ordinal) &&
                                                               string.Equals(pattern.RecoveryBurdenClassification, "acceptable_with_repair_loop", StringComparison.Ordinal));
            Assert.Contains(failurePatterns.Entries, pattern => string.Equals(pattern.TargetId, "test-extension", StringComparison.Ordinal) &&
                                                               string.Equals(pattern.FailureCategory, "namespace_import_omission", StringComparison.Ordinal));
            Assert.Contains(failurePatterns.Entries, pattern => string.Equals(pattern.TargetId, "bounded-refactor", StringComparison.Ordinal) &&
                                                               string.Equals(pattern.FailureCategory, "file_placement_mistake", StringComparison.Ordinal));

            var externalRun = BuilderExecutionService.LoadBuilderExternalProofRun(run.RunFolder);
            Assert.NotNull(externalRun);
            Assert.Equal("passed_cleanly", externalRun!.FinalClassification);
            Assert.Equal(5, externalRun.CleanSuccessCount);
            Assert.Equal(0, externalRun.RecoveryRequiredCount);

            var externalVerdict = BuilderExecutionService.LoadBuilderExternalFloorVerdict(run.RunFolder);
            Assert.NotNull(externalVerdict);
            Assert.Equal("sufficient_for_bounded_external_targets", externalVerdict!.Verdict);

            var policy = BuilderExecutionService.LoadBuilderModelFloorPolicy(run.RunFolder);
            Assert.NotNull(policy);
            Assert.Contains("stronger model", policy!.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(policy.Guidance, line => line.Contains("compile-fix", StringComparison.OrdinalIgnoreCase) || line.Contains("repair loop", StringComparison.OrdinalIgnoreCase));

            var trustBands = BuilderExecutionService.LoadBuilderModelTrustBands(run.RunFolder);
            Assert.NotNull(trustBands);
            Assert.Equal(15, trustBands!.Entries.Count);
            Assert.Equal(12, trustBands.CleanBuildBandCount);
            Assert.Equal(2, trustBands.RepairLoopBandCount);
            Assert.Equal(0, trustBands.EscalationRecommendedBandCount);
            Assert.Equal(1, trustBands.RejectBandCount);
            Assert.Contains(trustBands.WeakSpots, weakSpot => string.Equals(weakSpot.WeakSpot, "file_placement_mistake", StringComparison.Ordinal) &&
                                                              string.Equals(weakSpot.Classification, "boundary_of_model_floor", StringComparison.Ordinal));

            var routing = BuilderExecutionService.LoadBuilderModelRoutingRecommendation(run.RunFolder);
            Assert.NotNull(routing);
            Assert.Equal("task_out_of_scope_for_floor", routing!.RecommendationState);
            Assert.Equal("bounded-refactor", routing.FeaturedTargetId);

            var escalationDecision = BuilderExecutionService.LoadBuilderModelEscalationDecision(run.RunFolder);
            Assert.NotNull(escalationDecision);
            Assert.Equal("task_should_be_split_first", escalationDecision!.EscalationRequirementState);
            Assert.Equal("split_then_escalate", escalationDecision.SplitTaskRecommendationState);
            Assert.Equal("stronger_builder_tier", escalationDecision.RecommendedModelClass);
            Assert.Equal("bounded-refactor", escalationDecision.TargetId);
            Assert.Contains("file placement", escalationDecision.PrimaryWeakSpotSummary, StringComparison.OrdinalIgnoreCase);

            var routingPlan = BuilderExecutionService.LoadBuilderModelRoutingPlan(run.RunFolder);
            Assert.NotNull(routingPlan);
            Assert.Equal("task_should_be_split_first", routingPlan!.EscalationRequirementState);
            Assert.Equal("stronger_builder_tier", routingPlan.RecommendedModelClass);
            Assert.Contains(routingPlan.SplitTaskGuidance, line => line.Contains("Reduce the touched file count", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("repo_local|bounded_refactor", routingPlan.ComparativeProofHook.ComparisonKey, StringComparison.Ordinal);

            var strongerTier = BuilderExecutionService.LoadBuilderStrongerTierAvailability(run.RunFolder);
            Assert.NotNull(strongerTier);
            Assert.Equal("available", strongerTier!.AvailabilityState);
            Assert.Equal("qwen2.5:7b-instruct", strongerTier.ConfiguredStrongerTierId);
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
    public async Task RunBuilderComparativeProofAsync_with_available_stronger_tier_writes_comparative_artifacts_and_policy()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            var comparative = await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            Assert.Equal(run.ProofRunId, comparative.SourceProofRunId);
            Assert.Equal("cleaner_success", comparative.ComparativeClassification);
            Assert.Equal("splitting_makes_low_floor_viable", comparative.SplitThenEscalateEvidenceState);
            Assert.NotNull(comparative.SplitLowFloorCase);
            Assert.Equal("passed_cleanly", comparative.StrongerTierCase.FinalClassification);
            Assert.Equal("passed_cleanly", comparative.SplitLowFloorCase!.FinalClassification);
            Assert.True(File.Exists(BuilderExecutionService.BuilderComparativeProofRunPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderComparativeProofSummaryPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRoutingPolicyEvidencePath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderSplitFirstPlanPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderTieredRoutingPolicyPath(run.RunFolder)));

            var policy = BuilderExecutionService.LoadBuilderRoutingPolicyEvidence(run.RunFolder);
            Assert.NotNull(policy);
            Assert.Equal("split_first_keep_low_floor", policy!.RoutingPolicyState);
            Assert.Contains(policy.WeakSpotOutcomes, outcome => string.Equals(outcome.WeakSpot, "file_placement_mistake", StringComparison.Ordinal) &&
                                                                 string.Equals(outcome.StrongerTierState, "resolved", StringComparison.Ordinal));

            var splitPlan = BuilderExecutionService.LoadBuilderSplitFirstPlan(run.RunFolder);
            Assert.NotNull(splitPlan);
            Assert.Equal("split_first_keep_low_floor", splitPlan!.SplitRecommendationState);
            Assert.Contains(splitPlan.Steps, step => string.Equals(step.ScopeClassification, "low_floor_repair_loop_expected", StringComparison.Ordinal));
            Assert.Contains(splitPlan.Steps, step => string.Equals(step.ScopeClassification, "low_floor_safe", StringComparison.Ordinal));
            Assert.Contains(splitPlan.Steps, step => step.WeakSpotMitigation.Contains("file_placement_mistake", StringComparison.OrdinalIgnoreCase));
            Assert.All(splitPlan.Steps, step => Assert.True(File.Exists(step.ExecutionHook.FutureExecutionArtifactPath)));

            var tieredRouting = BuilderExecutionService.LoadBuilderTieredRoutingPolicy(run.RunFolder);
            Assert.NotNull(tieredRouting);
            Assert.Equal("split_first_keep_low_floor", tieredRouting!.PrimaryRoutingState);
            Assert.Equal("cleaner_not_required", tieredRouting.StrongerTierRecommendationState);
            Assert.Contains("split", tieredRouting.PrimaryRecommendationSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("file_placement_mistake", tieredRouting.WeakSpotMitigationSummary, StringComparison.OrdinalIgnoreCase);
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
    public async Task RunBuilderSplitFirstExecutionAsync_writes_split_execution_and_closure_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var initialExecution = BuilderExecutionService.LoadBuilderSplitStepExecution(run.RunFolder);
            Assert.NotNull(initialExecution);
            Assert.Equal(3, initialExecution!.Steps.Count);
            Assert.Equal("inspect_linked_file_scope", initialExecution.Steps[0].StepType);
            Assert.Equal("prepare_repair_bundle", initialExecution.Steps[1].StepType);
            Assert.Equal("rerun_bounded_build_scope", initialExecution.Steps[2].StepType);
            Assert.Equal("eligible", initialExecution.Steps[0].EligibilityState);
            Assert.Equal("blocked", initialExecution.Steps[1].EligibilityState);
            Assert.Equal("blocked", initialExecution.Steps[2].EligibilityState);
            Assert.Contains("must finish", initialExecution.Steps[1].BlockReason, StringComparison.OrdinalIgnoreCase);

            var outcome = await service.RunBuilderSplitFirstExecutionAsync(root, provider: "ollama");

            Assert.Equal("split_equal_to_stronger_tier", outcome.ClosureClassification);
            Assert.Equal("passed_cleanly", outcome.SplitResultFinalClassification);
            Assert.Equal("clean", outcome.SplitRecoveryBurden);
            Assert.Equal("too_fragile", outcome.UnsplitLowFloorBurden);
            Assert.Equal("clean", outcome.StrongerTierBurden);
            Assert.Contains("unsplit low-floor", outcome.ComparisonToUnsplit, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stronger tier", outcome.ComparisonToStrongerTier, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(BuilderExecutionService.BuilderSplitFirstOutcomePath(run.RunFolder)));

            var updatedExecution = BuilderExecutionService.LoadBuilderSplitStepExecution(run.RunFolder);
            Assert.NotNull(updatedExecution);
            Assert.Equal("opened", updatedExecution!.Steps[0].ExecutionState);
            Assert.Equal("executed", updatedExecution.Steps[1].ExecutionState);
            Assert.Equal("completed_by_outcome", updatedExecution.Steps[2].ExecutionState);
            Assert.Equal("completed", updatedExecution.Steps[0].EligibilityState);
            Assert.Equal("completed", updatedExecution.Steps[2].EligibilityState);
            Assert.Contains("split equal to stronger tier", updatedExecution.Summary, StringComparison.OrdinalIgnoreCase);
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
    public async Task RunBuilderSplitFirstExecutionAsync_writes_default_guidance_and_request_decision_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.RunBuilderSplitFirstExecutionAsync(root, provider: "ollama");

            Assert.True(File.Exists(BuilderExecutionService.BuilderDefaultPolicyPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderDefaultPolicyHistoryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRequestPolicyDecisionPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderPolicyStabilityPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRequestIntakePath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRequestIntakeHistoryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderExecutionPrepPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderExecutionPrepHistoryPathForRepo(root)));

            var defaultGuidance = BuilderExecutionService.LoadBuilderDefaultPolicy(run.RunFolder);
            Assert.NotNull(defaultGuidance);
            Assert.Contains("bounded_refactor", defaultGuidance!.SplitFirstRequiredTaskClasses);
            Assert.DoesNotContain("bounded_refactor", defaultGuidance.StrongerTierRequiredTaskClasses);

            var requestDecision = BuilderExecutionService.LoadBuilderRequestPolicyDecision(run.RunFolder);
            Assert.NotNull(requestDecision);
            Assert.Equal("split_first_low_floor", requestDecision!.ChosenPolicyState);
            Assert.True(requestDecision.SplitFirstIsDefault);
            Assert.Equal("optional_for_cleaner_success", requestDecision.StrongerTierDisposition);
            Assert.Contains("files=3", requestDecision.Summary, StringComparison.OrdinalIgnoreCase);

            var stability = BuilderExecutionService.LoadBuilderPolicyStability(run.RunFolder);
            Assert.NotNull(stability);
            Assert.Equal("provisional", stability!.SupportLevel);
            Assert.Equal(1, stability.SupportingRunCount);
            Assert.Equal(0, stability.ContradictionCount);

            var intake = BuilderExecutionService.LoadBuilderRequestIntake(run.RunFolder);
            Assert.NotNull(intake);
            Assert.Equal("ready_for_split_first_low_floor", intake!.IntakeClassificationState);
            Assert.StartsWith("normalized_bounded_request", intake.NormalizationState, StringComparison.Ordinal);
            Assert.Equal("current", intake.FreshnessState);

            var prep = BuilderExecutionService.LoadBuilderExecutionPrep(run.RunFolder);
            Assert.NotNull(prep);
            Assert.Equal("split_first_low_floor_route", prep!.SelectedRoute);
            Assert.True(prep.SplitPlanRequired);
            Assert.Equal("moderate", prep.RerunRepairExpectationLevel);
            Assert.Equal("current", prep.FreshnessState);

            var history = BuilderExecutionService.LoadBuilderDefaultPolicyHistory(root);
            Assert.Single(history.Entries);
            Assert.Equal(run.ProofRunId, history.Entries[0].SourceProofRunId);
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
    public async Task Repeated_builder_proof_runs_raise_guidance_support_to_corroborated()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.RunBuilderSplitFirstExecutionAsync(root, provider: "ollama");

            var secondRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.RunBuilderSplitFirstExecutionAsync(root, provider: "ollama");

            var stability = BuilderExecutionService.LoadBuilderPolicyStability(secondRun.RunFolder);
            Assert.NotNull(stability);
            Assert.Equal("corroborated", stability!.SupportLevel);
            Assert.Equal(2, stability.SupportingRunCount);
            Assert.Equal(0, stability.ContradictionCount);

            var history = BuilderExecutionService.LoadBuilderDefaultPolicyHistory(root);
            Assert.Equal(2, history.Entries.Count);
            Assert.Equal(secondRun.ProofRunId, history.Entries[0].SourceProofRunId);
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
    public async Task Repeated_builder_proof_runs_mark_prior_intake_and_execution_prep_stale()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var firstRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.RunBuilderSplitFirstExecutionAsync(root, provider: "ollama");

            var firstIntake = BuilderExecutionService.LoadBuilderRequestIntake(firstRun.RunFolder);
            var firstPrep = BuilderExecutionService.LoadBuilderExecutionPrep(firstRun.RunFolder);
            Assert.NotNull(firstIntake);
            Assert.NotNull(firstPrep);
            Assert.Equal("current", firstIntake!.FreshnessState);
            Assert.Equal("current", firstPrep!.FreshnessState);

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.RunBuilderSplitFirstExecutionAsync(root, provider: "ollama");

            firstIntake = BuilderExecutionService.LoadBuilderRequestIntake(firstRun.RunFolder);
            firstPrep = BuilderExecutionService.LoadBuilderExecutionPrep(firstRun.RunFolder);
            Assert.NotNull(firstIntake);
            Assert.NotNull(firstPrep);
            Assert.Equal("stale", firstIntake!.FreshnessState);
            Assert.Equal("stale", firstPrep!.FreshnessState);
            Assert.StartsWith("Stale intake.", firstIntake.Summary, StringComparison.Ordinal);
            Assert.StartsWith("Stale execution prep.", firstPrep.Summary, StringComparison.Ordinal);

            var intakeHistory = BuilderExecutionService.LoadBuilderRequestIntakeHistory(root);
            var prepHistory = BuilderExecutionService.LoadBuilderExecutionPrepHistory(root);
            Assert.Equal(2, intakeHistory.Entries.Count);
            Assert.Equal(2, prepHistory.Entries.Count);
            Assert.Equal("current", intakeHistory.Entries[0].FreshnessState);
            Assert.Equal("stale", intakeHistory.Entries[1].FreshnessState);
            Assert.Equal("current", prepHistory.Entries[0].FreshnessState);
            Assert.Equal("stale", prepHistory.Entries[1].FreshnessState);
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
    public async Task LaunchPreparedBuilderRouteAsync_writes_launch_and_result_artifacts_for_split_first_route()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var result = await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            Assert.True(File.Exists(BuilderExecutionService.BuilderExecutionLaunchPath(run.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderExecutionResultPath(run.RunFolder)));

            var launch = BuilderExecutionService.LoadBuilderExecutionLaunch(run.RunFolder);
            Assert.NotNull(launch);
            Assert.Equal("eligible", launch!.LaunchEligibilityState);
            Assert.Equal("split_first_low_floor_route", launch.SelectedRoute);
            Assert.Equal("current_model_tier", launch.SelectedModelTier);

            Assert.Equal("launched_and_passed", result.FinalRouteOutcomeClassification);
            Assert.Equal("confirmed", result.PreparedRouteComparisonState);
            Assert.Equal("split_first_low_floor_route", result.ActualRouteUsed);
            Assert.Equal("passed", result.BuildResult);
            Assert.Equal("not_applicable", result.TestResult);

            var persisted = BuilderExecutionService.LoadBuilderExecutionResult(run.RunFolder);
            Assert.NotNull(persisted);
            Assert.Equal("launched_and_passed", persisted!.FinalRouteOutcomeClassification);
            Assert.Contains("bounded build scope", persisted.GeneratedScopeSummary, StringComparison.OrdinalIgnoreCase);
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
    public async Task LaunchPreparedBuilderRouteAsync_writes_default_launch_decision_and_override_evidence()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var overrideRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            var overrideResult = await service.LaunchPreparedBuilderRouteAsync(
                root,
                provider: "ollama",
                routeOverride: "direct_low_floor_route",
                overrideReason: "Test override against the confirmed split-first route.");

            Assert.True(File.Exists(BuilderExecutionService.BuilderLaunchDefaultDecisionPath(overrideRun.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRouteOverrideEvidencePath(overrideRun.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderPolicyReviewCandidatesPath(overrideRun.RunFolder)));

            var launchDecision = BuilderExecutionService.LoadBuilderLaunchDefaultDecision(overrideRun.RunFolder);
            Assert.NotNull(launchDecision);
            Assert.Equal("split_first_low_floor_route", launchDecision!.ConfirmedDefaultRoute);
            Assert.Equal("direct_low_floor_route", launchDecision.ActualLaunchRoute);
            Assert.Equal("overridden_by_operator", launchDecision.RouteSourceState);
            Assert.Equal("operator_override_selected", launchDecision.OperatorDecisionState);
            Assert.False(launchDecision.RepairLoopExpectedDefault);
            Assert.Equal("eligible", launchDecision.LaunchEligibilityState);

            Assert.Equal("direct_low_floor_route", overrideResult.ActualRouteUsed);
            Assert.Contains(
                overrideResult.FinalRouteOutcomeClassification,
                new[] { "launched_and_failed_followup_created", "launched_and_failed_out_of_scope" });

            var overrideEvidence = BuilderExecutionService.LoadBuilderRouteOverrideEvidence(overrideRun.RunFolder);
            Assert.NotNull(overrideEvidence);
            Assert.Equal("operator_override_selected", overrideEvidence!.OverrideState);
            Assert.Equal("split_first_low_floor_route", overrideEvidence.DefaultRoute);
            Assert.Equal("direct_low_floor_route", overrideEvidence.SelectedRoute);
            Assert.Equal("launched_and_passed", overrideEvidence.BaselineDefaultOutcomeClassification);
            Assert.Equal("regressed_outcome", overrideEvidence.OverrideOutcomeComparisonState);

            var reviewCandidates = BuilderExecutionService.LoadBuilderPolicyReviewCandidates(overrideRun.RunFolder);
            Assert.NotNull(reviewCandidates);
            Assert.Contains(
                reviewCandidates!.Entries,
                entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal) &&
                         string.Equals(entry.CandidateState, "override_caused_contradiction", StringComparison.Ordinal));
            Assert.Contains(
                reviewCandidates.Entries,
                entry => string.Equals(entry.TaskClass, "compile_fix_edit", StringComparison.Ordinal) &&
                         string.Equals(entry.CandidateState, "stable_default", StringComparison.Ordinal));
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
    public async Task BuildBuilderLaunchDefaultDecision_marks_repair_loop_default_explicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var confirmedClasses = BuilderExecutionService.LoadBuilderConfirmedTaskClasses(run.RunFolder);
            Assert.NotNull(confirmedClasses);
            var compileFix = Assert.Single(
                confirmedClasses!.Entries,
                entry => string.Equals(entry.TaskClass, "compile_fix_edit", StringComparison.Ordinal));
            var confirmedCompileFixClasses = new BuilderConfirmedTaskClasses(
                run.ProofRunId,
                BuilderExecutionService.BuilderProofFloorModelId,
                new[]
                {
                    compileFix with
                    {
                        SupportingProofRunCount = 2,
                        SupportingPreparedLaunchCount = 2,
                        ConfirmationCount = 2,
                        CurrentReadinessState = "confirmed_with_repair_loop",
                        SummaryClassification = "confirmed_with_repair_loop",
                        BuilderReadyForBoundedUse = true,
                        RequiredSupportingProofRuns = 2,
                        RequiredPreparedLaunchConfirmations = 2,
                        FreshProofRunCountAfterLatestContradiction = 0,
                        FreshPreparedLaunchConfirmationCountAfterLatestContradiction = 0,
                        ReconfirmationRequired = false,
                        DefaultRouteSuspended = false,
                        ReconfirmationStatus = "not_required",
                        LatestContradictionNote = string.Empty,
                        ContradictoryRunIds = Array.Empty<string>(),
                        Summary = "compile_fix_edit is confirmed with repair loop for bounded use."
                    }
                },
                new[] { BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder) },
                "Synthetic compile-fix confirmation summary.",
                Path.Combine(run.RunFolder, "synthetic-confirmed-classes.json"),
                DateTimeOffset.UtcNow);

            var intakePath = Path.Combine(run.RunFolder, "synthetic-compile-fix-intake.json");
            var prepPath = Path.Combine(run.RunFolder, "synthetic-compile-fix-prep.json");
            var defaultDecisionPath = Path.Combine(run.RunFolder, "synthetic-compile-fix-default.json");
            File.WriteAllText(intakePath, "{}");
            File.WriteAllText(prepPath, "{}");
            File.WriteAllText(defaultDecisionPath, "{}");

            var intake = new BuilderRequestIntake(
                "compile-fix-request",
                run.ProofRunId,
                BuilderExecutionService.BuilderRequestPolicyDecisionPath(run.RunFolder),
                "latest_default_guidance",
                BuilderExecutionService.BuilderProofFloorModelId,
                compileFix.ProofScope,
                compileFix.TargetId,
                compileFix.TargetLabel,
                compileFix.TaskClass,
                new BuilderProofComplexityDimensions(1, 1, 0, false, 0, "low"),
                "low_floor_with_repair_loop_expected",
                "optional_for_cleaner_success",
                "corroborated",
                "normalized",
                "ready_for_low_floor_with_repair_loop",
                "partial_implementation_gap",
                new[] { "Compile-fix default remains in-band when the repair loop stays available." },
                new[] { BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder) },
                "current",
                "Synthetic compile-fix intake.",
                intakePath,
                DateTimeOffset.UtcNow,
                RouteSourceState: "defaulted_by_confirmed_policy",
                OperatorOverrideState: "override_available_no_override",
                DefaultRouteReason: "Confirmed repair-loop default.");
            var prep = new BuilderExecutionPrep(
                intake.RequestId,
                run.ProofRunId,
                BuilderExecutionService.BuilderProofFloorModelId,
                compileFix.ProofScope,
                compileFix.TargetId,
                compileFix.TargetLabel,
                compileFix.TaskClass,
                intake.IntakeClassificationState,
                "low_floor_with_repair_loop_route",
                "optional_for_cleaner_success",
                "corroborated",
                "repair_loop_expected",
                SplitPlanRequired: false,
                SplitPlanPath: string.Empty,
                TieredRoutingPath: BuilderExecutionService.BuilderTieredRoutingPolicyPath(run.RunFolder),
                WeakSpotMitigationSummary: "Keep the repair loop available for compile-fix work.",
                RequiredEvidencePaths: new[] { BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder) },
                NextActions: new[] { "Inspect the first compile error before rerunning the bounded edit." },
                FutureExecutionHookPaths: Array.Empty<string>(),
                LinkedArtifactPaths: new[] { BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder) },
                FreshnessState: "current",
                Summary: "Synthetic compile-fix prep.",
                ArtifactPath: prepPath,
                ObservedUtc: DateTimeOffset.UtcNow,
                RouteSourceState: "defaulted_by_confirmed_policy",
                OperatorOverrideState: "override_available_no_override",
                DefaultRouteReason: "Confirmed repair-loop default.");
            var defaultDecision = new BuilderDefaultRouteDecision(
                run.ProofRunId,
                compileFix.TargetId,
                compileFix.TargetLabel,
                compileFix.TaskClass,
                "low_floor_with_repair_loop_route",
                "defaulted_by_confirmed_policy",
                "override_available_no_override",
                false,
                compileFix.ConfirmationCount,
                compileFix.ContradictionCount,
                "Confirmed repair-loop default for compile-fix work.",
                new[] { BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder) },
                "Synthetic compile-fix default route decision.",
                defaultDecisionPath,
                DateTimeOffset.UtcNow);

            var launchDecision = InvokePrivateStatic<BuilderLaunchDefaultDecision>(
                "BuildBuilderLaunchDefaultDecision",
                run.RunFolder,
                intake,
                prep,
                defaultDecision,
                null,
                confirmedCompileFixClasses,
                null,
                null,
                null,
                null);

            Assert.True(launchDecision.RepairLoopExpectedDefault);
            Assert.Equal("accepted_default_route", launchDecision.OperatorDecisionState);
            Assert.Equal("confirmed_with_repair_loop", launchDecision.CurrentReadinessState);
            Assert.Equal("eligible", launchDecision.LaunchEligibilityState);
            Assert.Contains("repair loop", launchDecision.Summary, StringComparison.OrdinalIgnoreCase);
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
    public async Task LaunchPreparedBuilderRouteAsync_blocks_when_execution_prep_is_stale()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var prep = BuilderExecutionService.LoadBuilderExecutionPrep(run.RunFolder);
            Assert.NotNull(prep);
            var stalePrep = prep! with
            {
                FreshnessState = "stale",
                Summary = $"Stale execution prep. {prep.Summary}"
            };
            File.WriteAllText(BuilderExecutionService.BuilderExecutionPrepPath(run.RunFolder), JsonSerializer.Serialize(stalePrep, new JsonSerializerOptions { WriteIndented = true }));

            var result = await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var launch = BuilderExecutionService.LoadBuilderExecutionLaunch(run.RunFolder);
            Assert.NotNull(launch);
            Assert.Equal("blocked_stale_execution_prep", launch!.LaunchEligibilityState);
            Assert.Equal("launch_blocked", result.FinalRouteOutcomeClassification);
            Assert.Equal("insufficient_for_scope", result.PreparedRouteComparisonState);
            Assert.Contains("stale", result.Summary, StringComparison.OrdinalIgnoreCase);
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
    public async Task Newer_builder_proof_supersedes_prior_launch_and_result_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            var firstRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");

            var supersededLaunch = BuilderExecutionService.LoadBuilderExecutionLaunch(firstRun.RunFolder);
            var supersededResult = BuilderExecutionService.LoadBuilderExecutionResult(firstRun.RunFolder);
            Assert.NotNull(supersededLaunch);
            Assert.NotNull(supersededResult);
            Assert.Equal("superseded", supersededLaunch!.FreshnessState);
            Assert.Equal("superseded", supersededResult!.FreshnessState);
            Assert.StartsWith("Superseded launch.", supersededLaunch.Summary, StringComparison.Ordinal);
            Assert.StartsWith("Superseded route result.", supersededResult.Summary, StringComparison.Ordinal);
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
    public async Task Repeated_prepared_launches_confirm_builder_readiness_for_bounded_route()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var latestRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var gate = BuilderExecutionService.LoadBuilderReadinessGate(latestRun.RunFolder);
            Assert.NotNull(gate);
            Assert.Equal("confirmed_for_bounded_use", gate!.CurrentReadinessGateState);
            Assert.True(gate.BuilderReadyForBoundedUse);
            Assert.Equal(2, gate.RequiredSupportingProofRuns);
            Assert.Equal(2, gate.RequiredPreparedLaunchConfirmations);
            Assert.Equal(2, gate.SupportingProofRunCount);
            Assert.Equal(2, gate.SupportingPreparedLaunchCount);
            Assert.Equal(2, gate.ConfirmationCount);
            Assert.Equal(0, gate.ContradictionCount);
            Assert.True(File.Exists(BuilderExecutionService.BuilderReadinessGateHistoryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRouteStabilitySummaryPath(latestRun.RunFolder)));
            Assert.Contains("confirmed_for_bounded_use", File.ReadAllText(BuilderExecutionService.BuilderRouteStabilitySummaryPath(latestRun.RunFolder)), StringComparison.Ordinal);

            var history = BuilderExecutionService.LoadBuilderReadinessGateHistory(root);
            Assert.True(history.Entries.Count >= 2);
            Assert.Equal("confirmed_for_bounded_use", history.Entries[0].ReadinessGateState);
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
    public async Task Builder_readiness_gate_records_contradiction_when_later_launch_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var latestRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var currentResult = BuilderExecutionService.LoadBuilderExecutionResult(latestRun.RunFolder);
            Assert.NotNull(currentResult);
            var contradictedResult = currentResult! with
            {
                FinalRouteOutcomeClassification = "launched_and_failed_followup_created",
                PreparedRouteComparisonState = "insufficient_for_scope",
                Summary = "Prepared builder route failed and returned to follow-up."
            };
            File.WriteAllText(
                BuilderExecutionService.BuilderExecutionResultPath(latestRun.RunFolder),
                JsonSerializer.Serialize(contradictedResult, new JsonSerializerOptions { WriteIndented = true }));

            InvokePrivateInstance(service, "RefreshBuilderReadinessArtifacts", root, latestRun.RunFolder);

            var gate = BuilderExecutionService.LoadBuilderReadinessGate(latestRun.RunFolder);
            Assert.NotNull(gate);
            Assert.Equal("unstable_needs_more_evidence", gate!.CurrentReadinessGateState);
            Assert.False(gate.BuilderReadyForBoundedUse);
            Assert.Equal(1, gate.ContradictionCount);
            Assert.Contains("insufficient for scope", gate.ContradictionNotes[0], StringComparison.OrdinalIgnoreCase);

            var history = BuilderExecutionService.LoadBuilderReadinessGateHistory(root);
            Assert.Equal("unstable_needs_more_evidence", history.Entries[0].ReadinessGateState);
            Assert.Contains("Downgraded", history.Entries[0].ChangeReason, StringComparison.Ordinal);
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
    public async Task Confirmed_task_class_summary_tracks_per_class_readiness_and_default_route_activation()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var latestRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            Assert.True(File.Exists(BuilderExecutionService.BuilderConfirmedTaskClassesPath(latestRun.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderDefaultRouteDecisionPath(latestRun.RunFolder)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderReadinessContradictionsPath(latestRun.RunFolder)));

            var confirmed = BuilderExecutionService.LoadBuilderConfirmedTaskClasses(latestRun.RunFolder);
            Assert.NotNull(confirmed);
            Assert.Contains(confirmed!.Entries, entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal));
            Assert.Contains(confirmed.Entries, entry => string.Equals(entry.TaskClass, "service_feature_addition", StringComparison.Ordinal));
            Assert.Contains(confirmed.Entries, entry => string.Equals(entry.TaskClass, "compile_fix_edit", StringComparison.Ordinal));

            var boundedRefactor = Assert.Single(confirmed.Entries, entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal));
            Assert.Equal("split_first_low_floor_route", boundedRefactor.CurrentRoute);
            Assert.Equal("confirmed_for_bounded_use", boundedRefactor.CurrentReadinessState);
            Assert.Equal("confirmed_for_bounded_use", boundedRefactor.SummaryClassification);
            Assert.False(boundedRefactor.DefaultRouteSuspended);

            var serviceEdit = Assert.Single(confirmed.Entries, entry => string.Equals(entry.TaskClass, "service_feature_addition", StringComparison.Ordinal));
            Assert.Equal("direct_low_floor_route", serviceEdit.CurrentRoute);
            Assert.Equal("confirmed_for_bounded_use", serviceEdit.CurrentReadinessState);

            var compileFix = Assert.Single(confirmed.Entries, entry => string.Equals(entry.TaskClass, "compile_fix_edit", StringComparison.Ordinal));
            Assert.Equal("low_floor_with_repair_loop_route", compileFix.CurrentRoute);
            Assert.Equal("confirmed_with_repair_loop", compileFix.CurrentReadinessState);

            var defaultDecision = BuilderExecutionService.LoadBuilderDefaultRouteDecision(latestRun.RunFolder);
            Assert.NotNull(defaultDecision);
            Assert.Equal("split_first_low_floor_route", defaultDecision!.ChosenDefaultRoute);
            Assert.Equal("defaulted_by_confirmed_policy", defaultDecision.RouteSourceState);
            Assert.Equal("override_available_no_override", defaultDecision.OperatorOverrideState);
            Assert.False(defaultDecision.DefaultRouteSuspended);

            var intake = BuilderExecutionService.LoadBuilderRequestIntake(latestRun.RunFolder);
            Assert.NotNull(intake);
            Assert.Equal("defaulted_by_confirmed_policy", intake!.RouteSourceState);
            Assert.Equal("override_available_no_override", intake.OperatorOverrideState);
            Assert.Contains("defaulted by confirmed evidence", intake.DefaultRouteReason, StringComparison.OrdinalIgnoreCase);

            var prep = BuilderExecutionService.LoadBuilderExecutionPrep(latestRun.RunFolder);
            Assert.NotNull(prep);
            Assert.Equal("defaulted_by_confirmed_policy", prep!.RouteSourceState);
            Assert.Equal("override_available_no_override", prep.OperatorOverrideState);
            Assert.Contains("defaulted by confirmed evidence", prep.DefaultRouteReason, StringComparison.OrdinalIgnoreCase);
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
    public async Task Contradicted_route_requires_fresh_reconfirmation_before_returning_to_confirmed_state()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var contradictedRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var currentResult = BuilderExecutionService.LoadBuilderExecutionResult(contradictedRun.RunFolder);
            Assert.NotNull(currentResult);
            var contradictedResult = currentResult! with
            {
                FinalRouteOutcomeClassification = "launched_and_failed_followup_created",
                PreparedRouteComparisonState = "insufficient_for_scope",
                Summary = "Prepared builder route failed and returned to follow-up."
            };
            File.WriteAllText(
                BuilderExecutionService.BuilderExecutionResultPath(contradictedRun.RunFolder),
                JsonSerializer.Serialize(contradictedResult, new JsonSerializerOptions { WriteIndented = true }));

            InvokePrivateInstance(service, "RefreshBuilderDefaultPolicyArtifacts", root, contradictedRun.RunFolder);

            var contradictedClasses = BuilderExecutionService.LoadBuilderConfirmedTaskClasses(contradictedRun.RunFolder);
            var contradictedEntry = Assert.Single(contradictedClasses!.Entries, entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal));
            Assert.Equal("unstable_needs_more_evidence", contradictedEntry.CurrentReadinessState);
            Assert.True(contradictedEntry.DefaultRouteSuspended);
            Assert.True(contradictedEntry.ReconfirmationRequired);
            Assert.Equal("waiting_for_fresh_launch_confirmation", contradictedEntry.ReconfirmationStatus);

            var contradictionArtifact = BuilderExecutionService.LoadBuilderReadinessContradictions(contradictedRun.RunFolder);
            Assert.NotNull(contradictionArtifact);
            Assert.Contains(contradictionArtifact!.Entries, entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal));
            var contradictedReconfirmation = BuilderExecutionService.LoadBuilderRouteReconfirmation(contradictedRun.RunFolder);
            Assert.NotNull(contradictedReconfirmation);
            Assert.Equal("default_route_failure", contradictedReconfirmation!.ContradictionAttributionState);
            Assert.Equal("default_still_suspended", contradictedReconfirmation.CurrentReconfirmationState);
            var contradictedRecovery = BuilderExecutionService.LoadBuilderDefaultRouteRecovery(contradictedRun.RunFolder);
            Assert.NotNull(contradictedRecovery);
            Assert.Equal("default_route_regressed", contradictedRecovery!.SuspensionCauseState);
            Assert.Equal("default_still_suspended", contradictedRecovery.RecoveryState);

            var contradictedDecision = BuilderExecutionService.LoadBuilderDefaultRouteDecision(contradictedRun.RunFolder);
            Assert.NotNull(contradictedDecision);
            Assert.Equal("suggested", contradictedDecision!.RouteSourceState);
            Assert.True(contradictedDecision.DefaultRouteSuspended);

            var firstRecoveryRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var firstRecoveryClasses = BuilderExecutionService.LoadBuilderConfirmedTaskClasses(firstRecoveryRun.RunFolder);
            var firstRecoveryEntry = Assert.Single(firstRecoveryClasses!.Entries, entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal));
            Assert.Equal("unstable_needs_more_evidence", firstRecoveryEntry.CurrentReadinessState);
            Assert.Equal("collecting_fresh_evidence", firstRecoveryEntry.ReconfirmationStatus);
            Assert.Equal(1, firstRecoveryEntry.FreshPreparedLaunchConfirmationCountAfterLatestContradiction);
            var firstRecoveryReconfirmation = BuilderExecutionService.LoadBuilderRouteReconfirmation(firstRecoveryRun.RunFolder);
            Assert.NotNull(firstRecoveryReconfirmation);
            Assert.Equal("reconfirmation_in_progress", firstRecoveryReconfirmation!.CurrentReconfirmationState);
            var firstRecoveryState = BuilderExecutionService.LoadBuilderDefaultRouteRecovery(firstRecoveryRun.RunFolder);
            Assert.NotNull(firstRecoveryState);
            Assert.Equal("default_still_suspended", firstRecoveryState!.RecoveryState);

            var reconfirmedRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var reconfirmedClasses = BuilderExecutionService.LoadBuilderConfirmedTaskClasses(reconfirmedRun.RunFolder);
            var reconfirmedEntry = Assert.Single(reconfirmedClasses!.Entries, entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal));
            Assert.Equal("confirmed_for_bounded_use", reconfirmedEntry.CurrentReadinessState);
            Assert.Equal("reconfirmed_after_contradiction", reconfirmedEntry.ReconfirmationStatus);
            Assert.False(reconfirmedEntry.DefaultRouteSuspended);
            Assert.True(reconfirmedEntry.ContradictionCount >= 1);

            var reconfirmedDecision = BuilderExecutionService.LoadBuilderDefaultRouteDecision(reconfirmedRun.RunFolder);
            Assert.NotNull(reconfirmedDecision);
            Assert.Equal("defaulted_by_confirmed_policy", reconfirmedDecision!.RouteSourceState);
            Assert.False(reconfirmedDecision.DefaultRouteSuspended);
            var reconfirmedReconfirmation = BuilderExecutionService.LoadBuilderRouteReconfirmation(reconfirmedRun.RunFolder);
            Assert.NotNull(reconfirmedReconfirmation);
            Assert.Equal("reconfirmed_default_route", reconfirmedReconfirmation!.CurrentReconfirmationState);
            var reconfirmedRecovery = BuilderExecutionService.LoadBuilderDefaultRouteRecovery(reconfirmedRun.RunFolder);
            Assert.NotNull(reconfirmedRecovery);
            Assert.Equal("restored_default_route", reconfirmedRecovery!.RecoveryState);
            Assert.True(reconfirmedRecovery.DefaultRouteRestored);
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
    public async Task Override_only_contradiction_recovers_default_route_with_single_fresh_default_launch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var overrideRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(
                root,
                provider: "ollama",
                routeOverride: "direct_low_floor_route",
                overrideReason: "Override reconfirmation test.");

            var overrideReconfirmation = BuilderExecutionService.LoadBuilderRouteReconfirmation(overrideRun.RunFolder);
            Assert.NotNull(overrideReconfirmation);
            Assert.Equal("override_route_failure", overrideReconfirmation!.ContradictionAttributionState);
            Assert.Equal(1, overrideReconfirmation.RequiredFreshPreparedLaunchConfirmations);
            Assert.Equal("default_still_suspended", overrideReconfirmation.CurrentReconfirmationState);

            var overrideRecovery = BuilderExecutionService.LoadBuilderDefaultRouteRecovery(overrideRun.RunFolder);
            Assert.NotNull(overrideRecovery);
            Assert.Equal("override_route_regressed", overrideRecovery!.SuspensionCauseState);
            Assert.Equal("default_still_suspended", overrideRecovery.RecoveryState);
            Assert.False(overrideRecovery.DefaultRouteRestored);

            var recoveredRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var recoveredDecision = BuilderExecutionService.LoadBuilderDefaultRouteDecision(recoveredRun.RunFolder);
            Assert.NotNull(recoveredDecision);
            Assert.Equal("defaulted_by_confirmed_policy", recoveredDecision!.RouteSourceState);
            Assert.False(recoveredDecision.DefaultRouteSuspended);

            var recoveredReconfirmation = BuilderExecutionService.LoadBuilderRouteReconfirmation(recoveredRun.RunFolder);
            Assert.NotNull(recoveredReconfirmation);
            Assert.Equal("override_route_failure", recoveredReconfirmation!.ContradictionAttributionState);
            Assert.Equal("reconfirmed_default_route", recoveredReconfirmation.CurrentReconfirmationState);
            Assert.Equal(1, recoveredReconfirmation.FreshPreparedLaunchConfirmationCount);

            var recoveredRecovery = BuilderExecutionService.LoadBuilderDefaultRouteRecovery(recoveredRun.RunFolder);
            Assert.NotNull(recoveredRecovery);
            Assert.Equal("restored_default_route", recoveredRecovery!.RecoveryState);
            Assert.True(recoveredRecovery.DefaultRouteRestored);
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
    public async Task Builder_route_current_state_index_carries_forward_authoritative_override_artifacts_after_newer_proof_only_run()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var overrideRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(
                root,
                provider: "ollama",
                routeOverride: "direct_low_floor_route",
                overrideReason: "Continuity carry-forward test.");

            var latestProofOnlyRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var continuity = BuilderExecutionService.LoadBuilderRouteStateContinuity(root);
            Assert.NotNull(continuity);
            Assert.Contains(continuity!.Entries, entry => string.Equals(entry.SourceProofRunId, overrideRun.ProofRunId, StringComparison.Ordinal));
            Assert.Contains(continuity.Entries, entry => string.Equals(entry.SourceProofRunId, latestProofOnlyRun.ProofRunId, StringComparison.Ordinal));

            var index = BuilderExecutionService.LoadBuilderRouteCurrentStateIndex(root);
            Assert.NotNull(index);
            Assert.Equal(latestProofOnlyRun.ProofRunId, index!.LatestProofRunId);

            var launchDecisionEntry = Assert.Single(index.Entries, entry => string.Equals(entry.ArtifactKind, "builder_launch_default_decision", StringComparison.Ordinal));
            Assert.Equal(overrideRun.ProofRunId, launchDecisionEntry.SourceProofRunId);
            Assert.Equal(BuilderExecutionService.BuilderLaunchDefaultDecisionPath(overrideRun.RunFolder), launchDecisionEntry.ArtifactPath);
            Assert.Equal("continuity_carried_forward", launchDecisionEntry.ResolutionState);

            var overrideEvidenceEntry = Assert.Single(index.Entries, entry => string.Equals(entry.ArtifactKind, "builder_route_override_evidence", StringComparison.Ordinal));
            Assert.Equal(overrideRun.ProofRunId, overrideEvidenceEntry.SourceProofRunId);
            Assert.Equal(BuilderExecutionService.BuilderRouteOverrideEvidencePath(overrideRun.RunFolder), overrideEvidenceEntry.ArtifactPath);
            Assert.Equal("continuity_carried_forward", overrideEvidenceEntry.ResolutionState);

            var reviewEntry = Assert.Single(index.Entries, entry => string.Equals(entry.ArtifactKind, "builder_policy_review_candidates", StringComparison.Ordinal));
            Assert.Equal(overrideRun.ProofRunId, reviewEntry.SourceProofRunId);
            Assert.Equal(BuilderExecutionService.BuilderPolicyReviewCandidatesPath(overrideRun.RunFolder), reviewEntry.ArtifactPath);
            Assert.Equal("continuity_carried_forward", reviewEntry.ResolutionState);

            var reconfirmationEntry = Assert.Single(index.Entries, entry => string.Equals(entry.ArtifactKind, "builder_route_reconfirmation", StringComparison.Ordinal));
            Assert.Equal(latestProofOnlyRun.ProofRunId, reconfirmationEntry.SourceProofRunId);
            Assert.Equal(BuilderExecutionService.BuilderRouteReconfirmationPath(latestProofOnlyRun.RunFolder), reconfirmationEntry.ArtifactPath);
            Assert.Equal("latest_run", reconfirmationEntry.ResolutionState);

            var carriedLaunchDecision = BuilderExecutionService.LoadLatestBuilderLaunchDefaultDecision(root);
            Assert.NotNull(carriedLaunchDecision);
            Assert.Equal(BuilderExecutionService.BuilderLaunchDefaultDecisionPath(overrideRun.RunFolder), carriedLaunchDecision!.ArtifactPath);

            var carriedOverrideEvidence = BuilderExecutionService.LoadLatestBuilderRouteOverrideEvidence(root);
            Assert.NotNull(carriedOverrideEvidence);
            Assert.Equal(BuilderExecutionService.BuilderRouteOverrideEvidencePath(overrideRun.RunFolder), carriedOverrideEvidence!.ArtifactPath);

            var carriedReviewCandidates = BuilderExecutionService.LoadLatestBuilderPolicyReviewCandidates(root);
            Assert.NotNull(carriedReviewCandidates);
            Assert.Equal(BuilderExecutionService.BuilderPolicyReviewCandidatesPath(overrideRun.RunFolder), carriedReviewCandidates!.ArtifactPath);

            var latestReconfirmation = BuilderExecutionService.LoadLatestBuilderRouteReconfirmation(root);
            Assert.NotNull(latestReconfirmation);
            Assert.Equal(BuilderExecutionService.BuilderRouteReconfirmationPath(latestProofOnlyRun.RunFolder), latestReconfirmation!.ArtifactPath);
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
    public async Task Builder_route_state_continuity_counts_two_override_reconfirmation_cycles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new AvailableBuilderStrongerTierResolver());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(
                root,
                provider: "ollama",
                routeOverride: "direct_low_floor_route",
                overrideReason: "Continuity override cycle 1.");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var secondOverrideRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(
                root,
                provider: "ollama",
                routeOverride: "direct_low_floor_route",
                overrideReason: "Continuity override cycle 2.");

            var secondOverrideLaunchDecision = BuilderExecutionService.LoadBuilderLaunchDefaultDecision(secondOverrideRun.RunFolder);
            Assert.NotNull(secondOverrideLaunchDecision);
            File.WriteAllText(
                BuilderExecutionService.BuilderLaunchDefaultDecisionPath(secondOverrideRun.RunFolder),
                JsonSerializer.Serialize(
                    secondOverrideLaunchDecision! with
                    {
                        ActualLaunchRoute = "direct_low_floor_route",
                        OperatorDecisionState = "operator_override_selected",
                        OperatorOverrideState = "overridden_by_operator",
                        OverrideReason = "Continuity override cycle 2."
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            var secondOverrideResult = BuilderExecutionService.LoadBuilderExecutionResult(secondOverrideRun.RunFolder);
            Assert.NotNull(secondOverrideResult);
            File.WriteAllText(
                BuilderExecutionService.BuilderExecutionResultPath(secondOverrideRun.RunFolder),
                JsonSerializer.Serialize(
                    secondOverrideResult! with
                    {
                        ActualRouteUsed = "direct_low_floor_route",
                        FinalRouteOutcomeClassification = "launched_and_failed_followup_created",
                        PreparedRouteComparisonState = "insufficient_for_scope",
                        Summary = "Synthetic second override contradiction for continuity coverage."
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            InvokePrivateInstance(service, "RefreshBuilderDefaultPolicyArtifacts", root, secondOverrideRun.RunFolder);

            var secondOverrideEvidence = BuilderExecutionService.LoadBuilderRouteOverrideEvidence(secondOverrideRun.RunFolder);
            Assert.NotNull(secondOverrideEvidence);
            File.WriteAllText(
                BuilderExecutionService.BuilderRouteOverrideEvidencePath(secondOverrideRun.RunFolder),
                JsonSerializer.Serialize(
                    secondOverrideEvidence! with
                    {
                        SelectedRoute = "direct_low_floor_route",
                        OverrideState = "operator_override_selected",
                        OverrideReason = "Continuity override cycle 2.",
                        LaunchOutcomeClassification = "launched_and_failed_followup_created",
                        OverrideOutcomeComparisonState = "regressed_outcome",
                        Summary = "Synthetic second override contradiction for continuity coverage."
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            InvokePrivateInstance(service, "RefreshBuilderRouteRecoveryArtifacts", root, secondOverrideRun.RunFolder);
            InvokePrivateInstance(service, "RefreshBuilderRouteContinuityArtifacts", root);

            var recoveredRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");
            await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");

            var continuity = BuilderExecutionService.LoadBuilderRouteStateContinuity(root);
            Assert.NotNull(continuity);
            Assert.Equal(2, continuity!.OverrideContradictionCycleCount);
            Assert.Equal(2, continuity.ReconfirmationCycleCount);
            Assert.Equal(recoveredRun.ProofRunId, continuity.LatestProofRunId);

            var latestEntry = continuity.Entries.Last();
            Assert.Equal(recoveredRun.ProofRunId, latestEntry.SourceProofRunId);
            Assert.Equal("reconfirmed_default_route", latestEntry.ContinuityState);
            Assert.Equal(2, latestEntry.OverrideContradictionCycleCount);
            Assert.Equal(2, latestEntry.ReconfirmationCycleCount);

            var index = BuilderExecutionService.LoadBuilderRouteCurrentStateIndex(root);
            Assert.NotNull(index);
            Assert.Equal(2, index!.OverrideContradictionCycleCount);
            Assert.Equal(2, index.ReconfirmationCycleCount);
            Assert.Equal("reconfirmed_default_route", index.CurrentReconfirmationState);

            var reconfirmationEntry = Assert.Single(index.Entries, entry => string.Equals(entry.ArtifactKind, "builder_route_reconfirmation", StringComparison.Ordinal));
            Assert.Equal(recoveredRun.ProofRunId, reconfirmationEntry.SourceProofRunId);
            Assert.Equal(BuilderExecutionService.BuilderRouteReconfirmationPath(recoveredRun.RunFolder), reconfirmationEntry.ArtifactPath);
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
    public void RefreshBuilderCapabilityArtifacts_writes_registry_language_eligibility_and_summary()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoLanguagePolicyFiles(root);
            var observedUtc = DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
            var service = CreateBuilderExecutionService(
                capabilityScanner: new ScriptedBuilderToolchainCapabilityScanner(
                    new BuilderToolchainCapabilityObservation(
                        "dotnet",
                        "sdk",
                        @"C:\tools\dotnet\dotnet.exe",
                        "8.0.204",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        observedUtc),
                    new BuilderToolchainCapabilityObservation(
                        "msbuild",
                        "build_tool",
                        @"C:\tools\msbuild\MSBuild.exe",
                        "17.10.1",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        observedUtc),
                    new BuilderToolchainCapabilityObservation(
                        "node",
                        "runtime",
                        @"C:\tools\node\node.exe",
                        "22.5.1",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        observedUtc),
                    new BuilderToolchainCapabilityObservation(
                        "npm",
                        "packaging_tool",
                        @"C:\tools\node\npm.cmd",
                        "10.8.2",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        observedUtc),
                    new BuilderToolchainCapabilityObservation(
                        "python",
                        "runtime",
                        string.Empty,
                        string.Empty,
                        false,
                        false,
                        "not_found",
                        "python is not installed.",
                        observedUtc)));

            var registry = service.RefreshBuilderCapabilityArtifacts(root);

            Assert.Equal("wpf_desktop_dotnet", registry.PreferredStackId);
            Assert.True(File.Exists(BuilderExecutionService.BuilderToolchainCapabilityRegistryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderToolchainCapabilityHistoryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderLanguageEligibilityPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderLanguageEligibilitySummaryPathForRepo(root)));

            var loadedRegistry = BuilderExecutionService.LoadBuilderToolchainCapabilityRegistry(root);
            Assert.NotNull(loadedRegistry);
            var dotnet = Assert.Single(loadedRegistry!.Entries, entry => string.Equals(entry.ToolId, "dotnet", StringComparison.Ordinal));
            Assert.True(dotnet.SupportedByRepo);
            Assert.True(dotnet.PreferredByRepo);
            Assert.Equal("preferred_and_ready", dotnet.UsabilityState);

            var msbuild = Assert.Single(loadedRegistry.Entries, entry => string.Equals(entry.ToolId, "msbuild", StringComparison.Ordinal));
            Assert.True(msbuild.SupportedByRepo);
            Assert.False(msbuild.PreferredByRepo);
            Assert.Equal("approved_but_not_preferred", msbuild.UsabilityState);

            var node = Assert.Single(loadedRegistry.Entries, entry => string.Equals(entry.ToolId, "node", StringComparison.Ordinal));
            Assert.False(node.SupportedByRepo);
            Assert.False(node.PreferredByRepo);
            Assert.Equal("callable_but_repo_blocked", node.UsabilityState);
            Assert.Contains("repo", node.BlockedReason, StringComparison.OrdinalIgnoreCase);

            var eligibility = BuilderExecutionService.LoadBuilderLanguageEligibility(root);
            Assert.NotNull(eligibility);
            Assert.Equal("ready_and_preferred", Assert.Single(eligibility!.Entries, entry => string.Equals(entry.StackId, "wpf_desktop_dotnet", StringComparison.Ordinal)).EligibilityState);
            Assert.Equal("ready_but_not_preferred", Assert.Single(eligibility.Entries, entry => string.Equals(entry.StackId, "csharp_dotnet", StringComparison.Ordinal)).EligibilityState);
            Assert.Equal("installed_but_disallowed", Assert.Single(eligibility.Entries, entry => string.Equals(entry.StackId, "javascript_typescript", StringComparison.Ordinal)).EligibilityState);
            Assert.Equal("unsupported_for_repo", Assert.Single(eligibility.Entries, entry => string.Equals(entry.StackId, "java", StringComparison.Ordinal)).EligibilityState);

            var summaryMarkdown = File.ReadAllText(BuilderExecutionService.BuilderLanguageEligibilitySummaryPathForRepo(root));
            Assert.Contains("WPF/Desktop .NET", summaryMarkdown, StringComparison.Ordinal);
            Assert.Contains("available but not preferred", summaryMarkdown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blocked or unsupported", summaryMarkdown, StringComparison.OrdinalIgnoreCase);
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
    public void RefreshBuilderCapabilityArtifacts_records_drift_changes_in_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoLanguagePolicyFiles(root);
            var scanner = new SequencedBuilderToolchainCapabilityScanner(
                new[]
                {
                    new BuilderToolchainCapabilityObservation(
                        "dotnet",
                        "sdk",
                        @"C:\tools\dotnet\dotnet.exe",
                        "8.0.204",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture))
                },
                new[]
                {
                    new BuilderToolchainCapabilityObservation(
                        "dotnet",
                        "sdk",
                        @"C:\tools\dotnet\dotnet.exe",
                        "8.0.300",
                        true,
                        false,
                        "probe_failed",
                        "dotnet --version failed.",
                        DateTimeOffset.Parse("2026-03-13T18:05:00+00:00", System.Globalization.CultureInfo.InvariantCulture))
                });
            var service = CreateBuilderExecutionService(capabilityScanner: scanner);

            service.RefreshBuilderCapabilityArtifacts(root);
            var refreshed = service.RefreshBuilderCapabilityArtifacts(root);

            Assert.Equal("changed", refreshed.DriftState);
            Assert.Contains("dotnet", refreshed.ChangedToolIds);
            Assert.Contains(refreshed.ChangeSummaries, entry => entry.Contains("version", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(refreshed.ChangeSummaries, entry => entry.Contains("callable", StringComparison.OrdinalIgnoreCase));

            var history = BuilderExecutionService.LoadBuilderToolchainCapabilityHistory(root);
            Assert.NotNull(history);
            Assert.Equal(2, history!.Entries.Count);
            Assert.Equal("changed", history.Entries[0].DriftState);
            Assert.Contains("dotnet", history.Entries[0].ChangedToolIds);
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
    public void RefreshBuilderRepoKnowledgeArtifacts_writes_index_summary_and_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService(
                capabilityScanner: new ScriptedBuilderToolchainCapabilityScanner(
                    new BuilderToolchainCapabilityObservation(
                        "dotnet",
                        "sdk",
                        @"C:\tools\dotnet\dotnet.exe",
                        "8.0.204",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture)),
                    new BuilderToolchainCapabilityObservation(
                        "msbuild",
                        "build_tool",
                        @"C:\tools\msbuild\MSBuild.exe",
                        "17.10.1",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture))));

            service.RefreshBuilderCapabilityArtifacts(root);
            var index = service.RefreshBuilderRepoKnowledgeArtifacts(root);

            Assert.Equal("wpf_desktop_dotnet", index.PreferredStackId);
            Assert.True(File.Exists(BuilderExecutionService.BuilderRepoKnowledgeIndexPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRepoKnowledgeSummaryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderRepoKnowledgeHistoryPathForRepo(root)));

            var loaded = BuilderExecutionService.LoadBuilderRepoKnowledgeIndex(root);
            Assert.NotNull(loaded);
            var uiProject = Assert.Single(loaded!.ProjectEntries, entry => string.Equals(entry.ProjectName, "Shoots.Ui", StringComparison.Ordinal));
            Assert.Equal("wpf_desktop_app", uiProject.ProjectType);
            Assert.Contains(uiProject.RelatedUiSurfaces, item => string.Equals(item.RelativePath, Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"), StringComparison.Ordinal));
            Assert.Contains(uiProject.RelatedViewModels, item => string.Equals(item.RelativePath, Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"), StringComparison.Ordinal));
            Assert.Contains(uiProject.RelatedServices, item => string.Equals(item.RelativePath, Path.Combine("ui", "Shoots.Ui", "Services", "ValidationRunnerService.cs"), StringComparison.Ordinal));
            Assert.Contains(uiProject.RelatedBuilderFiles, item => string.Equals(item.RelativePath, Path.Combine("ui", "Shoots.Ui", "Builder", "BuilderExecutionService.cs"), StringComparison.Ordinal));
            Assert.Contains(uiProject.RelatedTests, item => string.Equals(item.RelativePath, Path.Combine("ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj"), StringComparison.Ordinal));
            Assert.Contains(
                loaded.FileOwnershipSummaries,
                summary => string.Equals(summary.RelativePath, Path.Combine("ui", "Shoots.Ui", "Builder"), StringComparison.Ordinal) &&
                           string.Equals(summary.OwnerProjectId, uiProject.ProjectId, StringComparison.Ordinal));

            var summaryMarkdown = File.ReadAllText(BuilderExecutionService.BuilderRepoKnowledgeSummaryPathForRepo(root));
            Assert.Contains("WPF/Desktop .NET", summaryMarkdown, StringComparison.Ordinal);
            Assert.Contains("builder", summaryMarkdown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("runtime", summaryMarkdown, StringComparison.OrdinalIgnoreCase);
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
    public void RefreshBuilderRepoKnowledgeArtifacts_records_structure_drift()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService();

            service.RefreshBuilderRepoKnowledgeArtifacts(root);

            Directory.CreateDirectory(Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Loader"));
            File.WriteAllText(
                Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Loader", "Shoots.Runtime.Loader.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Loader", "RuntimeLoader.cs"),
                "namespace Shoots.Runtime.Loader; public sealed class RuntimeLoader { }");

            var refreshed = service.RefreshBuilderRepoKnowledgeArtifacts(root);

            Assert.Equal("changed", refreshed.DriftState);
            Assert.Contains("Shoots.Runtime.Loader", refreshed.ChangedProjectIds);

            var drift = BuilderExecutionService.LoadBuilderRepoKnowledgeDrift(root);
            Assert.NotNull(drift);
            Assert.Contains("Shoots.Runtime.Loader", drift!.AddedProjectIds);

            var history = BuilderExecutionService.LoadBuilderRepoKnowledgeHistory(root);
            Assert.NotNull(history);
            Assert.Equal(2, history!.Entries.Count);
            Assert.Equal("changed", history.Entries[0].DriftState);
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
    public async Task PreviewBuilderConversationIntake_writes_retrieval_context_and_resolves_preferred_stack_for_strong_match()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var intake = service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");

            Assert.True(File.Exists(BuilderExecutionService.BuilderRepoRetrievalContextPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderConversationIntakePathForRepo(root)));
            Assert.Equal("wpf_desktop_dotnet", intake.ImpliedStackId);
            Assert.Equal("strong_match", intake.RetrievalConfidenceState);
            Assert.Equal("route_allowed", intake.CapabilityRoutingState);
            Assert.False(string.IsNullOrWhiteSpace(intake.SelectedRoute));

            var defaultRouteDecision = BuilderExecutionService.LoadLatestBuilderDefaultRouteDecision(root);
            Assert.NotNull(defaultRouteDecision);
            Assert.Equal(defaultRouteDecision!.ChosenDefaultRoute, intake.SelectedRoute);

            var retrieval = BuilderExecutionService.LoadBuilderRepoRetrievalContext(root);
            Assert.NotNull(retrieval);
            Assert.Contains(retrieval!.MatchedUiSurfaces, path => string.Equals(path, Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"), StringComparison.Ordinal));
            Assert.Contains(retrieval.MatchedViewModels, path => string.Equals(path, Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"), StringComparison.Ordinal));
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
    public async Task PreviewBuilderConversationIntake_blocks_disallowed_stack_requests()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService(
                new SuccessfulBuilderProofCommandRunner(),
                capabilityScanner: new ScriptedBuilderToolchainCapabilityScanner(
                    new BuilderToolchainCapabilityObservation(
                        "dotnet",
                        "sdk",
                        @"C:\tools\dotnet\dotnet.exe",
                        "8.0.204",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture)),
                    new BuilderToolchainCapabilityObservation(
                        "java",
                        "runtime",
                        @"C:\tools\java\java.exe",
                        "21.0.2",
                        true,
                        true,
                        "probe_succeeded",
                        string.Empty,
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture))));

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var intake = service.PreviewBuilderConversationIntake(root, "Add a Java service for builder routing.");

            Assert.Equal("java", intake.ImpliedStackId);
            Assert.Equal("route_blocked_repo_policy", intake.CapabilityRoutingState);
            Assert.Equal("launch_blocked_capability", intake.LaunchReadinessState);
            Assert.Contains("blocked", intake.Summary, StringComparison.OrdinalIgnoreCase);
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
    public async Task CreateBuilderConversationHandoff_requires_override_for_weak_match_requests()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var intake = service.PreviewBuilderConversationIntake(root, "Do the thing.");
            Assert.True(
                string.Equals(intake.RetrievalConfidenceState, "weak_match_needs_operator_review", StringComparison.Ordinal) ||
                string.Equals(intake.RetrievalConfidenceState, "no_clear_match", StringComparison.Ordinal));

            var blocked = service.CreateBuilderConversationHandoff(root, "accept_suggested_route");
            Assert.Equal("launch_blocked_weak_match", blocked.LaunchReadinessState);
            Assert.Contains("weak", blocked.BlockReason, StringComparison.OrdinalIgnoreCase);

            var routeOverride = string.Equals(intake.SelectedRoute, "direct_low_floor_route", StringComparison.Ordinal)
                ? "split_first_low_floor_route"
                : "direct_low_floor_route";
            var approved = service.CreateBuilderConversationHandoff(
                root,
                "override_route",
                routeOverride,
                "Operator confirmed the builder area manually.");

            Assert.Equal("ready_for_launch_with_override", approved.LaunchReadinessState);
            Assert.Equal(routeOverride, approved.SelectedRoute);
            Assert.Contains(BuilderExecutionService.BuilderConversationIntakePathForRepo(root), approved.LinkedArtifactPaths);
            Assert.True(File.Exists(BuilderExecutionService.BuilderConversationHandoffPathForRepo(root)));
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
    public async Task RunBuilderConversationExecutionSessionAsync_writes_session_patch_review_and_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            var intake = service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");
            var handoff = service.CreateBuilderConversationHandoff(root, "accept_suggested_route");

            var session = await service.RunBuilderConversationExecutionSessionAsync(root, provider: "ollama");

            Assert.Equal("awaiting_patch_review", session.SessionState);
            Assert.Equal("awaiting_operator_review", session.CurrentStageId);
            Assert.Equal("pending_operator_review", session.ReviewState);
            Assert.Equal(handoff.SelectedRoute, session.SelectedRoute);
            Assert.Equal(intake.ImpliedStackId, session.StackId);
            Assert.True(File.Exists(BuilderExecutionService.BuilderConversationExecutionSessionPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchReviewPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderConversationExecutionHistoryPathForRepo(root)));
            Assert.NotEmpty(session.ChangedFiles);

            var patchReview = BuilderExecutionService.LoadBuilderPatchReview(root);
            Assert.NotNull(patchReview);
            Assert.Equal("ready_for_operator_review", patchReview!.ReviewReadinessState);
            Assert.NotEmpty(patchReview.ChangedFiles);
            Assert.All(
                patchReview.ChangedFiles,
                file =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(file.Path));
                    Assert.False(string.IsNullOrWhiteSpace(file.ChangeKind));
                    Assert.False(string.IsNullOrWhiteSpace(file.ChangeSummary));
                });

            var history = BuilderExecutionService.LoadBuilderConversationExecutionHistory(root);
            Assert.NotNull(history);
            Assert.NotEmpty(history!.Entries);
            Assert.Equal(session.SessionId, history.Entries[0].SessionId);
            Assert.Contains(BuilderExecutionService.BuilderPatchReviewPathForRepo(root), session.LinkedArtifactPaths);
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
    public async Task RecordBuilderPatchReviewOutcome_tracks_revision_and_reroute_states()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");
            service.CreateBuilderConversationHandoff(root, "accept_suggested_route");
            await service.RunBuilderConversationExecutionSessionAsync(root, provider: "ollama");

            var revisionOutcome = service.RecordBuilderPatchReviewOutcome(
                root,
                "revise_requested",
                "Need a tighter bounded change before completion.");

            Assert.Equal("revise_requested", revisionOutcome.ReviewDecisionState);
            Assert.Equal("rejected_for_revision", revisionOutcome.SessionState);
            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchReviewOutcomePathForRepo(root)));

            var revisedSession = BuilderExecutionService.LoadBuilderConversationExecutionSession(root);
            Assert.NotNull(revisedSession);
            Assert.Equal("rejected_for_revision", revisedSession!.SessionState);
            Assert.Equal("revise_requested", revisedSession.ReviewState);

            var reviewSummary = File.ReadAllText(BuilderExecutionService.BuilderConversationReviewSummaryPathForRepo(root));
            Assert.Contains("revision", reviewSummary, StringComparison.OrdinalIgnoreCase);

            service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");
            service.CreateBuilderConversationHandoff(root, "accept_suggested_route");
            await service.RunBuilderConversationExecutionSessionAsync(root, provider: "ollama");

            var rerouteOutcome = service.RecordBuilderPatchReviewOutcome(
                root,
                "reroute_requested",
                "Compare the direct route before completion.",
                "direct_low_floor_route");

            Assert.Equal("reroute_requested", rerouteOutcome.ReviewDecisionState);
            Assert.Equal("rerouted", rerouteOutcome.SessionState);
            Assert.Contains(BuilderExecutionService.BuilderConversationHandoffPathForRepo(root), rerouteOutcome.LinkedArtifactPaths);

            var reroutedSession = BuilderExecutionService.LoadBuilderConversationExecutionSession(root);
            Assert.NotNull(reroutedSession);
            Assert.Equal("rerouted", reroutedSession!.SessionState);
            Assert.Equal("reroute_requested", reroutedSession.ReviewState);

            var handoff = BuilderExecutionService.LoadBuilderConversationHandoff(root);
            Assert.NotNull(handoff);
            Assert.Equal("override_route", handoff!.OperatorDecisionState);
            Assert.Equal("direct_low_floor_route", handoff.SelectedRoute);
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
    public async Task RunBuilderConversationExecutionSessionAsync_writes_patch_diff_review_with_pending_file_states()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoKnowledgeFiles(root);
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());

            await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            await service.RunBuilderComparativeProofAsync(root, provider: "ollama");

            service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");
            service.CreateBuilderConversationHandoff(root, "accept_suggested_route");
            await service.RunBuilderConversationExecutionSessionAsync(root, provider: "ollama");

            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchDiffReviewPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchApplyDecisionPathForRepo(root)));

            var patchDiffReview = BuilderExecutionService.LoadBuilderPatchDiffReview(root);
            Assert.NotNull(patchDiffReview);
            Assert.Equal("all_files_pending", patchDiffReview!.OverallFileReviewState);
            Assert.Equal("ready_for_operator_review", patchDiffReview.ReviewReadinessState);
            Assert.NotEmpty(patchDiffReview.FileEntries);
            Assert.All(
                patchDiffReview.FileEntries,
                file =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(file.RelativePath));
                    Assert.False(string.IsNullOrWhiteSpace(file.DiffSummary));
                    Assert.False(string.IsNullOrWhiteSpace(file.PatchPreviewText));
                    Assert.Equal("pending_review", file.ApprovalState);
                });

            var applyDecision = BuilderExecutionService.LoadBuilderPatchApplyDecision(root);
            Assert.NotNull(applyDecision);
            Assert.Equal("all_files_pending", applyDecision!.OverallFileApprovalState);
            Assert.Equal("not_ready", applyDecision.ApplyEligibilityState);
            Assert.Equal("not_ready_to_apply", applyDecision.FinalizationState);
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
    public void RecordBuilderPatchFileReviewDecision_tracks_mixed_states_and_blocks_apply()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService();

            service.RecordBuilderPatchFileReviewDecision(
                root,
                Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"),
                "approved",
                "operator_file_approve");
            service.RecordBuilderPatchFileReviewDecision(
                root,
                Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"),
                "rejected",
                "operator_file_reject",
                "View-model changes need revision.");

            var patchDiffReview = BuilderExecutionService.LoadBuilderPatchDiffReview(root);
            Assert.NotNull(patchDiffReview);
            Assert.Equal("rejected_file_present", patchDiffReview!.OverallFileReviewState);
            Assert.Contains(
                patchDiffReview.FileEntries,
                entry => string.Equals(entry.RelativePath, Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"), StringComparison.Ordinal) &&
                         string.Equals(entry.ApprovalState, "approved", StringComparison.Ordinal));
            Assert.Contains(
                patchDiffReview.FileEntries,
                entry => string.Equals(entry.RelativePath, Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"), StringComparison.Ordinal) &&
                         string.Equals(entry.ApprovalState, "rejected", StringComparison.Ordinal) &&
                         string.Equals(entry.RejectionReason, "View-model changes need revision.", StringComparison.Ordinal));

            var fileReviewDecision = BuilderExecutionService.LoadBuilderFileReviewDecision(root);
            Assert.NotNull(fileReviewDecision);
            Assert.Equal("rejected_file_present", fileReviewDecision!.OverallFileReviewState);
            Assert.Equal(2, fileReviewDecision.Entries.Count);

            var applyDecision = BuilderExecutionService.LoadBuilderPatchApplyDecision(root);
            Assert.NotNull(applyDecision);
            Assert.Equal("rejected_file_present", applyDecision!.OverallFileApprovalState);
            Assert.Equal("blocked", applyDecision.ApplyEligibilityState);
            Assert.Equal("blocked_by_file_rejection", applyDecision.FinalizationState);
            Assert.Contains(applyDecision.BlockReasons, reason => reason.Contains("revision", StringComparison.OrdinalIgnoreCase));
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
    public void FinalizeBuilderApprovedPatch_requires_all_files_approved_and_marks_session_accepted()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService();

            service.ApproveAllBuilderPatchFiles(root);
            var applyDecision = service.FinalizeBuilderApprovedPatch(root);

            Assert.Equal("ready_to_apply", applyDecision.OverallFileApprovalState);
            Assert.Equal("ready", applyDecision.ApplyEligibilityState);
            Assert.Equal("applied_with_operator_approval", applyDecision.FinalizationState);
            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchApplyDecisionPathForRepo(root)));

            var patchReviewOutcome = BuilderExecutionService.LoadBuilderPatchReviewOutcome(root);
            Assert.NotNull(patchReviewOutcome);
            Assert.Equal("accepted", patchReviewOutcome!.ReviewDecisionState);

            var updatedSession = BuilderExecutionService.LoadBuilderConversationExecutionSession(root);
            Assert.NotNull(updatedSession);
            Assert.Equal("accepted_for_completion", updatedSession!.SessionState);
            Assert.Equal("accepted", updatedSession.ReviewState);
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
    public void FinalizeBuilderApprovedPatch_writes_snapshot_and_history_for_accepted_files()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService();

            service.ApproveAllBuilderPatchFiles(root);
            service.FinalizeBuilderApprovedPatch(root);

            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchSnapshotPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchSnapshotHistoryPathForRepo(root)));

            var snapshot = BuilderExecutionService.LoadBuilderPatchSnapshot(root);
            Assert.NotNull(snapshot);
            Assert.Equal("session-1", snapshot!.ExecutionSessionId);
            Assert.Equal("applied_with_operator_approval", snapshot.OperatorApprovalState);
            Assert.Equal("split_first_low_floor_route", snapshot.RouteId);
            Assert.Equal("wpf_desktop_dotnet", snapshot.StackId);
            Assert.Equal(2, snapshot.ApprovedFiles.Count);
            Assert.All(
                snapshot.ApprovedFiles,
                file =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(file.RelativePath));
                    Assert.False(string.IsNullOrWhiteSpace(file.Checksum));
                    Assert.Equal("approved", file.ApprovalState);
                });

            var history = BuilderExecutionService.LoadBuilderPatchSnapshotHistory(root);
            Assert.NotNull(history);
            Assert.Contains(history!.Entries, entry => string.Equals(entry.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal));
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
    public void PrepareBuilderCommitProposal_generates_deterministic_message_for_approved_patch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService();

            service.ApproveAllBuilderPatchFiles(root);
            service.FinalizeBuilderApprovedPatch(root);
            var proposal = service.PrepareBuilderCommitProposal(root);

            Assert.True(File.Exists(BuilderExecutionService.BuilderCommitProposalPathForRepo(root)));
            Assert.Equal("session-1", proposal.ExecutionSessionId);
            Assert.Equal(2, proposal.ChangedFiles.Count);
            Assert.Contains("Shoots Builder Accepted Patch", proposal.ProposedCommitMessage, StringComparison.Ordinal);
            Assert.Contains("Route: split_first_low_floor_route", proposal.ProposedCommitMessage, StringComparison.Ordinal);
            Assert.Contains("Stack: wpf_desktop_dotnet", proposal.ProposedCommitMessage, StringComparison.Ordinal);
            Assert.Contains("Session: session-1", proposal.ProposedCommitMessage, StringComparison.Ordinal);
            Assert.Contains("Files: 2", proposal.ProposedCommitMessage, StringComparison.Ordinal);
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
    public void ExportBuilderApprovedPatchBundle_writes_bundle_deterministically_and_updates_snapshot_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService();

            service.ApproveAllBuilderPatchFiles(root);
            service.FinalizeBuilderApprovedPatch(root);

            var firstExport = service.ExportBuilderApprovedPatchBundle(root);
            var firstBundleText = File.ReadAllText(BuilderExecutionService.BuilderPatchBundlePathForRepo(root));
            var secondExport = service.ExportBuilderApprovedPatchBundle(root);
            var secondBundleText = File.ReadAllText(BuilderExecutionService.BuilderPatchBundlePathForRepo(root));

            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchExportPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderPatchBundlePathForRepo(root)));
            Assert.Equal(2, firstExport.FileCount);
            Assert.Equal(firstBundleText, secondBundleText);
            Assert.Contains("--- a/ui/Shoots.Ui/MainWindow.xaml", firstBundleText, StringComparison.Ordinal);
            Assert.Contains("+++ b/ui/Shoots.Ui/MainWindow.xaml", firstBundleText, StringComparison.Ordinal);

            var history = BuilderExecutionService.LoadBuilderPatchSnapshotHistory(root);
            Assert.NotNull(history);
            Assert.Contains(
                history!.Entries,
                entry => string.Equals(entry.ExportBundlePath, firstExport.BundleFilePath, StringComparison.Ordinal));

            Assert.Equal(firstExport.BundleFilePath, secondExport.BundleFilePath);
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
    public void PrepareBuilderOutputHandoff_writes_manual_apply_guidance_without_requiring_git()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService(
                gitReadinessProbe: new ScriptedBuilderGitReadinessProbe(
                    new BuilderGitReadinessObservation(
                        false,
                        string.Empty,
                        false,
                        false,
                        "unknown",
                        "blocked_git_missing_repo",
                        new[] { "No Git repository was detected for the approved patch handoff." },
                        DateTimeOffset.Parse("2026-03-14T09:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture))));

            service.ApproveAllBuilderPatchFiles(root);
            service.FinalizeBuilderApprovedPatch(root);
            var handoff = service.PrepareBuilderOutputHandoff(root);

            Assert.True(File.Exists(BuilderExecutionService.BuilderOutputHandoffPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderManualApplyGuidancePathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderOutputHandoffSummaryPathForRepo(root)));
            Assert.Equal("ready_for_manual_apply", handoff.HandoffReadinessState);
            Assert.Equal("blocked_git_missing_repo", handoff.OptionalGitReadinessState);

            var manualApply = BuilderExecutionService.LoadBuilderManualApplyGuidance(root);
            Assert.NotNull(manualApply);
            Assert.Equal(2, manualApply!.ApprovedFiles.Count);
            Assert.NotEmpty(manualApply.ApplySteps);

            var gitReadiness = BuilderExecutionService.LoadBuilderGitHandoffReadiness(root);
            Assert.NotNull(gitReadiness);
            Assert.False(gitReadiness!.RepoDetected);
            Assert.Equal("blocked_git_missing_repo", gitReadiness.ReadinessClassification);

            var history = BuilderExecutionService.LoadBuilderOutputHandoffHistory(root);
            Assert.NotNull(history);
            Assert.Contains(history!.Entries, entry => string.Equals(entry.SnapshotId, handoff.SnapshotId, StringComparison.Ordinal));
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
    public void PrepareBuilderOutputHandoff_records_optional_git_ready_state_when_probe_is_clean()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService(
                gitReadinessProbe: new ScriptedBuilderGitReadinessProbe(
                    new BuilderGitReadinessObservation(
                        true,
                        "main",
                        true,
                        true,
                        "ahead_behind_unknown",
                        "ready_for_optional_git_handoff",
                        Array.Empty<string>(),
                        DateTimeOffset.Parse("2026-03-14T09:15:00+00:00", System.Globalization.CultureInfo.InvariantCulture))));

            service.ApproveAllBuilderPatchFiles(root);
            service.FinalizeBuilderApprovedPatch(root);
            var handoff = service.PrepareBuilderOutputHandoff(root);

            Assert.Equal("ready_for_optional_git_handoff", handoff.HandoffReadinessState);
            Assert.Equal("ready_for_optional_git_handoff", handoff.OptionalGitReadinessState);

            var gitReadiness = BuilderExecutionService.LoadBuilderGitHandoffReadiness(root);
            Assert.NotNull(gitReadiness);
            Assert.True(gitReadiness!.RepoDetected);
            Assert.Equal("main", gitReadiness.BranchName);
            Assert.True(gitReadiness.WorkingTreeStateKnown);
            Assert.True(gitReadiness.WorkingTreeClean);

            var gitCommitHandoff = BuilderExecutionService.LoadBuilderGitCommitHandoff(root);
            Assert.NotNull(gitCommitHandoff);
            Assert.Equal("ready_for_optional_git_handoff", gitCommitHandoff!.ReadinessClassification);
            Assert.Contains("Shoots Builder Accepted Patch", gitCommitHandoff.ProposedCommitMessage, StringComparison.Ordinal);
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
    public void PrepareBuilderOutputHandoff_keeps_manual_apply_ready_when_git_tree_is_dirty()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService(
                gitReadinessProbe: new ScriptedBuilderGitReadinessProbe(
                    new BuilderGitReadinessObservation(
                        true,
                        "feature/dirty-tree",
                        true,
                        false,
                        "unknown",
                        "blocked_git_dirty_tree",
                        new[] { "Git working tree is dirty and should be reviewed before using the commit handoff." },
                        DateTimeOffset.Parse("2026-03-14T09:30:00+00:00", System.Globalization.CultureInfo.InvariantCulture))));

            service.ApproveAllBuilderPatchFiles(root);
            service.FinalizeBuilderApprovedPatch(root);
            var handoff = service.PrepareBuilderOutputHandoff(root);

            Assert.Equal("ready_for_manual_apply", handoff.HandoffReadinessState);
            Assert.Equal("blocked_git_dirty_tree", handoff.OptionalGitReadinessState);
            Assert.Contains(handoff.BlockReasons, reason => reason.Contains("dirty", StringComparison.OrdinalIgnoreCase));

            var manualApply = BuilderExecutionService.LoadBuilderManualApplyGuidance(root);
            Assert.NotNull(manualApply);
            Assert.Contains(manualApply!.Warnings, warning => warning.Contains("dirty", StringComparison.OrdinalIgnoreCase));

            var gitCommitHandoff = BuilderExecutionService.LoadBuilderGitCommitHandoff(root);
            Assert.NotNull(gitCommitHandoff);
            Assert.Equal("blocked_git_dirty_tree", gitCommitHandoff!.ReadinessClassification);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("csharp_dotnet", "csharp_dotnet", "ready_but_not_preferred", "route_allowed_but_not_preferred")]
    [InlineData("csharp_dotnet", "wpf_desktop_dotnet", "ready_and_preferred", "route_redirected_to_preferred_stack")]
    [InlineData("javascript_typescript", "javascript_typescript", "installed_but_disallowed", "route_blocked_repo_policy")]
    [InlineData("csharp_dotnet", "csharp_dotnet", "unavailable", "route_blocked_missing_toolchain")]
    public void Builder_capability_routing_states_map_repo_preference_and_blocks(
        string requestedStackId,
        string effectiveStackId,
        string eligibilityState,
        string expectedState)
    {
        var actual = InvokePrivateStatic<string>(
            "DetermineBuilderCapabilityRoutingState",
            requestedStackId,
            effectiveStackId,
            eligibilityState);

        Assert.Equal(expectedState, actual);
    }

    [Fact]
    public async Task LaunchPreparedBuilderRouteAsync_blocks_when_required_repo_toolchain_is_unavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedBuilderRepoLanguagePolicyFiles(root);
            var service = CreateBuilderExecutionService(
                new SuccessfulBuilderProofCommandRunner(),
                new AvailableBuilderStrongerTierResolver(),
                new ScriptedBuilderToolchainCapabilityScanner(
                    new BuilderToolchainCapabilityObservation(
                        "dotnet",
                        "sdk",
                        string.Empty,
                        string.Empty,
                        false,
                        false,
                        "not_found",
                        "dotnet is not installed.",
                        DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture))));

            var latestRun = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");

            var intake = BuilderExecutionService.LoadBuilderRequestIntake(latestRun.RunFolder);
            var prep = BuilderExecutionService.LoadBuilderExecutionPrep(latestRun.RunFolder);
            Assert.NotNull(intake);
            Assert.NotNull(prep);
            Assert.Equal("route_blocked_missing_toolchain", intake!.CapabilityRoutingState);
            Assert.Equal("route_blocked_missing_toolchain", prep!.CapabilityRoutingState);
            Assert.Equal("unavailable", intake.LanguageEligibilityState);
            Assert.Equal("unavailable", prep.LanguageEligibilityState);

            var blockDecision = BuilderExecutionService.LoadBuilderCapabilityBlockDecision(latestRun.RunFolder);
            Assert.NotNull(blockDecision);
            Assert.Equal("route_blocked_missing_toolchain", blockDecision!.RoutingDecisionState);
            Assert.Contains("dotnet", blockDecision.BlockReason, StringComparison.OrdinalIgnoreCase);

            var result = await service.LaunchPreparedBuilderRouteAsync(root, provider: "ollama");
            Assert.Equal("launch_blocked", result.FinalRouteOutcomeClassification);

            var launchDecision = BuilderExecutionService.LoadBuilderLaunchDefaultDecision(latestRun.RunFolder);
            Assert.NotNull(launchDecision);
            Assert.Equal("blocked_missing_toolchain", launchDecision!.LaunchEligibilityState);
            Assert.Contains("toolchain", launchDecision.BlockReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("direct_low_floor_route", true)]
    [InlineData("split_first_low_floor_route", true)]
    [InlineData("low_floor_with_repair_loop_route", true)]
    [InlineData("current_model_with_optional_stronger_tier_route", true)]
    [InlineData("stronger_tier_recommended_route", false)]
    [InlineData("task_out_of_scope_route", false)]
    public void Prepared_builder_launch_route_support_is_bounded(string route, bool expected)
    {
        var actual = InvokePrivateStatic<bool>("IsBuilderPreparedRouteSupported", route);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("direct_low_floor", "ready_for_direct_low_floor", "direct_low_floor_route", false)]
    [InlineData("stronger_tier_required", "task_out_of_scope", "task_out_of_scope_route", false)]
    public void Builder_intake_and_execution_prep_classify_synthetic_routes(
        string chosenPolicyState,
        string expectedIntakeState,
        string expectedRoute,
        bool expectedSplitPlanRequired)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var runFolder = Path.Combine(root, "run");
            Directory.CreateDirectory(runFolder);

            var decisionPath = Path.Combine(runFolder, "builder_request_policy_decision.json");
            var stabilityPath = Path.Combine(runFolder, "builder_policy_stability.json");
            var comparativePath = Path.Combine(runFolder, "comparative.json");
            var capabilityPath = Path.Combine(runFolder, "builder_capability_block_decision.json");
            File.WriteAllText(decisionPath, "{}");
            File.WriteAllText(stabilityPath, "{}");
            File.WriteAllText(comparativePath, "{}");
            File.WriteAllText(capabilityPath, "{}");

            var complexity = new BuilderProofComplexityDimensions(
                FileCountTouched: 2,
                ProjectCountTouched: 1,
                DependencyReferenceChangeCount: 0,
                TestChangesRequired: false,
                NewFileCreationCount: 0,
                PromptAmbiguity: "low");
            var requestDecision = new BuilderRequestPolicyDecision(
                "proof-run-1",
                "synthetic_test_target",
                BuilderExecutionService.BuilderProofFloorModelId,
                "repo_local",
                "synthetic-target",
                "Synthetic target",
                "compile_fix",
                complexity,
                chosenPolicyState,
                chosenPolicyState == "stronger_tier_required" ? "recommended_for_scope" : "optional_for_cleaner_success",
                string.Equals(chosenPolicyState, "split_first_low_floor", StringComparison.Ordinal),
                "No linked weak-spot likelihood recorded.",
                new[] { "Synthetic reason." },
                new[] { decisionPath },
                "Synthetic routing decision summary.",
                decisionPath,
                DateTimeOffset.UtcNow);
            var stability = new BuilderPolicyStability(
                "proof-run-1",
                BuilderExecutionService.BuilderProofFloorModelId,
                "compile_fix",
                chosenPolicyState,
                "provisional",
                1,
                0,
                new[] { comparativePath },
                "Synthetic support summary.",
                stabilityPath,
                DateTimeOffset.UtcNow);
            var comparativeHook = new BuilderModelComparativeProofHook(
                "repo_local|compile_fix",
                "compile_fix",
                BuilderExecutionService.BuilderProofFloorModelId,
                "clean_build_band",
                "stronger_builder_tier",
                "repo_local",
                "synthetic-target",
                comparativePath,
                Path.Combine(runFolder, "future-comparison.json"),
                "Synthetic comparative hook.");
            var routingPlan = new BuilderModelRoutingPlan(
                BuilderExecutionService.BuilderProofFloorModelId,
                BuilderExecutionService.BuilderProofFloorModelId,
                "repo_local",
                "synthetic-target",
                "Synthetic target",
                "compile_fix",
                "clean_build_band",
                chosenPolicyState == "stronger_tier_required" ? "task_out_of_scope_for_floor" : "proceed_with_current_model",
                chosenPolicyState == "stronger_tier_required" ? "stronger_model_required" : "stay_on_current_model",
                BuilderExecutionService.BuilderProofFloorModelId,
                "stronger_builder_tier",
                chosenPolicyState == "stronger_tier_required" ? "split_then_escalate" : "no_split_needed",
                "The bounded task exceeds the proven low-floor envelope.",
                Array.Empty<string>(),
                string.Empty,
                "No linked builder weak-spot reason recorded.",
                new[] { "Synthetic reason." },
                new[] { comparativePath },
                comparativeHook,
                "Synthetic routing plan.",
                Path.Combine(runFolder, "builder_model_routing_plan.json"),
                DateTimeOffset.UtcNow);
            var defaultRouteDecision = new BuilderDefaultRouteDecision(
                "proof-run-1",
                "synthetic-target",
                "Synthetic target",
                "compile_fix",
                expectedRoute,
                "suggested",
                "override_available_no_override",
                false,
                0,
                0,
                "Synthetic default route reason.",
                new[] { decisionPath },
                "Synthetic default route decision summary.",
                Path.Combine(runFolder, "builder_default_route_decision.json"),
                DateTimeOffset.UtcNow);
            var capabilityDecision = new BuilderCapabilityBlockDecision(
                "proof-run-1",
                "compile_fix",
                "repo_local",
                "csharp_dotnet",
                "C# / .NET",
                "csharp_dotnet",
                "C# / .NET",
                "callable dotnet SDK",
                "callable",
                "ready_but_not_preferred",
                "route_allowed_but_not_preferred",
                expectedRoute,
                "csharp_dotnet",
                string.Empty,
                new[] { capabilityPath },
                "Synthetic capability decision.",
                capabilityPath,
                DateTimeOffset.UtcNow);

            var intake = InvokePrivateStatic<BuilderRequestIntake>(
                "BuildBuilderRequestIntake",
                runFolder,
                requestDecision,
                stability,
                routingPlan,
                null,
                null,
                null,
                defaultRouteDecision,
                capabilityDecision);
            var prep = InvokePrivateStatic<BuilderExecutionPrep>(
                "BuildBuilderExecutionPrep",
                runFolder,
                intake,
                requestDecision,
                stability,
                routingPlan,
                null,
                null,
                null,
                defaultRouteDecision,
                capabilityDecision);

            Assert.Equal(expectedIntakeState, intake.IntakeClassificationState);
            Assert.Equal(expectedRoute, prep.SelectedRoute);
            Assert.Equal(expectedSplitPlanRequired, prep.SplitPlanRequired);
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
    public async Task RunBuilderComparativeProofAsync_blocks_when_stronger_tier_is_unavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new FakeBuilderProofCommandRunner(), new UnavailableBuilderStrongerTierResolver());

            var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunBuilderComparativeProofAsync(root, provider: "ollama"));

            Assert.Contains("stronger-tier", ex.Message, StringComparison.OrdinalIgnoreCase);
            var availability = BuilderExecutionService.LoadBuilderStrongerTierAvailability(run.RunFolder);
            Assert.NotNull(availability);
            Assert.Equal("unavailable", availability!.AvailabilityState);
            Assert.False(File.Exists(BuilderExecutionService.BuilderComparativeProofRunPath(run.RunFolder)));
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
    public async Task RefreshBuilderModelRoutingArtifacts_writes_matrix_policy_summary_and_stability()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await PrepareSyntheticBuilderModelRoutingProofAsync(
                root,
                service,
                "compile_fix",
                "direct_low_floor",
                "compile-fix",
                "Compile fix");

            var policy = service.RefreshBuilderModelRoutingArtifacts(root);

            Assert.True(File.Exists(BuilderExecutionService.BuilderModelCapabilityMatrixPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelRoutingPolicyPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelRoutingPolicySummaryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelRoutingPolicyHistoryPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelRoutingStabilityPathForRepo(root)));

            var matrix = BuilderExecutionService.LoadBuilderModelCapabilityMatrix(root);
            var entry = Assert.Single(matrix.Entries);
            Assert.Equal("compile_fix", entry.TaskClass);
            Assert.Equal("low_floor_direct_supported", entry.CapabilityState);
            Assert.Equal("direct_low_floor_route", entry.RouteClass);

            Assert.Single(policy.Entries);
            Assert.Equal("low_floor_model_tier", policy.Entries[0].PreferredModelTier);

            var stability = BuilderExecutionService.LoadBuilderModelRoutingStability(root);
            var stabilityEntry = Assert.Single(stability.Entries);
            Assert.Equal("provisional", stabilityEntry.StabilityState);

            var markdown = File.ReadAllText(BuilderExecutionService.BuilderModelRoutingPolicySummaryPathForRepo(root));
            Assert.Contains("Low-floor direct", markdown, StringComparison.OrdinalIgnoreCase);
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
    public async Task RefreshBuilderModelRoutingArtifacts_records_policy_change_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            var run = await PrepareSyntheticBuilderModelRoutingProofAsync(
                root,
                service,
                "bounded_refactor",
                "direct_low_floor",
                "bounded-refactor",
                "Bounded refactor");

            service.RefreshBuilderModelRoutingArtifacts(root);
            WriteSyntheticBuilderDefaultPolicy(
                root,
                run,
                "bounded_refactor",
                "stronger_tier_recommended",
                "bounded-refactor",
                "Bounded refactor");

            var refreshed = service.RefreshBuilderModelRoutingArtifacts(root);

            var history = BuilderExecutionService.LoadBuilderModelRoutingPolicyHistory(root);
            Assert.NotNull(history);
            Assert.Contains(
                history!.Entries,
                entry => string.Equals(entry.TaskClass, "bounded_refactor", StringComparison.Ordinal) &&
                         string.Equals(entry.PriorPolicyState, "direct_low_floor", StringComparison.Ordinal) &&
                         string.Equals(entry.NewPolicyState, "stronger_tier_recommended", StringComparison.Ordinal));
            Assert.Single(refreshed.Entries);
            Assert.Equal("stronger_builder_tier", refreshed.Entries[0].PreferredModelTier);
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
    public async Task PreviewBuilderConversationIntake_records_low_floor_direct_model_decision()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await PrepareSyntheticBuilderModelRoutingProofAsync(
                root,
                service,
                "compile_fix",
                "direct_low_floor",
                "compile-fix",
                "Compile fix");

            var intake = service.PreviewBuilderConversationIntake(
                root,
                "Fix compile errors in ValidationRunnerService.");

            var modelDecision = BuilderExecutionService.LoadBuilderModelDecision(root);
            var escalation = BuilderExecutionService.LoadBuilderModelEscalationPolicyDecision(root);

            Assert.Equal("compile_fix", intake.NormalizedTaskClass);
            Assert.Equal("direct_low_floor_route", intake.SelectedRoute);
            Assert.Equal("ready_for_operator_approval", intake.LaunchReadinessState);
            Assert.Equal("low_floor_model_tier", modelDecision.SelectedModelTier);
            Assert.Equal("low_floor_direct_supported", modelDecision.CapabilityState);
            Assert.Equal("low_floor_direct", escalation.FinalDecisionState);
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
    public async Task PreviewBuilderConversationIntake_records_split_first_low_floor_model_decision()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await PrepareSyntheticBuilderModelRoutingProofAsync(
                root,
                service,
                "bounded_refactor",
                "split_first_low_floor",
                "bounded-refactor",
                "Bounded refactor");

            var intake = service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");

            var modelDecision = BuilderExecutionService.LoadBuilderModelDecision(root);
            var escalation = BuilderExecutionService.LoadBuilderModelEscalationPolicyDecision(root);

            Assert.Equal("bounded_refactor", intake.NormalizedTaskClass);
            Assert.Equal("split_first_low_floor_route", intake.SelectedRoute);
            Assert.True(intake.SplitFirstRequired);
            Assert.Equal("low_floor_model_tier", modelDecision.SelectedModelTier);
            Assert.Equal("low_floor_split_first_supported", modelDecision.CapabilityState);
            Assert.Equal("low_floor_via_split_first", escalation.FinalDecisionState);
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
    public async Task PreviewBuilderConversationIntake_blocks_when_required_stronger_tier_is_unavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await PrepareSyntheticBuilderModelRoutingProofAsync(
                root,
                service,
                "bounded_refactor",
                "stronger_tier_required",
                "bounded-refactor",
                "Bounded refactor",
                strongerTierAvailabilityState: "unavailable");

            var intake = service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");

            var modelDecision = BuilderExecutionService.LoadBuilderModelDecision(root);
            var escalation = BuilderExecutionService.LoadBuilderModelEscalationPolicyDecision(root);

            Assert.Equal("task_out_of_scope_route", intake.SelectedRoute);
            Assert.Equal("launch_blocked_model_policy", intake.LaunchReadinessState);
            Assert.Contains("stronger", intake.BlockReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("stronger_builder_tier", modelDecision.SelectedModelTier);
            Assert.Equal("blocked_required_stronger_tier_unavailable", escalation.FinalDecisionState);
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
    public async Task PreviewBuilderConversationIntake_writes_route_and_model_decision_explanations()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateBuilderExecutionService(new SuccessfulBuilderProofCommandRunner());
            await PrepareSyntheticBuilderModelRoutingProofAsync(
                root,
                service,
                "bounded_refactor",
                "split_first_low_floor",
                "bounded-refactor",
                "Bounded refactor");

            service.PreviewBuilderConversationIntake(
                root,
                "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.");

            Assert.True(File.Exists(BuilderExecutionService.BuilderRouteExplanationPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderModelDecisionExplanationPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderOperatorDiagnosticSummaryPathForRepo(root)));

            var routeExplanation = BuilderExecutionService.LoadBuilderRouteExplanation(root);
            Assert.Equal("bounded_refactor", routeExplanation.TaskClass);
            Assert.Equal("split_first_low_floor_route", routeExplanation.SelectedRoute);
            Assert.Contains(
                routeExplanation.LinkedCapabilityMatrixEntries,
                entry => entry.Contains("low_floor_split_first_supported", StringComparison.Ordinal));

            var modelExplanation = BuilderExecutionService.LoadBuilderModelDecisionExplanation(root);
            Assert.Equal("low_floor_model_tier", modelExplanation.ModelTierSelected);
            Assert.Equal("low_floor_via_split_first", modelExplanation.EscalationState);
            Assert.Contains("Split-first keeps the low-floor route viable", modelExplanation.SplitFirstReasoning, StringComparison.OrdinalIgnoreCase);

            var diagnosticSummary = File.ReadAllText(BuilderExecutionService.BuilderOperatorDiagnosticSummaryPathForRepo(root));
            Assert.Contains("- Route: split_first_low_floor_route", diagnosticSummary, StringComparison.Ordinal);
            Assert.Contains("- Model tier: low_floor_model_tier", diagnosticSummary, StringComparison.Ordinal);
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
    public void RecordBuilderPatchReviewOutcome_writes_failure_analysis_for_revision_request()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            SeedSyntheticBuilderPatchDiffReviewArtifacts(root);
            var service = CreateBuilderExecutionService();

            service.RecordBuilderPatchReviewOutcome(
                root,
                "revise_requested",
                "Need a smaller bounded patch.");

            Assert.True(File.Exists(BuilderExecutionService.BuilderFailureAnalysisPathForRepo(root)));
            Assert.True(File.Exists(BuilderExecutionService.BuilderOperatorDiagnosticSummaryPathForRepo(root)));

            var failureAnalysis = BuilderExecutionService.LoadBuilderFailureAnalysis(root);
            Assert.Equal("revision_requested", failureAnalysis.FailureClassification);
            Assert.Equal("awaiting_operator_review", failureAnalysis.FailureStageId);
            Assert.Contains("Revise the candidate changes", failureAnalysis.PossibleRemediationPath, StringComparison.OrdinalIgnoreCase);

            var diagnosticSummary = File.ReadAllText(BuilderExecutionService.BuilderOperatorDiagnosticSummaryPathForRepo(root));
            Assert.Contains("## Failure Analysis", diagnosticSummary, StringComparison.Ordinal);
            Assert.Contains("revision_requested", diagnosticSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<BuilderProofRun> PrepareSyntheticBuilderModelRoutingProofAsync(
        string root,
        BuilderExecutionService service,
        string taskClass,
        string policyState,
        string targetId,
        string targetLabel,
        string strongerTierAvailabilityState = "available")
    {
        SeedBuilderRepoKnowledgeFiles(root);
        var run = await service.RunBuilderProofMatrixAsync(root, BuilderExecutionService.BuilderProofFloorModelId, "ollama");
        WriteSyntheticBuilderDefaultPolicy(root, run, taskClass, policyState, targetId, targetLabel);
        WriteSyntheticBuilderStrongerTierAvailability(run, strongerTierAvailabilityState);
        return run;
    }

    private static void WriteSyntheticBuilderDefaultPolicy(
        string root,
        BuilderProofRun run,
        string taskClass,
        string policyState,
        string targetId,
        string targetLabel)
    {
        var observedUtc = DateTimeOffset.Parse("2026-03-14T18:30:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        var complexity = new BuilderProofComplexityDimensions(
            FileCountTouched: 2,
            ProjectCountTouched: 1,
            DependencyReferenceChangeCount: 0,
            TestChangesRequired: string.Equals(taskClass, "test_extension", StringComparison.Ordinal),
            NewFileCreationCount: 0,
            PromptAmbiguity: "low");
        var evidencePaths = new[] { BuilderExecutionService.BuilderProofRunArtifactPath(run.RunFolder) };
        var entry = new BuilderDefaultPolicyTaskClassEntry(
            "wpf_app",
            targetId,
            targetLabel,
            taskClass,
            complexity,
            policyState,
            string.Equals(policyState, "stronger_tier_required", StringComparison.Ordinal) ? "partial_implementation_gap" : string.Empty,
            $"Synthetic default policy for {taskClass} set to {policyState}.",
            new[] { $"Synthetic model routing evidence for {taskClass}={policyState}." },
            evidencePaths);
        var policy = new BuilderDefaultPolicy(
            run.ProofRunId,
            BuilderExecutionService.BuilderProofFloorModelId,
            string.Equals(policyState, "direct_low_floor", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "split_first_low_floor", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "low_floor_with_repair_loop_expected", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "stronger_tier_optional", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "stronger_tier_recommended", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            string.Equals(policyState, "stronger_tier_required", StringComparison.Ordinal) ? new[] { taskClass } : Array.Empty<string>(),
            new[] { entry },
            evidencePaths,
            $"Synthetic default policy keeps {taskClass} at {policyState}.",
            BuilderExecutionService.BuilderDefaultPolicyPath(run.RunFolder),
            observedUtc);
        File.WriteAllText(
            BuilderExecutionService.BuilderDefaultPolicyPath(run.RunFolder),
            JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));

        var history = new BuilderDefaultPolicyHistory(
            20,
            new[]
            {
                new BuilderDefaultPolicyHistoryEntry(
                    policy.SourceProofRunId,
                    policy.CurrentModelId,
                    policy.Summary,
                    policy.ArtifactPath,
                    policy.ObservedUtc,
                    policy.TaskClassEntries)
            });
        File.WriteAllText(
            BuilderExecutionService.BuilderDefaultPolicyHistoryPathForRepo(root),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSyntheticBuilderStrongerTierAvailability(BuilderProofRun run, string availabilityState)
    {
        var available = string.Equals(availabilityState, "available", StringComparison.Ordinal);
        var availability = new BuilderStrongerTierAvailability(
            BuilderExecutionService.BuilderProofFloorModelId,
            "stronger_builder_tier",
            "qwen2.5:7b-instruct",
            available ? "qwen2.5:7b-instruct" : string.Empty,
            availabilityState,
            available
                ? "qwen2.5:7b-instruct is available for bounded comparative proof."
                : "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            "ollama",
            "http://localhost:11434",
            string.Empty,
            available
                ? new[] { BuilderExecutionService.BuilderProofFloorModelId, "qwen2.5:7b-instruct" }
                : new[] { BuilderExecutionService.BuilderProofFloorModelId },
            available
                ? "Resolved qwen2.5:7b-instruct from the bounded stronger-tier candidate set."
                : "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            Array.Empty<string>(),
            available
                ? "qwen2.5:7b-instruct is available for bounded comparative proof."
                : "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            BuilderExecutionService.BuilderStrongerTierAvailabilityPath(run.RunFolder),
            DateTimeOffset.Parse("2026-03-14T18:31:00+00:00", System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(
            BuilderExecutionService.BuilderStrongerTierAvailabilityPath(run.RunFolder),
            JsonSerializer.Serialize(availability, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static BuilderExecutionService CreateBuilderExecutionService(
        IBuilderProofCommandRunner? runner = null,
        IBuilderStrongerTierResolver? resolver = null,
        IBuilderToolchainCapabilityScanner? capabilityScanner = null,
        IBuilderGitReadinessProbe? gitReadinessProbe = null)
    {
        var registry = new ToolRegistry("etc/ui.tools.catalog.json");
        var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
        return new BuilderExecutionService(
            runtimeBridge,
            new ArtifactManager(),
            registry,
            runner,
            resolver ?? new AvailableBuilderStrongerTierResolver(),
            capabilityScanner ?? CreateDefaultBuilderToolchainCapabilityScanner(),
            gitReadinessProbe ?? new ScriptedBuilderGitReadinessProbe(
                new BuilderGitReadinessObservation(
                    false,
                    string.Empty,
                    false,
                    false,
                    "unknown",
                    "blocked_git_missing_repo",
                    new[] { "No Git repository was detected for the approved patch handoff." },
                    DateTimeOffset.Parse("2026-03-14T08:30:00+00:00", System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static IBuilderToolchainCapabilityScanner CreateDefaultBuilderToolchainCapabilityScanner()
    {
        var observedUtc = DateTimeOffset.Parse("2026-03-13T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        return new ScriptedBuilderToolchainCapabilityScanner(
            new BuilderToolchainCapabilityObservation(
                "dotnet",
                "sdk",
                @"C:\tools\dotnet\dotnet.exe",
                "8.0.204",
                true,
                true,
                "probe_succeeded",
                string.Empty,
                observedUtc),
            new BuilderToolchainCapabilityObservation(
                "msbuild",
                "build_tool",
                @"C:\tools\msbuild\MSBuild.exe",
                "17.10.1",
                true,
                true,
                "probe_succeeded",
                string.Empty,
                observedUtc));
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(BuilderExecutionService).GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return Assert.IsType<T>(result);
    }

    private static void InvokePrivateInstance(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private sealed class FakeBuilderProofCommandRunner : IBuilderProofCommandRunner
    {
        public Task<BuilderProofCommandExecutionResult> ExecuteAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            string logPath,
            CancellationToken ct)
        {
            var isProofCalc = arguments.Any(argument => argument.Contains("ProofCalc.Tests.csproj", StringComparison.OrdinalIgnoreCase));
            var isTestExtension = arguments.Any(argument => argument.Contains("ExtensionCalc.Tests.csproj", StringComparison.OrdinalIgnoreCase));
            var isSplitRefactorProbe = workingDirectory.Contains($"comparative-proof{Path.DirectorySeparatorChar}split-floor", StringComparison.OrdinalIgnoreCase) ||
                                       workingDirectory.Contains("bounded-refactor-split", StringComparison.OrdinalIgnoreCase);
            var isComparativeRefactorProbe = workingDirectory.Contains($"comparative-proof{Path.DirectorySeparatorChar}stronger-tier", StringComparison.OrdinalIgnoreCase);
            var isRefactorProbe = arguments.Any(argument => argument.Contains("RefactorProof.csproj", StringComparison.OrdinalIgnoreCase)) &&
                                  !isSplitRefactorProbe &&
                                  !isComparativeRefactorProbe;
            var isRecovery = logPath.Contains($"{Path.DirectorySeparatorChar}recovery{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            var isTestCommand = arguments.Count > 0 && string.Equals(arguments[0], "test", StringComparison.Ordinal);

            var lines = isRefactorProbe
                ? new[]
                {
                    "ProfileSummary.cs(7,20): error CS0103: The name 'NameFormatter' does not exist in the current context",
                    "Build FAILED."
                }
                : isTestExtension && !isRecovery
                    ? new[]
                    {
                        "CalculatorExtensionTests.cs(10,35): error CS0103: The name 'Calculator' does not exist in the current context",
                        "Build FAILED."
                    }
                : isProofCalc && !isRecovery
                ? new[]
                {
                    "ProofCalc/Calculator.cs(7,36): error CS1002: ; expected",
                    "Build FAILED."
                }
                : isTestCommand
                    ? new[]
                    {
                        "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1"
                    }
                    : new[]
                    {
                        "Build succeeded."
                    };

            var exitCode = (isProofCalc && !isRecovery) || (isTestExtension && !isRecovery) || isRefactorProbe ? 1 : 0;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, string.Join(System.Environment.NewLine, lines));
            return Task.FromResult(new BuilderProofCommandExecutionResult(exitCode, lines));
        }
    }

    private sealed class SuccessfulBuilderProofCommandRunner : IBuilderProofCommandRunner
    {
        public Task<BuilderProofCommandExecutionResult> ExecuteAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            string logPath,
            CancellationToken ct)
        {
            var isTestCommand = arguments.Count > 0 && string.Equals(arguments[0], "test", StringComparison.Ordinal);
            var lines = isTestCommand
                ? new[]
                {
                    "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1"
                }
                : new[]
                {
                    "Build succeeded."
                };

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, string.Join(System.Environment.NewLine, lines));
            return Task.FromResult(new BuilderProofCommandExecutionResult(0, lines));
        }
    }

    private sealed class AlwaysFailingProofCommandRunner : IBuilderProofCommandRunner
    {
        public Task<BuilderProofCommandExecutionResult> ExecuteAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            string logPath,
            CancellationToken ct)
        {
            var isProofCalc = arguments.Any(argument => argument.Contains("ProofCalc.Tests.csproj", StringComparison.OrdinalIgnoreCase));
            var isTestCommand = arguments.Count > 0 && string.Equals(arguments[0], "test", StringComparison.Ordinal);

            var lines = isProofCalc && !isTestCommand
                ? new[]
                {
                    "ProofCalc/Calculator.cs(7,36): error CS1002: ; expected",
                    "Build FAILED."
                }
                : isTestCommand
                    ? new[]
                    {
                        "Test Run Failed."
                    }
                    : new[]
                    {
                        "Build succeeded."
                    };

            var exitCode = isProofCalc ? 1 : 0;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, string.Join(System.Environment.NewLine, lines));
            return Task.FromResult(new BuilderProofCommandExecutionResult(exitCode, lines));
        }
    }

    private sealed class AvailableBuilderStrongerTierResolver : IBuilderStrongerTierResolver
    {
        public Task<BuilderStrongerTierAvailability> ResolveAsync(
            string currentModelId,
            string recommendedModelClass,
            string? preferredStrongerModelId,
            string provider,
            CancellationToken ct)
            => Task.FromResult(new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                "qwen2.5:7b-instruct",
                "available",
                "qwen2.5:7b-instruct is available for bounded comparative proof.",
                provider,
                "http://localhost:11434",
                string.Empty,
                new[] { BuilderExecutionService.BuilderProofFloorModelId, "qwen2.5:7b-instruct" },
                "Resolved qwen2.5:7b-instruct from the bounded stronger-tier candidate set.",
                Array.Empty<string>(),
                "qwen2.5:7b-instruct is available for bounded comparative proof.",
                string.Empty,
                System.DateTimeOffset.UtcNow));
    }

    private sealed class UnavailableBuilderStrongerTierResolver : IBuilderStrongerTierResolver
    {
        public Task<BuilderStrongerTierAvailability> ResolveAsync(
            string currentModelId,
            string recommendedModelClass,
            string? preferredStrongerModelId,
            string provider,
            CancellationToken ct)
            => Task.FromResult(new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                string.Empty,
                "unavailable",
                "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
                provider,
                "http://localhost:11434",
                string.Empty,
                new[] { BuilderExecutionService.BuilderProofFloorModelId },
                "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
                Array.Empty<string>(),
                "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
                string.Empty,
                System.DateTimeOffset.UtcNow));
    }

    private static void SeedBuilderRepoLanguagePolicyFiles(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui"));
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui.Tests"));
        File.WriteAllText(Path.Combine(root, "Shoots.sln"), "Microsoft Visual Studio Solution File");
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "Shoots.Ui.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
    }

    private static void SeedBuilderRepoKnowledgeFiles(string root)
    {
        SeedBuilderRepoLanguagePolicyFiles(root);

        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows</TargetFramework>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Shoots.Ui\Shoots.Ui.csproj" />
              </ItemGroup>
            </Project>
            """);

        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui", "Builder"));
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui", "ViewModels"));
        Directory.CreateDirectory(Path.Combine(root, "ui", "Shoots.Ui", "Services"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Core"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Tests"));

        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "MainWindow.xaml"),
            """
            <Window x:Class="Shoots.UI.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
            """);
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"),
            "namespace Shoots.UI.ViewModels; public sealed class MainWindowViewModel { }");
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "Builder", "BuilderExecutionService.cs"),
            "namespace Shoots.UI.Builder; public sealed class BuilderExecutionService { }");
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui", "Services", "ValidationRunnerService.cs"),
            "namespace Shoots.UI.Services; public sealed class ValidationRunnerService { }");
        File.WriteAllText(
            Path.Combine(root, "ui", "Shoots.Ui.Tests", "MainWindowViewModelBackendStatusTests.cs"),
            "namespace Shoots.UI.Tests; public sealed class MainWindowViewModelBackendStatusTests { }");

        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Core", "Shoots.Runtime.Core.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Core", "RuntimeLoop.cs"),
            "namespace Shoots.Runtime.Core; public sealed class RuntimeLoop { }");
        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Tests", "Shoots.Runtime.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Shoots.Runtime.Core\Shoots.Runtime.Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "src", "Runtime", "Shoots.Runtime.Tests", "RuntimeLoopTests.cs"),
            "namespace Shoots.Runtime.Tests; public sealed class RuntimeLoopTests { }");
    }

    private static void SeedSyntheticBuilderPatchDiffReviewArtifacts(string root)
    {
        SeedBuilderRepoKnowledgeFiles(root);
        Directory.CreateDirectory(BuilderExecutionService.BuilderProofRootForRepo(root));

        var now = DateTimeOffset.Parse("2026-03-14T03:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        var intake = new BuilderConversationIntake(
            "Update MainWindow.xaml and MainWindowViewModel for the builder conversation preview in the WPF UI.",
            "bounded_refactor",
            "wpf_desktop_dotnet",
            "WPF/Desktop .NET",
            "strong_match",
            "Repo retrieval matched the WPF UI surfaces strongly.",
            "route_allowed",
            "Capability review allows the preferred WPF/Desktop .NET stack.",
            "split_first_low_floor_route",
            "default_route_policy",
            true,
            "optional",
            "accept_suggested_route",
            "ready_for_launch",
            string.Empty,
            Array.Empty<string>(),
            "Conversation intake is ready for launch.",
            BuilderExecutionService.BuilderConversationIntakePathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderConversationIntakePathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(intake, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var handoff = new BuilderConversationHandoff(
            intake.RawRequestText,
            intake.NormalizedTaskClass,
            intake.RetrievalConfidenceState,
            intake.CapabilityRoutingState,
            intake.SelectedRoute,
            intake.RouteSourceState,
            intake.OperatorDecisionState,
            intake.LaunchReadinessState,
            intake.BlockReason,
            new[] { intake.ArtifactPath },
            "Conversation handoff is ready for execution.",
            BuilderExecutionService.BuilderConversationHandoffPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderConversationHandoffPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(handoff, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var changedFiles = new[]
        {
            new BuilderPatchReviewChangedFile(
                Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"),
                "ui_markup",
                "modified",
                "MainWindow.xaml was modified to satisfy the bounded ui markup route.",
                true),
            new BuilderPatchReviewChangedFile(
                Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"),
                "view_model",
                "modified",
                "MainWindowViewModel.cs was modified to satisfy the bounded view model route.",
                true)
        };

        var session = new BuilderConversationExecutionSession(
            "session-1",
            "intake-1",
            "handoff-1",
            intake.RawRequestText,
            intake.NormalizedTaskClass,
            intake.SelectedRoute,
            intake.ImpliedStackId,
            intake.ImpliedStackLabel,
            intake.CapabilitySummary,
            "awaiting_patch_review",
            "awaiting_operator_review",
            "Awaiting operator review",
            "pending_operator_review",
            "Build=passed. Test=passed. Outcome=launched_and_passed.",
            string.Empty,
            string.Empty,
            string.Empty,
            BuilderExecutionService.BuilderPatchReviewPathForRepo(root),
            string.Empty,
            changedFiles,
            new[]
            {
                new BuilderConversationExecutionStage(
                    "awaiting_operator_review",
                    "Awaiting operator review",
                    "active",
                    "Candidate changes are ready for operator review.",
                    Array.Empty<string>())
            },
            new[] { BuilderExecutionService.BuilderConversationHandoffPathForRepo(root) },
            "Execution session is awaiting patch review.",
            BuilderExecutionService.BuilderConversationExecutionSessionPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderConversationExecutionSessionPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(session, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var patchReview = new BuilderPatchReview(
            session.SessionId,
            intake.ArtifactPath,
            handoff.ArtifactPath,
            intake.SelectedRoute,
            intake.ImpliedStackId,
            intake.ImpliedStackLabel,
            session.ValidationSummary,
            "ready_for_operator_review",
            changedFiles,
            new[] { session.ArtifactPath, handoff.ArtifactPath },
            "Patch review found 2 changed file candidate(s) on route split_first_low_floor_route.",
            BuilderExecutionService.BuilderPatchReviewPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderPatchReviewPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(patchReview, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var patchDiffReview = new BuilderPatchDiffReview(
            session.SessionId,
            patchReview.SessionId,
            patchReview.ArtifactPath,
            "all_files_pending",
            "ready_for_operator_review",
            new[]
            {
                new BuilderPatchDiffReviewFileEntry(
                    Path.Combine("ui", "Shoots.Ui", "MainWindow.xaml"),
                    "ui_markup",
                    "modified",
                    "Diff preview shows UI copy and layout changes.",
                    "@@ MainWindow.xaml\n-<TextBlock Text=\"Old\" />\n+<TextBlock Text=\"New\" />",
                    "pending_review",
                    string.Empty,
                    now),
                new BuilderPatchDiffReviewFileEntry(
                    Path.Combine("ui", "Shoots.Ui", "ViewModels", "MainWindowViewModel.cs"),
                    "view_model",
                    "modified",
                    "Diff preview shows view-model state changes.",
                    "@@ MainWindowViewModel.cs\n-private string _status = \"old\";\n+private string _status = \"new\";",
                    "pending_review",
                    string.Empty,
                    now)
            },
            new[] { session.ArtifactPath, patchReview.ArtifactPath },
            "Patch diff review is waiting on file-level approval.",
            BuilderExecutionService.BuilderPatchDiffReviewPathForRepo(root),
            now);
        File.WriteAllText(
            BuilderExecutionService.BuilderPatchDiffReviewPathForRepo(root),
            System.Text.Json.JsonSerializer.Serialize(patchDiffReview, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ScriptedBuilderToolchainCapabilityScanner : IBuilderToolchainCapabilityScanner
    {
        private readonly BuilderToolchainCapabilityObservation[] _observations;

        public ScriptedBuilderToolchainCapabilityScanner(params BuilderToolchainCapabilityObservation[] observations)
        {
            _observations = observations;
        }

        public IReadOnlyList<BuilderToolchainCapabilityObservation> Scan(string repoRoot) => _observations;
    }

    private sealed class SequencedBuilderToolchainCapabilityScanner : IBuilderToolchainCapabilityScanner
    {
        private readonly Queue<IReadOnlyList<BuilderToolchainCapabilityObservation>> _observations;

        public SequencedBuilderToolchainCapabilityScanner(params IReadOnlyList<BuilderToolchainCapabilityObservation>[] observations)
        {
            _observations = new Queue<IReadOnlyList<BuilderToolchainCapabilityObservation>>(observations);
        }

        public IReadOnlyList<BuilderToolchainCapabilityObservation> Scan(string repoRoot)
        {
            Assert.NotEmpty(_observations);
            return _observations.Dequeue();
        }
    }

    private sealed class ScriptedBuilderGitReadinessProbe : IBuilderGitReadinessProbe
    {
        private readonly BuilderGitReadinessObservation _observation;

        public ScriptedBuilderGitReadinessProbe(BuilderGitReadinessObservation observation)
        {
            _observation = observation;
        }

        public BuilderGitReadinessObservation Probe(string repoRoot) => _observation;
    }
}
