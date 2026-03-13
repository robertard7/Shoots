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
            File.WriteAllText(decisionPath, "{}");
            File.WriteAllText(stabilityPath, "{}");
            File.WriteAllText(comparativePath, "{}");

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

            var intake = InvokePrivateStatic<BuilderRequestIntake>(
                "BuildBuilderRequestIntake",
                runFolder,
                requestDecision,
                stability,
                routingPlan,
                null,
                null,
                null,
                defaultRouteDecision);
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
                defaultRouteDecision);

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

    private static BuilderExecutionService CreateBuilderExecutionService(
        IBuilderProofCommandRunner? runner = null,
        IBuilderStrongerTierResolver? resolver = null)
    {
        var registry = new ToolRegistry("etc/ui.tools.catalog.json");
        var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(registry));
        return new BuilderExecutionService(runtimeBridge, new ArtifactManager(), registry, runner, resolver ?? new AvailableBuilderStrongerTierResolver());
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
}
