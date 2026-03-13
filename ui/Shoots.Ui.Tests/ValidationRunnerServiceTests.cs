using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Services;
using Shoots.UI.Settings;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ValidationRunnerServiceTests
{
    [Fact]
    public async Task Full_validation_loop_stops_on_first_failure_in_stage_order()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." }),
                ["smoke_validation"] = new(0, new[] { "Smoke passed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var events = new List<ValidationProgressEvent>();

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false),
                events.Add);

            Assert.False(result.Success);
            Assert.Equal(2, result.Stages.Count);
            Assert.Equal(new[] { "Building UI", "Running UI tests" }, result.Stages.Select(stage => stage.StageLabel).ToArray());
            Assert.Equal("Tests failed.", result.FirstFailureText);
            Assert.True(File.Exists(Path.Combine(result.OutputFolder, "validation_result.json")));
            Assert.True(File.Exists(Path.Combine(result.OutputFolder, "validation_stability.json")));
            Assert.Equal("failed", result.StabilityClassification);
            Assert.Equal(
                new[] { "Building UI", "Running UI tests" },
                events.Where(evt => evt.EventType == "stage_started").Select(evt => evt.StageLabel).ToArray());
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Full_validation_loop_can_continue_collecting_results_after_failure()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." }),
                ["smoke_validation"] = new(0, new[] { "Smoke passed." }),
                ["integrity_validation"] = new(0, new[] { "Integrity passed." }),
                ["validate_build"] = new(0, new[] { "Repository validation passed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(true, true, 5, false, false));

            Assert.False(result.Success);
            Assert.Equal(5, result.Stages.Count);
            Assert.Equal("Tests failed.", result.FirstFailureText);
            Assert.Contains(result.Stages, stage => stage.StageId == "validate_build");
            Assert.Equal("failed", result.StabilityClassification);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Full_validation_loop_writes_orchestration_artifact_with_stage_dependencies()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(0, new[] { "Tests passed." }),
                ["smoke_validation"] = new(0, new[] { "Smoke passed." }),
                ["integrity_validation"] = new(0, new[] { "Integrity passed." }),
                ["validate_build"] = new(0, new[] { "Repository validation passed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, true, 5, false, false));

            Assert.Equal("sequential_standard_mode", result.RunMode);
            Assert.NotNull(result.OrchestrationArtifactPath);
            Assert.True(File.Exists(result.OrchestrationArtifactPath!));

            var report = JsonSerializer.Deserialize<ValidationOrchestrationReport>(File.ReadAllText(result.OrchestrationArtifactPath!));
            Assert.NotNull(report);
            Assert.Equal(new[] { "build_ui", "ui_tests", "smoke_validation", "integrity_validation", "validate_build" }, report!.Stages.Select(stage => stage.StageId).ToArray());
            Assert.Equal(new[] { "build_ui" }, report.Stages[1].DependsOnStageIds);
            Assert.Equal(new[] { "smoke_validation" }, report.Stages[3].DependsOnStageIds);
            Assert.Contains(report.Stages[3].ConcurrencyClassifications, value => string.Equals(value, "workspace_cleaning", StringComparison.Ordinal));
            Assert.Contains(report.Decisions, decision => decision.Summary.Contains("Smoke validation must finish before integrity validation can clean restore artifacts.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Manual_build_can_run_in_isolated_workspace_mode()
    {
        var repoRoot = CreateRepoRoot();
        File.WriteAllText(Path.Combine(repoRoot, "global.json"), "{ }");
        var executor = new RecordingValidationCommandExecutor();
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.BuildUiProject,
                new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, false, 5, 200, true, false, true, true, true, 2, true, 3, true));

            Assert.True(result.Success);
            Assert.Equal("isolated_workspace_mode", result.RunMode);
            Assert.NotNull(result.IsolatedWorkspacePath);
            Assert.True(Directory.Exists(result.IsolatedWorkspacePath!));
            Assert.True(File.Exists(Path.Combine(result.IsolatedWorkspacePath!, "Shoots.sln")));
            Assert.Equal(result.IsolatedWorkspacePath, Assert.Single(executor.WorkingDirectories));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Orchestration_policy_note_explains_smoke_integrity_conflicts_and_isolated_mode()
    {
        var repoRoot = CreateRepoRoot();

        try
        {
            ValidationRunnerService.RefreshOrchestrationPolicyArtifacts(
                repoRoot,
                new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, false, 5, 200, true, false, true, true, true, 2, true, 3, true));

            var path = ValidationRunnerService.OrchestrationPolicyNotePathForRepo(repoRoot);
            Assert.True(File.Exists(path));

            var contents = File.ReadAllText(path);
            Assert.Contains("serializes smoke before integrity", contents, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Integrity validation stays on the repo root because it intentionally cleans caches and restore artifacts.", contents, StringComparison.Ordinal);
            Assert.Contains("Isolated workspace mode", contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_runner_prunes_old_runs_to_keep_last_n()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 2, false, false));
            await Task.Delay(5);
            await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 2, false, false));
            await Task.Delay(5);
            await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 2, false, false));

            var runs = Directory.GetDirectories(service.ValidationRunsRoot);
            Assert.Equal(2, runs.Length);
            Assert.Equal(2, service.LoadRecentRuns(5).Count);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Fail_then_pass_retry_is_classified_as_flaky_suspected_and_preserves_first_failure()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new SequencedValidationCommandExecutor(
            new Dictionary<string, IReadOnlyList<ValidationCommandExecutionResult>>(StringComparer.Ordinal)
            {
                ["build_ui"] = new[]
                {
                    new ValidationCommandExecutionResult(0, new[] { "Build succeeded." })
                },
                ["ui_tests"] = new[]
                {
                    new ValidationCommandExecutionResult(1, new[]
                    {
                        "Test run for C:\\dev\\Shoots\\src\\Runtime\\Shoots.Runtime.Tests\\bin\\Debug\\net8.0\\Shoots.Runtime.Tests.dll (.NETCoreApp,Version=v8.0)",
                        "[xUnit.net 00:00:03.37]     Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [FAIL]",
                        "Failed!"
                    }),
                    new ValidationCommandExecutionResult(0, new[] { "Passed!  - Failed:     0, Passed:   159, Skipped:     0, Total:   159, Duration: 1 s - Shoots.Runtime.Tests.dll (net8.0)" })
                }
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false, true));

            Assert.True(result.Success);
            Assert.Equal("flaky_suspected", result.StabilityClassification);
            Assert.Equal("Flaky suspected", result.StabilityStatus);
            Assert.NotNull(result.FirstFailure);
            Assert.Equal("Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path", result.FirstFailure!.FailingTestName);
            Assert.Contains("Shoots.Runtime.Tests.dll", result.FirstFailure.ProjectOrFile, StringComparison.Ordinal);
            Assert.Contains("[FAIL]", result.FirstFailure.ErrorExcerpt, StringComparison.Ordinal);
            Assert.Equal(result.FirstFailure.ErrorExcerpt, result.FirstFailureText);
            Assert.Single(result.RetryAudits!);
            Assert.Equal("flaky_suspected", result.RetryAudits![0].FinalClassification);
            Assert.Equal("passed", result.RetryAudits![0].Result);
            Assert.Equal("flaky_suspected", result.Stages.Single(stage => stage.StageId == "ui_tests").StabilityClassification);
            Assert.Equal(1, result.Stages.Single(stage => stage.StageId == "ui_tests").RetryCount);
            Assert.NotNull(result.StabilityArtifactPath);
            Assert.True(File.Exists(result.StabilityArtifactPath!));

            var stability = JsonSerializer.Deserialize<ValidationStabilityReport>(File.ReadAllText(result.StabilityArtifactPath!));
            Assert.NotNull(stability);
            Assert.Equal("flaky_suspected", stability!.Classification);
            Assert.Single(stability.RetryAudits);
            Assert.NotNull(stability.FirstFailure);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Non_test_stage_passes_on_retry_is_classified_as_passed_on_retry()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new SequencedValidationCommandExecutor(
            new Dictionary<string, IReadOnlyList<ValidationCommandExecutionResult>>(StringComparer.Ordinal)
            {
                ["build_ui"] = new[]
                {
                    new ValidationCommandExecutionResult(1, new[] { "error CS1000: build failed" }),
                    new ValidationCommandExecutionResult(0, new[] { "Build succeeded." })
                }
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.BuildUiProject,
                new ValidationSettings(false, false, 5, false, false, true));

            Assert.True(result.Success);
            Assert.Equal("passed_on_retry", result.StabilityClassification);
            Assert.Equal("Passed after retry", result.StabilityStatus);
            Assert.Single(result.RetryAudits!);
            Assert.Equal("passed_on_retry", result.RetryAudits![0].FinalClassification);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_history_ledger_is_persisted_and_pruned_to_retention()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var first = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false, false, 5, 3, false));
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false, false, 5, 3, false));
            await Task.Delay(5);
            var third = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false, false, 5, 3, false));
            await Task.Delay(5);
            var fourth = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false, false, 5, 3, false));
            await Task.Delay(5);
            var fifth = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false, false, 5, 3, false));
            await Task.Delay(5);
            var sixth = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false, false, 5, 3, false));

            var ledgerPath = ValidationRunnerService.HistoryLedgerPathForRepo(repoRoot);
            Assert.True(File.Exists(ledgerPath));

            var ledger = ValidationRunnerService.LoadHistoryLedger(repoRoot);
            Assert.Equal(5, ledger.RetentionCount);
            Assert.Equal(new[] { second.RunId, third.RunId, fourth.RunId, fifth.RunId, sixth.RunId }, ledger.Entries.Select(entry => entry.RunId).ToArray());
            Assert.All(ledger.Entries, entry => Assert.Single(entry.StageOutcomes));
            Assert.Equal(sixth.OutputFolder, ledger.Entries[^1].OutputFolder);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Validation_trend_summary_is_generated_from_history_ledger()
    {
        var repoRoot = CreateRepoRoot();

        try
        {
            WriteHistoryLedger(
                repoRoot,
                new[]
                {
                    HistoryEntry("20260310-120000000Z-build-ui", "Build UI project", 0, "passed", "passed", "", "", "", retryUsed: false),
                    HistoryEntry("20260310-120100000Z-ui-tests", "Run UI tests", 1, "failed", "failed", "Tests failed.", "Running UI tests", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path", retryUsed: false),
                    HistoryEntry("20260310-120200000Z-build-ui", "Build UI project", 2, "passed", "passed_on_retry", "", "", "", retryUsed: true),
                    HistoryEntry("20260310-120300000Z-ui-tests", "Run UI tests", 3, "passed", "flaky_suspected", "[FAIL]", "Running UI tests", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path", retryUsed: true)
                });

            ValidationRunnerService.RefreshTrendArtifacts(repoRoot, new ValidationSettings(false, false, 5, false, false, false, 20, 3, false));

            var trend = ValidationRunnerService.LoadTrendSummary(repoRoot);
            Assert.Equal(4, trend.HistoryCount);
            Assert.Equal(3, trend.PassCount);
            Assert.Equal(75, trend.RecentPassRatePercent);
            Assert.Equal(1, trend.StablePassCount);
            Assert.Equal(25, trend.StablePassRatePercent);
            Assert.Equal(1, trend.PassedOnRetryCount);
            Assert.Equal(1, trend.FlakySuspectedCount);
            Assert.Equal("Running UI tests", trend.MostCommonFailingStage);
            Assert.Equal(DateTimeOffset.Parse("2026-03-10T12:00:30+00:00"), trend.LastCleanPassUtc);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Validation_regression_summary_detects_new_failure_after_clean_history()
    {
        var repoRoot = CreateRepoRoot();

        try
        {
            WriteHistoryLedger(
                repoRoot,
                new[]
                {
                    HistoryEntry("20260310-120000000Z-build-ui", "Build UI project", 0, "passed", "passed", "", "", "", retryUsed: false),
                    HistoryEntry("20260310-120100000Z-ui-tests", "Run UI tests", 1, "passed", "passed", "", "", "", retryUsed: false),
                    HistoryEntry("20260310-120200000Z-ui-tests", "Run UI tests", 2, "failed", "failed", "Tests failed.", "Running UI tests", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path", retryUsed: false)
                });

            ValidationRunnerService.RefreshTrendArtifacts(repoRoot, new ValidationSettings(false, false, 5, false, false, false, 20, 3, false));

            var regression = ValidationRunnerService.LoadRegressionSummary(repoRoot);
            Assert.Equal("regression_detected", regression.Classification);
            Assert.Equal("new_failure_after_clean_history", regression.FailureNovelty);
            Assert.Equal("Running UI tests", regression.CurrentFailingStage);
            Assert.Equal("Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path", regression.CurrentFailingTestName);
            Assert.Contains("clean pass", string.Join(" ", regression.Reasons), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Release_baseline_history_is_created_and_pruned_deterministically()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 2, false, true);

        try
        {
            var first = await service.RunAsync(ValidationAction.BuildUiProject, settings);
            ValidationRunnerService.SetActiveReleaseBaseline(repoRoot, first, settings);
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.BuildUiProject, settings);
            ValidationRunnerService.SetActiveReleaseBaseline(repoRoot, second, settings);
            await Task.Delay(5);
            var third = await service.RunAsync(ValidationAction.BuildUiProject, settings);
            ValidationRunnerService.SetActiveReleaseBaseline(repoRoot, third, settings);

            var active = ValidationRunnerService.LoadActiveReleaseBaseline(repoRoot);
            var history = ValidationRunnerService.LoadBaselineHistory(repoRoot);
            var comparison = ValidationRunnerService.LoadBaselineComparison(repoRoot);

            Assert.NotNull(active);
            Assert.Equal(third.RunId, active!.BaselineId);
            Assert.Equal(2, history.RetentionCount);
            Assert.Equal(new[] { second.RunId, third.RunId }, history.Entries.Select(entry => entry.BaselineId).ToArray());
            Assert.Equal(new[] { "superseded", "active" }, history.Entries.Select(entry => entry.Status).ToArray());
            Assert.Equal("ready", comparison.ReadinessClassification);
            Assert.Equal("no_drift", comparison.DriftClassification);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Baseline_comparison_classifies_retry_drift_and_caution_readiness()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true);

        try
        {
            var clean = await service.RunAsync(ValidationAction.BuildUiProject, settings);
            ValidationRunnerService.SetActiveReleaseBaseline(repoRoot, clean, settings);

            var retryOutput = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-retry");
            Directory.CreateDirectory(retryOutput);
            var retryResult = new ValidationRunResult(
                "run-retry",
                "Build UI project",
                retryOutput,
                true,
                "Validation passed after retry (1 stage).",
                "error CS1000: build failed",
                Path.Combine(retryOutput, "01-build-ui.log"),
                clean.StartedUtc.AddMinutes(1),
                clean.CompletedUtc.AddMinutes(1),
                new[]
                {
                    new ValidationStageResult("build_ui", "Building UI", "passed", "Build passed after retry.", Path.Combine(retryOutput, "01-build-ui.log"), 0, 35, "passed_on_retry", 1, Path.Combine(retryOutput, "01-build-ui.retry1.log"))
                },
                "passed_on_retry",
                "Passed after retry");

            ValidationRunnerService.RefreshReleaseBaselineArtifacts(repoRoot, settings, retryResult);
            var comparison = ValidationRunnerService.LoadBaselineComparison(repoRoot);

            Assert.Equal("retry_drift", comparison.DriftClassification);
            Assert.Equal("caution", comparison.ReadinessClassification);
            Assert.Contains("Latest validation passed after retry.", comparison.ReadinessReasons);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Baseline_comparison_classifies_stage_regression_drift_as_not_ready()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true);

        try
        {
            var clean = await service.RunAsync(ValidationAction.BuildUiProject, settings);
            ValidationRunnerService.SetActiveReleaseBaseline(repoRoot, clean, settings);

            var failedOutput = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-failed");
            Directory.CreateDirectory(failedOutput);
            var failedResult = new ValidationRunResult(
                "run-failed",
                "Run UI tests",
                failedOutput,
                false,
                "Validation failed: Tests failed.",
                "Tests failed.",
                Path.Combine(failedOutput, "01-ui-tests.log"),
                clean.StartedUtc.AddMinutes(1),
                clean.CompletedUtc.AddMinutes(1),
                new[]
                {
                    new ValidationStageResult("ui_tests", "Running UI tests", "failed", "Tests failed.", Path.Combine(failedOutput, "01-ui-tests.log"), 1, 55, "failed")
                },
                "failed",
                "Failed");

            ValidationRunnerService.RefreshReleaseBaselineArtifacts(repoRoot, settings, failedResult);
            var comparison = ValidationRunnerService.LoadBaselineComparison(repoRoot);

            Assert.Equal("stage_regression_drift", comparison.DriftClassification);
            Assert.Equal("not_ready", comparison.ReadinessClassification);
            Assert.Contains("Running UI tests", comparison.ChangedFailingStages);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_run_generates_handoff_bundle_and_summary()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));

            var bundlePath = ValidationRunnerService.HandoffBundlePathForRun(result.OutputFolder);
            var summaryPath = ValidationRunnerService.HandoffSummaryPathForRun(result.OutputFolder);
            Assert.True(File.Exists(bundlePath));
            Assert.True(File.Exists(summaryPath));

            var bundle = ValidationRunnerService.LoadHandoffBundleForRun(result.OutputFolder);
            Assert.NotNull(bundle);
            Assert.Equal(result.RunId, bundle!.RunId);
            Assert.Equal("failed", bundle.OverallResult);
            Assert.Equal("failed", bundle.StabilityClassification);
            Assert.Equal("not_ready", bundle.ReadinessClassification);
            Assert.NotNull(bundle.FirstFailure);
            Assert.Equal("Running UI tests", bundle.FirstFailure!.StageLabel);
            Assert.Contains(bundle.ArtifactPaths, artifact => string.Equals(artifact.Label, "validation_result.json", StringComparison.Ordinal));
            Assert.NotEmpty(bundle.BlockedStageNotes);

            var summary = File.ReadAllText(summaryPath);
            Assert.Contains("Overall result: failed", summary, StringComparison.Ordinal);
            Assert.Contains("Stability: Failed", summary, StringComparison.Ordinal);
            Assert.Contains("Release readiness: not ready", summary, StringComparison.Ordinal);
            Assert.Contains("First failure: Running UI tests: Tests failed.", summary, StringComparison.Ordinal);
            Assert.Contains("validation_handoff_bundle.json", summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_handoff_history_is_pruned_to_keep_last_runs()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 2, false, false);

        try
        {
            var first = await service.RunAsync(ValidationAction.BuildUiProject, settings);
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.BuildUiProject, settings);
            await Task.Delay(5);
            var third = await service.RunAsync(ValidationAction.BuildUiProject, settings);

            var history = ValidationRunnerService.LoadHandoffHistory(repoRoot);
            Assert.Equal(2, history.RetentionCount);
            Assert.Equal(new[] { second.RunId, third.RunId }, history.Entries.Select(entry => entry.RunId).ToArray());
            Assert.DoesNotContain(history.Entries, entry => string.Equals(entry.RunId, first.RunId, StringComparison.Ordinal));
            Assert.All(history.Entries, entry =>
            {
                Assert.True(File.Exists(entry.BundlePath));
                Assert.True(File.Exists(entry.SummaryPath));
            });
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_handoff_bundle_includes_previous_bundle_comparison()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new SequencedValidationCommandExecutor(
            new Dictionary<string, IReadOnlyList<ValidationCommandExecutionResult>>(StringComparer.Ordinal)
            {
                ["build_ui"] = new[]
                {
                    new ValidationCommandExecutionResult(0, new[] { "Build succeeded." }),
                    new ValidationCommandExecutionResult(1, new[] { "error CS1000: build failed" })
                }
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var first = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false));
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.BuildUiProject, new ValidationSettings(false, false, 5, false, false));

            var latest = ValidationRunnerService.LoadLatestHandoffBundle(repoRoot);
            Assert.NotNull(latest);
            Assert.NotNull(latest!.PreviousBundleComparison);
            Assert.Equal(first.RunId, latest.PreviousBundleComparison!.PreviousRunId);
            Assert.Equal("passed -> failed", latest.PreviousBundleComparison.ResultChange);
            Assert.Equal("passed -> failed", latest.PreviousBundleComparison.StabilityChange);
            Assert.Contains("first-failure stage none -> Building UI", latest.PreviousBundleComparison.Summary, StringComparison.Ordinal);

            var summary = File.ReadAllText(ValidationRunnerService.HandoffSummaryPathForRun(second.OutputFolder));
            Assert.Contains("Previous bundle: Result passed -> failed;", summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_followup_intake_and_prompt_are_generated_from_latest_handoff()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));

            var intakePath = ValidationRunnerService.FollowupIntakePathForRun(result.OutputFolder);
            var promptPath = ValidationRunnerService.FollowupPromptPathForRun(result.OutputFolder);
            Assert.True(File.Exists(intakePath));
            Assert.True(File.Exists(promptPath));

            var intake = ValidationRunnerService.LoadLatestFollowupIntake(repoRoot);
            Assert.NotNull(intake);
            Assert.Equal("fix_tests", intake!.FollowupCategory);
            Assert.Equal("failed", intake.OverallResult);
            Assert.Equal("failed", intake.StabilityClassification);
            Assert.Equal("not_ready", intake.ReadinessClassification);
            Assert.Equal("Running UI tests", intake.FirstFailure!.StageLabel);
            Assert.Contains("Isolate the first failing test", intake.NextStep, StringComparison.Ordinal);

            var prompt = File.ReadAllText(promptPath);
            Assert.Contains("Follow-up category: fix_tests", prompt, StringComparison.Ordinal);
            Assert.Contains("Recommended next step: Isolate the first failing test or test project, fix it, and rerun UI tests deterministically.", prompt, StringComparison.Ordinal);
            Assert.Contains("validation_followup_intake.json", prompt, StringComparison.Ordinal);
            Assert.Contains("validation_handoff_bundle.json", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Clean_pass_without_active_baseline_is_classified_as_baseline_update_candidate()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.BuildUiProject,
                new ValidationSettings(false, false, 5, false, false));

            var intake = ValidationRunnerService.LoadFollowupIntakeForRun(result.OutputFolder);
            Assert.NotNull(intake);
            Assert.Equal("baseline_update_candidate", intake!.FollowupCategory);
            Assert.Contains("set or refresh the release baseline", intake.NextStep, StringComparison.Ordinal);
            Assert.False(intake.HasRecentRepeatedIssue);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repeated_followup_issue_is_marked_from_recent_intake_history()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false);

        try
        {
            await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);

            var intake = ValidationRunnerService.LoadFollowupIntakeForRun(second.OutputFolder);
            var history = ValidationRunnerService.LoadFollowupHistory(repoRoot);
            Assert.NotNull(intake);
            Assert.True(intake!.HasRecentRepeatedIssue);
            Assert.Contains("Matches recent unresolved follow-up", intake.RepeatedIssueSummary, StringComparison.Ordinal);
            Assert.Equal(2, history.Entries.Count);
            Assert.True(history.Entries[^1].HasRecentRepeatedIssue);
            Assert.Equal("fix_tests", history.Entries[^1].FollowupCategory);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_followup_plan_and_repair_prep_bundle_are_generated_from_fix_tests_intake()
    {
        var repoRoot = CreateRepoRoot();
        SeedFollowupSemanticReuseArtifacts(repoRoot);
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[]
                {
                    "Test run for C:\\dev\\Shoots\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj (.NET 9.0)",
                    "  Failed Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [42 ms]",
                    "Tests failed."
                })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false, EnableSemanticReuseSuggestions: true, EnablePlaybookSuggestions: true));

            var planPath = ValidationRunnerService.FollowupPlanPathForRun(result.OutputFolder);
            var repairPrepPath = ValidationRunnerService.RepairPrepBundlePathForRun(result.OutputFolder);
            Assert.True(File.Exists(planPath));
            Assert.True(File.Exists(repairPrepPath));

            var plan = ValidationRunnerService.LoadLatestFollowupPlan(repoRoot);
            var prep = ValidationRunnerService.LoadLatestRepairPrepBundle(repoRoot);
            Assert.NotNull(plan);
            Assert.NotNull(prep);
            Assert.Equal("fix_tests", plan!.FollowupCategory);
            Assert.Equal(new[]
            {
                "inspect_test_failure",
                "inspect_artifact",
                "review_playbook_or_similar_case",
                "rerun_single_test_or_project",
                "prepare_repair_bundle",
                "rerun_single_stage"
            }, plan.Steps.Select(step => step.StepType).ToArray());
            Assert.Contains("Shoots.Ui.Tests.csproj", plan.TargetScopeSummary, StringComparison.Ordinal);
            Assert.Contains("Rerun the first failing test or test project", plan.RerunScopeRecommendation, StringComparison.Ordinal);
            Assert.True(plan.IsLatestForRepo);
            Assert.Equal("latest", plan.FreshnessStatus);

            Assert.Equal("fix_tests", prep!.FollowupCategory);
            Assert.NotEmpty(prep.SimilarCaseSuggestions);
            Assert.NotEmpty(prep.PlaybookSuggestions);
            Assert.Contains(prep.KeyArtifactPaths, artifact => string.Equals(artifact.Label, "validation_followup_plan.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Older_followup_plan_is_marked_superseded_after_newer_validation_run()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false);

        try
        {
            var first = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);

            var firstPlan = ValidationRunnerService.LoadFollowupPlanForRun(first.OutputFolder);
            var secondPlan = ValidationRunnerService.LoadFollowupPlanForRun(second.OutputFolder);
            Assert.NotNull(firstPlan);
            Assert.NotNull(secondPlan);
            Assert.False(firstPlan!.IsLatestForRepo);
            Assert.Equal("superseded", firstPlan.FreshnessStatus);
            Assert.Contains(second.RunId, firstPlan.FreshnessSummary, StringComparison.Ordinal);
            Assert.True(secondPlan!.IsLatestForRepo);
            Assert.Equal("latest", secondPlan.FreshnessStatus);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repeated_followup_issue_adds_escalation_hint_to_repair_prep_bundle()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false);

        try
        {
            await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);

            var prep = ValidationRunnerService.LoadRepairPrepBundleForRun(second.OutputFolder);
            Assert.NotNull(prep);
            Assert.Contains("Recurring test failure", prep!.EscalationHint, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Followup_plan_steps_include_safe_execution_metadata()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[]
                {
                    "Test run for C:\\dev\\Shoots\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj (.NET 9.0)",
                    "  Failed Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [42 ms]",
                    "Tests failed."
                })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var result = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));

            var plan = ValidationRunnerService.LoadFollowupPlanForRun(result.OutputFolder);
            Assert.NotNull(plan);
            Assert.Equal(
                new[]
                {
                    "inspect_test_failure",
                    "inspect_artifact",
                    "rerun_single_test_or_project",
                    "prepare_repair_bundle",
                    "rerun_single_stage"
                },
                plan!.Steps.Select(step => step.StepType).ToArray());

            var inspectFailure = plan.Steps[0];
            Assert.Equal("view_only", inspectFailure.InteractionMode);
            Assert.Equal("open_log", inspectFailure.ActionKind);
            Assert.EndsWith(".log", inspectFailure.ActionTarget, StringComparison.OrdinalIgnoreCase);

            var rerunScope = Assert.Single(plan.Steps, step => step.StepType == "rerun_single_test_or_project");
            Assert.Equal("rerun_capable", rerunScope.InteractionMode);
            Assert.Equal("rerun_single_test_or_project", rerunScope.ActionKind);
            Assert.Contains("dotnet test", rerunScope.CommandSummary, StringComparison.Ordinal);

            var repairPrep = Assert.Single(plan.Steps, step => step.StepType == "prepare_repair_bundle");
            Assert.Equal("view_only", repairPrep.InteractionMode);
            Assert.Equal("open_repair_prep_bundle", repairPrep.ActionKind);
            Assert.True(string.IsNullOrWhiteSpace(repairPrep.CommandSummary));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Followup_execution_state_records_step_interactions_and_rerun_linkage()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[]
                {
                    "Test run for C:\\dev\\Shoots\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj (.NET 9.0)",
                    "  Failed Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [42 ms]",
                    "Tests failed."
                })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var failed = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));
            var plan = ValidationRunnerService.LoadFollowupPlanForRun(failed.OutputFolder);
            Assert.NotNull(plan);

            var inspectStep = Assert.Single(plan!.Steps, step => step.StepType == "inspect_test_failure");
            var rerunStep = Assert.Single(plan.Steps, step => step.StepType == "rerun_single_stage");

            ValidationRunnerService.RecordFollowupStepInteraction(
                failed.OutputFolder,
                inspectStep.Order,
                inspectStep.StepType,
                "opened",
                inspectStep.ActionKind,
                inspectStep.ActionTarget,
                inspectStep.ActionTarget);

            var rerunOutput = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-rerun-ui-tests");
            Directory.CreateDirectory(rerunOutput);
            var rerunResult = new ValidationRunResult(
                "run-rerun-ui-tests",
                "Guided rerun: UI tests",
                rerunOutput,
                false,
                "Validation failed: Tests failed.",
                "Tests failed.",
                Path.Combine(rerunOutput, "01-ui-tests.log"),
                DateTimeOffset.UtcNow.AddSeconds(-30),
                DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult(
                        "ui_tests",
                        "Running UI tests",
                        "failed",
                        "Tests failed.",
                        Path.Combine(rerunOutput, "01-ui-tests.log"),
                        1,
                        42,
                        "failed")
                },
                "failed",
                "Failed",
                new ValidationFirstFailure(
                    "ui_tests",
                    "Running UI tests",
                    "C:\\dev\\Shoots\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj",
                    "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path",
                    "Tests failed.",
                    Path.Combine(rerunOutput, "01-ui-tests.log"),
                    "Tests failed.",
                    1),
                null,
                Path.Combine(rerunOutput, "validation_stability.json"),
                "single_stage_manual_mode");
            File.WriteAllText(
                Path.Combine(rerunOutput, "validation_result.json"),
                JsonSerializer.Serialize(rerunResult, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(
                Path.Combine(rerunOutput, "validation_stability.json"),
                "{}");

            ValidationRunnerService.RecordFollowupRerun(
                failed.OutputFolder,
                rerunStep.Order,
                rerunStep.StepType,
                rerunStep.ActionKind,
                "Guided rerun: UI tests",
                rerunStep.CommandSummary,
                rerunResult,
                "unchanged");

            var state = ValidationRunnerService.LoadFollowupExecutionStateForRun(failed.OutputFolder);
            Assert.NotNull(state);
            Assert.Equal(failed.RunId, state!.SourceValidationRunId);
            Assert.Equal(plan.PlanPath, state.SourceFollowupPlanPath);

            var inspectState = Assert.Single(state.Steps, step => step.Order == inspectStep.Order && step.StepType == inspectStep.StepType);
            Assert.Equal("opened", inspectState.CompletionState);
            Assert.Equal("open_log", inspectState.LastActionKind);

            var rerunState = Assert.Single(state.Steps, step => step.Order == rerunStep.Order && step.StepType == rerunStep.StepType);
            Assert.Equal("completed_by_validation", rerunState.CompletionState);
            Assert.Equal("rerun_single_stage", rerunState.LastActionKind);
            Assert.Equal(Path.Combine(rerunOutput, "validation_result.json"), rerunState.EvidencePath);

            Assert.NotNull(state.LatestRerun);
            Assert.Equal(failed.RunId, state.LatestRerun!.SourceValidationRunId);
            Assert.Equal(rerunResult.RunId, state.LatestRerun.RerunValidationRunId);
            Assert.Equal(rerunStep.CommandSummary, state.LatestRerun.RerunCommandSummary);
            Assert.Equal("unchanged", state.LatestRerun.OutcomeClassification);
            Assert.True(File.Exists(ValidationRunnerService.FollowupExecutionPathForRun(failed.OutputFolder)));

            var outcome = ValidationRunnerService.LoadFollowupExecutionOutcomeForRun(failed.OutputFolder);
            Assert.NotNull(outcome);
            Assert.Equal("unchanged", outcome!.OutcomeClassification);
            Assert.Equal("prepare_repair", outcome.RecommendedNextState);
            Assert.Contains("stayed unchanged", outcome.OutcomeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(ValidationRunnerService.FollowupExecutionOutcomePathForRun(failed.OutputFolder)));

            var escalation = ValidationRunnerService.LoadFollowupEscalationForRun(failed.OutputFolder);
            Assert.NotNull(escalation);
            Assert.Equal("watch_recurring_issue", escalation!.EscalationClassification);
            Assert.Equal("prepare_repair", escalation.SuggestedNextState);
            Assert.True(File.Exists(ValidationRunnerService.FollowupEscalationPathForRun(failed.OutputFolder)));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Followup_execution_outcome_is_inconclusive_before_any_guided_rerun()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var failed = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));

            var outcome = ValidationRunnerService.LoadFollowupExecutionOutcomeForRun(failed.OutputFolder);
            Assert.NotNull(outcome);
            Assert.Equal("inconclusive", outcome!.OutcomeClassification);
            Assert.Equal("inspect_artifacts_more", outcome.RecommendedNextState);
            Assert.False(outcome.HasRecordedRerun);
            Assert.Contains("No guided rerun result is recorded yet.", outcome.OutcomeSummary, StringComparison.Ordinal);

            var escalation = ValidationRunnerService.LoadFollowupEscalationForRun(failed.OutputFolder);
            Assert.NotNull(escalation);
            Assert.Equal("no_escalation", escalation!.EscalationClassification);

            var review = ValidationRunnerService.LoadFollowupResolutionReviewForRun(failed.OutputFolder);
            Assert.NotNull(review);
            Assert.Equal("unresolved", review!.ResolutionClassification);
            Assert.Equal("unresolved", review.CurrentResolutionState);
            Assert.Equal("still_open", review.IssueClosureStatus);
            Assert.True(File.Exists(ValidationRunnerService.FollowupResolutionReviewPathForRun(failed.OutputFolder)));

            var handoff = ValidationRunnerService.LoadResolutionHandoffForRun(failed.OutputFolder);
            Assert.NotNull(handoff);
            Assert.Equal("no_handoff", handoff!.CandidateState);
            Assert.True(File.Exists(ValidationRunnerService.ResolutionHandoffPathForRun(failed.OutputFolder)));

            var promotionReview = ValidationRunnerService.LoadResolutionPromotionReviewForRun(failed.OutputFolder);
            Assert.NotNull(promotionReview);
            Assert.Equal("do_not_promote", promotionReview!.PromotionRecommendationState);
            Assert.True(File.Exists(ValidationRunnerService.ResolutionPromotionReviewPathForRun(failed.OutputFolder)));

            var decisionSummary = ValidationRunnerService.LoadReleaseDecisionSummaryForRun(failed.OutputFolder);
            Assert.NotNull(decisionSummary);
            Assert.Equal("resolution_not_stable_enough", decisionSummary!.DecisionState);
            Assert.True(File.Exists(ValidationRunnerService.ReleaseDecisionSummaryPathForRun(failed.OutputFolder)));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Guided_full_stage_rerun_success_is_classified_resolved()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var failed = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));
            var plan = ValidationRunnerService.LoadFollowupPlanForRun(failed.OutputFolder);
            Assert.NotNull(plan);
            var rerunStep = Assert.Single(plan!.Steps, step => step.StepType == "rerun_single_stage");
            var rerunResult = CreateGuidedRerunResult(
                repoRoot,
                "run-guided-resolved",
                "Guided rerun: UI tests",
                "ui_tests",
                "Running UI tests",
                "passed",
                true);

            ValidationRunnerService.RecordFollowupRerun(
                failed.OutputFolder,
                rerunStep.Order,
                rerunStep.StepType,
                rerunStep.ActionKind,
                "Guided rerun: UI tests",
                rerunStep.CommandSummary,
                rerunResult,
                "improved");

            var outcome = ValidationRunnerService.LoadFollowupExecutionOutcomeForRun(failed.OutputFolder);
            Assert.NotNull(outcome);
            Assert.Equal("resolved", outcome!.OutcomeClassification);
            Assert.Equal("no_further_action", outcome.RecommendedNextState);
            Assert.Equal("full_stage_scope", outcome.ComparisonScope);

            var review = ValidationRunnerService.LoadFollowupResolutionReviewForRun(failed.OutputFolder);
            Assert.NotNull(review);
            Assert.Equal("closed_by_guided_rerun", review!.ResolutionClassification);
            Assert.Equal("closed_by_guided_rerun", review.CurrentResolutionState);
            Assert.Equal("closed", review.IssueClosureStatus);

            var handoff = ValidationRunnerService.LoadResolutionHandoffForRun(failed.OutputFolder);
            Assert.NotNull(handoff);
            Assert.Equal("readiness_review_candidate", handoff!.CandidateState);

            var promotionReview = ValidationRunnerService.LoadResolutionPromotionReviewForRun(failed.OutputFolder);
            Assert.NotNull(promotionReview);
            Assert.Equal("recommend_review_only", promotionReview!.PromotionRecommendationState);
            Assert.Contains("Review only.", promotionReview.PromotionRecommendationSummary, StringComparison.Ordinal);

            var decisionSummary = ValidationRunnerService.LoadReleaseDecisionSummaryForRun(failed.OutputFolder);
            Assert.NotNull(decisionSummary);
            Assert.Equal("needs_more_validation_evidence", decisionSummary!.DecisionState);
            Assert.Contains("Issue appears resolved, but current release readiness is still not ready.", decisionSummary.ContradictionNotes);
            Assert.Contains("Current release readiness still needs more validation evidence.", decisionSummary.DeferralNotes);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Guided_narrow_rerun_success_is_classified_improved_and_requests_full_stage()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var failed = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));
            var plan = ValidationRunnerService.LoadFollowupPlanForRun(failed.OutputFolder);
            Assert.NotNull(plan);
            var rerunStep = Assert.Single(plan!.Steps, step => step.StepType == "rerun_single_test_or_project");
            var rerunResult = CreateGuidedRerunResult(
                repoRoot,
                "run-guided-improved",
                "Guided rerun: UI tests",
                "ui_tests",
                "Running UI tests",
                "passed",
                true);

            ValidationRunnerService.RecordFollowupRerun(
                failed.OutputFolder,
                rerunStep.Order,
                rerunStep.StepType,
                rerunStep.ActionKind,
                "Guided rerun: UI tests",
                rerunStep.CommandSummary,
                rerunResult,
                "improved");

            var outcome = ValidationRunnerService.LoadFollowupExecutionOutcomeForRun(failed.OutputFolder);
            Assert.NotNull(outcome);
            Assert.Equal("improved", outcome!.OutcomeClassification);
            Assert.Equal("rerun_full_stage", outcome.RecommendedNextState);
            Assert.Equal("narrow_stage_scope", outcome.ComparisonScope);

            var review = ValidationRunnerService.LoadFollowupResolutionReviewForRun(failed.OutputFolder);
            Assert.NotNull(review);
            Assert.Equal("improved_but_open", review!.ResolutionClassification);
            Assert.Equal("improved_but_open", review.CurrentResolutionState);
            Assert.Equal("partially_resolved", review.IssueClosureStatus);

            var handoff = ValidationRunnerService.LoadResolutionHandoffForRun(failed.OutputFolder);
            Assert.NotNull(handoff);
            Assert.Equal("no_handoff", handoff!.CandidateState);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Guided_regressed_outcome_requests_more_artifact_review()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);

        try
        {
            var failed = await service.RunAsync(
                ValidationAction.RunFullValidationLoop,
                new ValidationSettings(false, false, 5, false, false));
            var plan = ValidationRunnerService.LoadFollowupPlanForRun(failed.OutputFolder);
            Assert.NotNull(plan);
            var rerunStep = Assert.Single(plan!.Steps, step => step.StepType == "rerun_single_stage");
            var rerunResult = CreateGuidedRerunResult(
                repoRoot,
                "run-guided-regressed",
                "Guided rerun: UI tests",
                "ui_tests",
                "Running UI tests",
                "failed",
                false);

            ValidationRunnerService.RecordFollowupRerun(
                failed.OutputFolder,
                rerunStep.Order,
                rerunStep.StepType,
                rerunStep.ActionKind,
                "Guided rerun: UI tests",
                rerunStep.CommandSummary,
                rerunResult,
                "regressed");

            var outcome = ValidationRunnerService.LoadFollowupExecutionOutcomeForRun(failed.OutputFolder);
            Assert.NotNull(outcome);
            Assert.Equal("regressed", outcome!.OutcomeClassification);
            Assert.Equal("inspect_artifacts_more", outcome.RecommendedNextState);

            var review = ValidationRunnerService.LoadFollowupResolutionReviewForRun(failed.OutputFolder);
            Assert.NotNull(review);
            Assert.Equal("regressed", review!.ResolutionClassification);
            Assert.Equal("regressed", review.CurrentResolutionState);
            Assert.Equal("still_open", review.IssueClosureStatus);

            var handoff = ValidationRunnerService.LoadResolutionHandoffForRun(failed.OutputFolder);
            Assert.NotNull(handoff);
            Assert.Equal("no_handoff", handoff!.CandidateState);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repeated_unresolved_guided_outcomes_trigger_escalation_artifact()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[] { "Tests failed." })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false);

        try
        {
            var first = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);
            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);

            foreach (var (result, rerunId) in new[] { (first, "run-guided-repeat-a"), (second, "run-guided-repeat-b") })
            {
                var plan = ValidationRunnerService.LoadFollowupPlanForRun(result.OutputFolder);
                Assert.NotNull(plan);
                var rerunStep = Assert.Single(plan!.Steps, step => step.StepType == "rerun_single_stage");
                var rerunResult = CreateGuidedRerunResult(
                    repoRoot,
                    rerunId,
                    "Guided rerun: UI tests",
                    "ui_tests",
                    "Running UI tests",
                    "failed",
                    false);

                ValidationRunnerService.RecordFollowupRerun(
                    result.OutputFolder,
                    rerunStep.Order,
                    rerunStep.StepType,
                    rerunStep.ActionKind,
                    "Guided rerun: UI tests",
                    rerunStep.CommandSummary,
                    rerunResult,
                    "unchanged");
            }

            var escalation = ValidationRunnerService.LoadFollowupEscalationForRun(second.OutputFolder);
            Assert.NotNull(escalation);
            Assert.Equal("escalate_recurring_issue", escalation!.EscalationClassification);
            Assert.Equal("escalate_recurring_issue", escalation.SuggestedNextState);
            Assert.True(escalation.RepeatedUnresolvedCount >= 2);
            Assert.Contains("Recurring unresolved guided outcomes", escalation.SuggestedNextAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Closed_guided_resolution_is_marked_reopened_when_later_validation_reintroduces_same_issue()
    {
        var repoRoot = CreateRepoRoot();
        var executor = new ScriptedValidationCommandExecutor(
            new Dictionary<string, ValidationCommandExecutionResult>(StringComparer.Ordinal)
            {
                ["build_ui"] = new(0, new[] { "Build succeeded." }),
                ["ui_tests"] = new(1, new[]
                {
                    "Test run for C:\\dev\\Shoots\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj (.NET 9.0)",
                    "  Failed Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path [42 ms]",
                    "Tests failed."
                })
            });
        var service = new ValidationRunnerService(repoRoot, executor);
        var settings = new ValidationSettings(false, false, 5, false, false);

        try
        {
            var first = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);
            var firstPlan = ValidationRunnerService.LoadFollowupPlanForRun(first.OutputFolder);
            Assert.NotNull(firstPlan);
            var rerunStep = Assert.Single(firstPlan!.Steps, step => step.StepType == "rerun_single_stage");
            var rerunResult = CreateGuidedRerunResult(
                repoRoot,
                "run-guided-closed",
                "Guided rerun: UI tests",
                "ui_tests",
                "Running UI tests",
                "passed",
                true);

            ValidationRunnerService.RecordFollowupRerun(
                first.OutputFolder,
                rerunStep.Order,
                rerunStep.StepType,
                rerunStep.ActionKind,
                "Guided rerun: UI tests",
                rerunStep.CommandSummary,
                rerunResult,
                "improved");

            await Task.Delay(5);
            var second = await service.RunAsync(ValidationAction.RunFullValidationLoop, settings);

            var firstReview = ValidationRunnerService.LoadFollowupResolutionReviewForRun(first.OutputFolder);
            Assert.NotNull(firstReview);
            Assert.Equal("closed_by_guided_rerun", firstReview!.ResolutionClassification);
            Assert.Equal("superseded", firstReview.CurrentResolutionState);
            Assert.Equal("reopened_by_later_validation", firstReview.ReopenStatus);
            Assert.Contains(second.RunId, firstReview.ReopenSummary, StringComparison.Ordinal);

            var secondReview = ValidationRunnerService.LoadFollowupResolutionReviewForRun(second.OutputFolder);
            Assert.NotNull(secondReview);
            Assert.Equal("unresolved", secondReview!.ResolutionClassification);
            Assert.Equal("unresolved", secondReview.CurrentResolutionState);

            var firstHandoff = ValidationRunnerService.LoadResolutionHandoffForRun(first.OutputFolder);
            Assert.NotNull(firstHandoff);
            Assert.Equal("no_handoff", firstHandoff!.CandidateState);

            var firstPromotionReview = ValidationRunnerService.LoadResolutionPromotionReviewForRun(first.OutputFolder);
            Assert.NotNull(firstPromotionReview);
            Assert.Equal("do_not_promote", firstPromotionReview!.PromotionRecommendationState);
            Assert.Equal("superseded", firstPromotionReview.FreshnessStatus);

            var firstDecisionSummary = ValidationRunnerService.LoadReleaseDecisionSummaryForRun(first.OutputFolder);
            Assert.NotNull(firstDecisionSummary);
            Assert.Equal("resolution_not_stable_enough", firstDecisionSummary!.DecisionState);
            Assert.Equal("superseded", firstDecisionSummary.FreshnessStatus);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    private static string CreateRepoRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shoots-validation-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Shoots.sln"), "Microsoft Visual Studio Solution File");
        return root;
    }

    private static ValidationRunResult CreateGuidedRerunResult(
        string repoRoot,
        string runId,
        string actionLabel,
        string stageId,
        string stageLabel,
        string status,
        bool success)
    {
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", runId);
        Directory.CreateDirectory(outputFolder);
        var logPath = Path.Combine(outputFolder, "01-guided-rerun.log");
        var stabilityPath = Path.Combine(outputFolder, "validation_stability.json");
        var result = new ValidationRunResult(
            runId,
            actionLabel,
            outputFolder,
            success,
            success ? "Validation passed (1 stage)." : "Validation failed: Tests failed.",
            success ? null : "Tests failed.",
            success ? null : logPath,
            DateTimeOffset.UtcNow.AddSeconds(-30),
            DateTimeOffset.UtcNow,
            new[]
            {
                new ValidationStageResult(
                    stageId,
                    stageLabel,
                    status,
                    success ? "Stage passed." : "Tests failed.",
                    logPath,
                    success ? 0 : 1,
                    30,
                    success ? "passed" : "failed")
            },
            success ? "passed" : "failed",
            success ? "Passed cleanly" : "Failed",
            success
                ? null
                : new ValidationFirstFailure(
                    stageId,
                    stageLabel,
                    "C:\\dev\\Shoots\\ui\\Shoots.Ui.Tests\\Shoots.Ui.Tests.csproj",
                    "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path",
                    "Tests failed.",
                    logPath,
                    "Tests failed.",
                    1),
            null,
            stabilityPath,
            "single_stage_manual_mode");
        File.WriteAllText(
            Path.Combine(outputFolder, "validation_result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(stabilityPath, "{}");
        return result;
    }

    private static void SeedFollowupSemanticReuseArtifacts(string repoRoot)
    {
        var artifactsRoot = SemanticReuseService.ArtifactsRootForRepo(repoRoot);
        Directory.CreateDirectory(artifactsRoot);
        var index = new SemanticReuseIndexLedger(
            20,
            DateTimeOffset.UtcNow,
            new[]
            {
                new SemanticReuseIndexedCase(
                    "repair-doc-001",
                    "repair_bundle_summary",
                    "Repair route gate test failure",
                    "Running UI tests: Tests failed. Repair outcome passed.",
                    "passed",
                    "run-old",
                    Path.Combine(artifactsRoot, "repair-doc-001.json"),
                    new[]
                    {
                        new SemanticReuseArtifactLink("Repair comparison", Path.Combine(artifactsRoot, "repair-doc-001.json")),
                        new SemanticReuseArtifactLink("Changed file", Path.Combine(repoRoot, "src", "Runtime", "RouteGate.cs"))
                    },
                    new[]
                    {
                        new SemanticReuseMetadataField("failing_stage", "Running UI tests"),
                        new SemanticReuseMetadataField("failing_test_name", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path"),
                        new SemanticReuseMetadataField("first_failure_excerpt", "Tests failed."),
                        new SemanticReuseMetadataField("changed_file_names", "RouteGate.cs")
                    },
                    "Running UI tests Tests failed RouteGate",
                    "fingerprint-001",
                    DateTimeOffset.UtcNow.AddMinutes(-5))
            });
        File.WriteAllText(SemanticReuseService.IndexPathForRepo(repoRoot), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));

        var playbooks = new SemanticReusePlaybookCatalog(
            2,
            DateTimeOffset.UtcNow,
            new[]
            {
                new SemanticReusePlaybook(
                    "playbook-001",
                    "repair_bundle_reference",
                    "common_repair_review_path",
                    "Repair review path: Running UI tests",
                    "Evidence-backed for stage Running UI tests: 2 corroborating outcome(s); clean pass 2.",
                    "Repeated repair references for Running UI tests later produced clean pass 2.",
                    "corroborated",
                    2,
                    new[] { new SemanticReuseMetadataField("failing_stage", "Running UI tests") },
                    new[] { "repair-doc-001" },
                    new[] { Path.Combine(artifactsRoot, "repair-doc-001.json") },
                    new[] { Path.Combine(artifactsRoot, "repair-evidence-001.json") },
                    new[] { "passed" },
                    DateTimeOffset.UtcNow.AddMinutes(-2))
            });
        File.WriteAllText(SemanticReuseService.PlaybookPathForRepo(repoRoot), JsonSerializer.Serialize(playbooks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteHistoryLedger(string repoRoot, IReadOnlyList<ValidationHistoryEntry> entries)
    {
        var artifactsRoot = ValidationRunnerService.ValidationArtifactsRootForRepo(repoRoot);
        Directory.CreateDirectory(artifactsRoot);
        var ledger = new ValidationHistoryLedger(entries.Count, entries);
        File.WriteAllText(
            ValidationRunnerService.HistoryLedgerPathForRepo(repoRoot),
            JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ValidationHistoryEntry HistoryEntry(
        string runId,
        string actionLabel,
        int minuteOffset,
        string overallResult,
        string stabilityClassification,
        string firstFailureSummary,
        string firstFailureStage,
        string failingTestName,
        bool retryUsed)
    {
        var startedUtc = DateTimeOffset.Parse("2026-03-10T12:00:00+00:00").AddMinutes(minuteOffset);
        var completedUtc = startedUtc.AddSeconds(30);
        var outputFolder = Path.Combine(Path.GetTempPath(), runId);
        var stageStatus = string.Equals(overallResult, "passed", StringComparison.Ordinal) ? "passed" : "failed";
        return new ValidationHistoryEntry(
            runId,
            actionLabel,
            outputFolder,
            Path.Combine(outputFolder, "validation_result.json"),
            Path.Combine(outputFolder, "validation_stability.json"),
            startedUtc,
            completedUtc,
            overallResult,
            stabilityClassification,
            stabilityClassification switch
            {
                "passed_on_retry" => "Passed after retry",
                "flaky_suspected" => "Flaky suspected",
                "failed" => "Failed",
                _ => "Passed cleanly"
            },
            firstFailureSummary,
            firstFailureStage,
            failingTestName,
            retryUsed,
            retryUsed ? 1 : 0,
            new[]
            {
                new ValidationHistoryStageOutcome(
                    "ui_tests",
                    string.IsNullOrWhiteSpace(firstFailureStage) ? "Building UI" : firstFailureStage,
                    stageStatus,
                    stabilityClassification,
                    retryUsed)
            });
    }

    private sealed class ScriptedValidationCommandExecutor : IValidationCommandExecutor
    {
        private readonly IReadOnlyDictionary<string, ValidationCommandExecutionResult> _results;

        public ScriptedValidationCommandExecutor(IReadOnlyDictionary<string, ValidationCommandExecutionResult> results)
        {
            _results = results;
        }

        public Task<ValidationCommandExecutionResult> ExecuteAsync(
            ValidationCommandSpec command,
            string workingDirectory,
            string logPath,
            Action<string> onOutput,
            CancellationToken ct)
        {
            var result = _results.TryGetValue(command.StageId, out var mapped)
                ? mapped
                : new ValidationCommandExecutionResult(0, Array.Empty<string>());

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, result.OutputLines);
            foreach (var line in result.OutputLines)
            {
                onOutput(line);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class RecordingValidationCommandExecutor : IValidationCommandExecutor
    {
        public List<string> WorkingDirectories { get; } = new();

        public Task<ValidationCommandExecutionResult> ExecuteAsync(
            ValidationCommandSpec command,
            string workingDirectory,
            string logPath,
            Action<string> onOutput,
            CancellationToken ct)
        {
            WorkingDirectories.Add(workingDirectory);
            var result = new ValidationCommandExecutionResult(0, new[] { $"{command.StageLabel} passed." });
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, result.OutputLines);
            foreach (var line in result.OutputLines)
            {
                onOutput(line);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class SequencedValidationCommandExecutor : IValidationCommandExecutor
    {
        private readonly Dictionary<string, Queue<ValidationCommandExecutionResult>> _results;

        public SequencedValidationCommandExecutor(IReadOnlyDictionary<string, IReadOnlyList<ValidationCommandExecutionResult>> results)
        {
            _results = results.ToDictionary(
                pair => pair.Key,
                pair => new Queue<ValidationCommandExecutionResult>(pair.Value),
                StringComparer.Ordinal);
        }

        public Task<ValidationCommandExecutionResult> ExecuteAsync(
            ValidationCommandSpec command,
            string workingDirectory,
            string logPath,
            Action<string> onOutput,
            CancellationToken ct)
        {
            var queue = _results.TryGetValue(command.StageId, out var mapped)
                ? mapped
                : new Queue<ValidationCommandExecutionResult>(new[] { new ValidationCommandExecutionResult(0, Array.Empty<string>()) });
            var result = queue.Count > 0
                ? queue.Dequeue()
                : new ValidationCommandExecutionResult(0, Array.Empty<string>());

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, result.OutputLines);
            foreach (var line in result.OutputLines)
            {
                onOutput(line);
            }

            return Task.FromResult(result);
        }
    }
}
