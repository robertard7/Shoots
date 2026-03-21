using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderSignalProfileServiceTests
{
    [Fact]
    public void Refresh_signal_profiles_generates_deterministic_built_in_profiles()
    {
        var repo = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var first = BuilderSignalProfileService.RefreshSignalProfiles(
                repo,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(70));
            var firstJson = File.ReadAllText(BuilderSignalProfileService.SignalProfilesPathForRepo(repo));
            var second = BuilderSignalProfileService.RefreshSignalProfiles(
                repo,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(70));
            var secondJson = File.ReadAllText(BuilderSignalProfileService.SignalProfilesPathForRepo(repo));

            Assert.Equal(firstJson, secondJson);
            Assert.Equal(5, first.Profiles.Count);
            Assert.Equal(BuilderSignalProfileService.BalancedDefaultProfileId, first.ActiveProfileId);
            Assert.True(first.OverridePolicy.OverrideEnabled);
            Assert.All(first.Profiles, profile => Assert.Equal(6, profile.BaseWeights.Count));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Signal_profiles_enforce_bounded_overrides_and_calibration_normalizes_to_one()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);
            var profiles = BuilderSignalProfileService.RefreshSignalProfiles(
                repoB,
                BuilderSignalProfileService.RiskAverseProfileId,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    [BuilderSignalCalibrationService.GuardrailSignalId] = 0.5d,
                    [BuilderSignalCalibrationService.RankingSignalId] = -0.5d
                },
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(71));
            var calibration = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repoB,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Audit,
                seeded.Guardrails,
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(72),
                profiles);

            Assert.Equal(BuilderSignalProfileService.RiskAverseProfileId, calibration.ActiveProfileId);
            Assert.NotEmpty(calibration.ProfileOverrideHash);
            Assert.InRange(calibration.Weights.Sum(entry => entry.AdjustedWeight), 0.999d, 1.001d);
            Assert.Contains(profiles.ActiveOverrides, entry =>
                string.Equals(entry.SignalId, BuilderSignalCalibrationService.GuardrailSignalId, StringComparison.OrdinalIgnoreCase) &&
                entry.AppliedDelta <= 0.06d);
            Assert.Contains(profiles.ActiveOverrides, entry =>
                string.Equals(entry.SignalId, BuilderSignalCalibrationService.RankingSignalId, StringComparison.OrdinalIgnoreCase) &&
                entry.AppliedDelta >= -0.06d);
            Assert.Contains(calibration.Weights, entry =>
                string.Equals(entry.SignalId, BuilderSignalCalibrationService.GuardrailSignalId, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(entry.OverrideDelta) > 0d);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Signal_profile_context_flows_into_suggestions_comparisons_and_readiness()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var seeded = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);
            var profiles = BuilderSignalProfileService.RefreshSignalProfiles(
                repoB,
                BuilderSignalProfileService.ConstraintFirstProfileId,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    [BuilderSignalCalibrationService.ConstraintSignalId] = 0.06d,
                    [BuilderSignalCalibrationService.RankingSignalId] = -0.06d
                },
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(73));
            var calibration = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repoB,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorConstraintService.LoadOperatorConstraints(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                seeded.Audit,
                seeded.Guardrails,
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(74),
                profiles);
            var suggestions = BuilderAutoSuggestionService.RefreshAutoSuggestions(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                seeded.Comparisons,
                seeded.Accuracy,
                seeded.Readiness,
                seeded.Guardrails,
                seeded.Decisions,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(75),
                seeded.Trust,
                BuilderPredictiveDriftService.LoadPredictiveDrift(repoB),
                calibration);
            var comparisons = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
                repoB,
                seeded.Playbooks,
                seeded.Simulations,
                BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                seeded.Accuracy,
                seeded.Decisions,
                BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                BuilderOperatorIntentService.LoadOperatorIntent(repoB),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(76),
                3,
                seeded.Trust,
                BuilderPredictiveDriftService.LoadPredictiveDrift(repoB),
                seeded.Guardrails,
                calibration);
            var readiness = BuilderExecutionReadinessService.RefreshExecutionReadiness(
                repoB,
                selectedPlaybookId: seeded.RetrySameRoute.PlaybookId,
                selectedSimulationId: seeded.RetrySameRoute.SimulationId,
                selectedComparisonId: seeded.Comparisons.ComparisonSets.First().ComparisonId,
                playbooks: seeded.Playbooks,
                simulations: seeded.Simulations,
                rankings: BuilderPlaybookRankingService.LoadPlaybookRankings(repoB),
                contextFilters: BuilderPlaybookContextFilterService.LoadContextFilters(repoB),
                comparisons: comparisons,
                accuracy: seeded.Accuracy,
                decisions: seeded.Decisions,
                routeWarnings: BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoB),
                justifications: BuilderDecisionJustificationService.LoadDecisionJustifications(repoB),
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(77),
                guardrails: seeded.Guardrails,
                trust: seeded.Trust,
                predictiveDrift: BuilderPredictiveDriftService.LoadPredictiveDrift(repoB),
                calibration: calibration);

            Assert.NotNull(suggestions);
            Assert.NotNull(comparisons);
            Assert.NotNull(readiness);

            var primarySuggestion = suggestions!.Suggestions.First(entry =>
                string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase));
            var topMetric = comparisons!.ComparisonSets.SelectMany(set => set.ComparisonMetrics).First();
            Assert.Contains("constraint_first", primarySuggestion.CalibrationProfile, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Constraint First", primarySuggestion.SignalBalanceSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(primarySuggestion.SignalContributions, entry =>
                string.Equals(entry.SignalId, BuilderSignalCalibrationService.ConstraintSignalId, StringComparison.OrdinalIgnoreCase) &&
                entry.Weight > 0.16d);
            Assert.Contains("constraint_first", topMetric.CalibrationProfile, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(topMetric.SignalContributions, entry =>
                string.Equals(entry.SignalId, BuilderSignalCalibrationService.ConstraintSignalId, StringComparison.OrdinalIgnoreCase) &&
                entry.Weight > 0.16d);
            Assert.Contains("constraint_first", readiness!.CalibrationProfile, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Constraint First", readiness.SignalBalanceSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Operator_decisions_can_record_signal_profile_snapshot_context()
    {
        var repo = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            var profiles = BuilderSignalProfileService.RefreshSignalProfiles(
                repo,
                BuilderSignalProfileService.TrustWeightedProfileId,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    [BuilderSignalCalibrationService.TrustSignalId] = 0.03d
                },
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(78));
            var calibration = BuilderSignalCalibrationService.RefreshSignalCalibration(
                repo,
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(79),
                profiles: profiles);
            var decisions = BuilderOperatorDecisionService.RecordDecision(
                repo,
                new BuilderOperatorDecisionRequest(
                    "playbook-1",
                    "simulation-1",
                    "manual_retry",
                    BuilderWorkspaceService.ResolveWorkspaceId(repo),
                    "comparative_route",
                    new[] { calibration.ArtifactPath, profiles.ArtifactPath },
                    "run-001",
                    "resolved_block",
                    true,
                    string.Empty,
                    new[] { calibration.ArtifactPath },
                    PredictedOutcomeClass: "success",
                    PredictedConfidenceLevel: "high",
                    PredictedConfidenceScore: 0.82d,
                    ActiveSignalProfileId: calibration.ActiveProfileId,
                    ProfileOverrideHash: calibration.ProfileOverrideHash,
                    CalibrationSnapshotLink: calibration.ArtifactPath),
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(80));

            var decision = Assert.Single(decisions.Decisions);
            Assert.Equal(calibration.ActiveProfileId, decision.ActiveSignalProfileId);
            Assert.Equal(calibration.ProfileOverrideHash, decision.ProfileOverrideHash);
            Assert.Equal(calibration.ArtifactPath, decision.CalibrationSnapshotLink);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderSignalProfileTests
{
    [Fact]
    public async Task Builder_workspace_supports_profile_selection_bounded_override_and_reset()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("host-b");
        try
        {
            _ = BuilderPredictiveDriftServiceTests.SeedPredictiveDriftState(repoA, repoB);
            var workspaceProvider = new MultiWorkspaceProvider(
                new ProjectWorkspace("runtime-a", repoA, BuilderWorkspaceTestData.ObservedUtc, ProjectId: "runtime-a"),
                new ProjectWorkspace("host-b", repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1), ProjectId: "host-b"));
            var scanner = BuilderWorkspaceTestData.CreateScanner(
                repoA,
                new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));
            scanner.AddObservations(
                repoB,
                new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));

            var viewModel = BuilderWorkspaceTestData.CreateViewModel(repoA, workspaceProvider, scanner);
            viewModel.SelectedBuilderWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoB);

            Assert.True(viewModel.HasBuilderSignalProfileSummary);
            Assert.True(viewModel.HasBuilderSignalProfileArtifactPath);

            viewModel.SelectedBuilderSignalProfileId = BuilderSignalProfileService.RiskAverseProfileId;
            viewModel.SelectedBuilderSignalGuardrailOverride = "0.06";
            viewModel.SelectedBuilderSignalRankingOverride = "-0.06";

            Assert.Contains("Risk Averse", viewModel.BuilderSignalProfileSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("0.06", viewModel.SelectedBuilderSignalGuardrailOverride);
            Assert.Equal("-0.06", viewModel.SelectedBuilderSignalRankingOverride);
            Assert.Contains("Risk Averse", viewModel.BuilderSignalCalibrationProfileSummary, StringComparison.OrdinalIgnoreCase);

            await viewModel.ResetBuilderSignalOverridesCommand.ExecuteAsync();

            Assert.Equal("0", viewModel.SelectedBuilderSignalGuardrailOverride);
            Assert.Equal("0", viewModel.SelectedBuilderSignalRankingOverride);
            await viewModel.OpenBuilderSignalProfileArtifactCommand.ExecuteAsync();
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}
