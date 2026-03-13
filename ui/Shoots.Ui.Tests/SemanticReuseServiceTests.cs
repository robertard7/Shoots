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

public sealed class SemanticReuseServiceTests
{
    [Fact]
    public void Semantic_reuse_index_is_deterministic_and_suppresses_duplicates()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            WriteFailedValidationResult(repoRoot, "run-001", "Running UI tests", "Tests failed.", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path");
            WriteRepairComparison(repoRoot, "repair-001", "Running UI tests", "Tests failed.", "improved");

            var service = new SemanticReuseService(repoRoot);
            var settings = new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true);

            var first = service.RefreshLocalIndex(settings);
            var second = service.RefreshLocalIndex(settings);

            Assert.True(File.Exists(service.DesignNotePath));
            Assert.True(File.Exists(service.IndexPath));
            Assert.True(File.Exists(service.LinkagePath));
            Assert.Equal(first.Entries.Select(entry => entry.DocumentId).ToArray(), second.Entries.Select(entry => entry.DocumentId).ToArray());
            Assert.Equal(first.Entries.Count, first.Entries.Select(entry => entry.DocumentId).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(first.Entries, entry => entry.CaseType == "validation_failure_record");
            Assert.Contains(first.Entries, entry => entry.CaseType == "repair_bundle_summary");
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Semantic_reuse_search_ranks_matching_validation_case_before_unrelated_cases()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            WriteFailedValidationResult(repoRoot, "run-ui", "Running UI tests", "Tests failed in ui tests.", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path");
            WriteFailedValidationResult(repoRoot, "run-smoke", "Running smoke validation", "Smoke failed.", "SmokeScenario");
            File.WriteAllText(
                Path.Combine(repoRoot, "provider_diagnostics.json"),
                JsonSerializer.Serialize(new[]
                {
                    new { Provider = "Ollama", State = "unavailable", Classification = "timeout", ErrorCode = "ui.provider.timeout", Summary = "Probe timed out.", ObservedAtUtc = DateTimeOffset.UtcNow, Endpoint = "http://localhost:11434" }
                }, JsonOptions()));

            var service = new SemanticReuseService(repoRoot);
            var result = await service.FindSimilarCasesAsync(
                new[]
                {
                    new SemanticReuseQuery(
                        "current-validation",
                        "Current validation failure",
                        new[] { "validation_failure_record" },
                        "Run UI tests Tests failed RouteGate",
                        "failed",
                        new[]
                        {
                            new SemanticReuseMetadataField("failing_stage", "Running UI tests"),
                            new SemanticReuseMetadataField("failing_test_name", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path")
                        },
                        Array.Empty<string>())
                },
                new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true));

            Assert.NotEmpty(result.Suggestions);
            Assert.Equal("run-ui", result.Suggestions[0].SourceRunId);
            Assert.DoesNotContain(result.Suggestions, suggestion => suggestion.CaseType == "provider_diagnostics_episode");
            Assert.Contains("same failing stage", result.Suggestions[0].MatchExplanation, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Semantic_reuse_search_falls_back_to_local_ranking_when_vector_store_is_unavailable()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            WriteFailedValidationResult(repoRoot, "run-001", "Running UI tests", "Tests failed.", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path");
            var service = new SemanticReuseService(repoRoot, new ThrowingVectorStore());

            var result = await service.FindSimilarCasesAsync(
                new[]
                {
                    new SemanticReuseQuery(
                        "current-validation",
                        "Current validation failure",
                        new[] { "validation_failure_record" },
                        "Tests failed RouteGate",
                        "failed",
                        new[] { new SemanticReuseMetadataField("failing_stage", "Running UI tests") },
                        Array.Empty<string>())
                },
                new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true));

            Assert.Equal("local_only", result.Status);
            Assert.NotEmpty(result.Suggestions);
            Assert.Contains("Qdrant was unavailable", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Provider_diagnostics_are_indexed_only_when_enabled()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(repoRoot, "provider_diagnostics.json"),
                JsonSerializer.Serialize(new[]
                {
                    new { Provider = "Ollama", State = "unavailable", Classification = "timeout", ErrorCode = "ui.provider.timeout", Summary = "Probe timed out.", ObservedAtUtc = DateTimeOffset.UtcNow, Endpoint = "http://localhost:11434" }
                }, JsonOptions()));

            var service = new SemanticReuseService(repoRoot);
            var disabled = service.RefreshLocalIndex(new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, false));
            var enabled = service.RefreshLocalIndex(new ValidationSettings(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true));

            Assert.DoesNotContain(disabled.Entries, entry => entry.CaseType == "provider_diagnostics_episode");
            Assert.Contains(enabled.Entries, entry => entry.CaseType == "provider_diagnostics_episode");
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Semantic_reuse_planning_queries_prioritize_exact_linked_history_and_positive_cases()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            var linkedRunPath = WriteGeneratedOutputLink(
                repoRoot,
                "generated-run-001",
                "Semantic Planner",
                "Build validation assistant",
                "passed",
                "Validation passed cleanly.");
            WriteFailedValidationResult(repoRoot, "run-unrelated", "Running UI tests", "Unrelated failure.", "Other.Test");

            var service = new SemanticReuseService(repoRoot);
            var result = await service.FindSimilarCasesAsync(
                new[]
                {
                    new SemanticReuseQuery(
                        "planning-001",
                        "Current planning context",
                        new[] { "generated_output_pattern", "validation_failure_record" },
                        "Semantic Planner build validation assistant",
                        string.Empty,
                        new[]
                        {
                            new SemanticReuseMetadataField("project_name", "Semantic Planner"),
                            new SemanticReuseMetadataField("source_path", linkedRunPath)
                        },
                        new[] { linkedRunPath },
                        ContextKind: "planning",
                        PreferredSourceRunIds: new[] { "generated-run-001" })
                },
                CreateSemanticReuseSettings(onlyPositiveCases: true));

            Assert.NotEmpty(result.Suggestions);
            Assert.Equal("generated_output_pattern", result.Suggestions[0].CaseType);
            Assert.Equal("planning", result.Suggestions[0].ContextKind);
            Assert.Contains("exact linked history", result.Suggestions[0].MatchExplanation, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Suggestions, suggestion => string.Equals(suggestion.CaseType, "validation_failure_record", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Repair_reference_outcomes_are_persisted_as_usefulness_evidence()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            var linkedRunPath = WriteGeneratedOutputLink(
                repoRoot,
                "generated-run-002",
                "Repair Assist",
                "Repair failing validation",
                "passed",
                "Validation passed cleanly.");
            var service = new SemanticReuseService(repoRoot);
            var settings = CreateSemanticReuseSettings();
            var index = service.RefreshLocalIndex(settings);
            var generatedDocument = Assert.Single(index.Entries, entry => entry.CaseType == "generated_output_pattern");

            var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-after-repair");
            Directory.CreateDirectory(outputFolder);
            var validationResult = new ValidationRunResult(
                "validation-after-repair",
                "Validate generated output",
                outputFolder,
                true,
                "Validation passed cleanly.",
                null,
                null,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow,
                new[]
                {
                    new ValidationStageResult("ui_tests", "Running UI tests", "passed", "Tests passed.", Path.Combine(outputFolder, "02-ui-tests.log"), 0, 25)
                });
            File.WriteAllText(Path.Combine(outputFolder, "validation_result.json"), JsonSerializer.Serialize(validationResult, JsonOptions()));
            var bundle = new RepairBundle(
                "repair-refs-001",
                repoRoot,
                linkedRunPath,
                "generated-run-002",
                linkedRunPath,
                "validation-before-repair",
                "Running UI tests",
                "Tests failed.",
                outputFolder,
                null,
                new[] { outputFolder },
                DateTimeOffset.UtcNow,
                new[]
                {
                    new RepairReferenceCase(
                        generatedDocument.DocumentId,
                        "planning",
                        "Current planning context",
                        generatedDocument.CaseType,
                        generatedDocument.Title,
                        generatedDocument.Outcome,
                        "High",
                        "exact linked history",
                        generatedDocument.SourceRunId,
                        generatedDocument.PrimaryArtifactPath,
                        new[] { generatedDocument.PrimaryArtifactPath },
                        string.Empty)
                });

            var ledger = SemanticReuseService.RecordRepairReferenceOutcome(repoRoot, bundle, validationResult, "passed", settings);
            Assert.Single(ledger.Entries);
            Assert.Equal(generatedDocument.DocumentId, ledger.Entries[0].DocumentId);
            Assert.Equal("passed", ledger.Entries[0].OutcomeClassification);
            Assert.True(File.Exists(SemanticReuseService.UsefulnessPathForRepo(repoRoot)));

            var result = await service.FindSimilarCasesAsync(
                new[]
                {
                    new SemanticReuseQuery(
                        "planning-002",
                        "Current planning context",
                        new[] { "generated_output_pattern" },
                        "Repair Assist repair failing validation",
                        string.Empty,
                        new[] { new SemanticReuseMetadataField("project_name", "Repair Assist") },
                        new[] { linkedRunPath },
                        ContextKind: "planning",
                        PreferredSourceRunIds: new[] { "generated-run-002" })
                },
                settings);

            Assert.NotEmpty(result.Suggestions);
            Assert.Contains("clean pass 1", result.Suggestions[0].UsefulnessSummary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Suggestion_outcomes_are_tracked_by_context_and_emit_effectiveness_summary()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            var runPath = WriteGeneratedOutputLink(
                repoRoot,
                "generated-run-003",
                "Context Learning",
                "Track reuse outcomes",
                "passed",
                "Validation passed cleanly.");
            WriteFailedValidationResult(repoRoot, "run-context", "Running UI tests", "Tests failed.", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path");

            var settings = CreateSemanticReuseSettings();
            var service = new SemanticReuseService(repoRoot);
            var index = service.RefreshLocalIndex(settings);
            var generatedDocument = Assert.Single(index.Entries, entry => entry.CaseType == "generated_output_pattern");
            var failureDocument = Assert.Single(index.Entries, entry => entry.CaseType == "validation_failure_record");

            SemanticReuseService.RecordSuggestionOutcome(
                repoRoot,
                new[] { ToReferenceCase(generatedDocument, "planning", "Current planning context") },
                "planning",
                "generated-run-003",
                "validation-planning-001",
                string.Empty,
                "passed",
                "Validation passed cleanly.",
                Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-planning-001", "validation_result.json"),
                new[] { runPath },
                "validation",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                settings);
            SemanticReuseService.RecordSuggestionOutcome(
                repoRoot,
                new[] { ToReferenceCase(failureDocument, "validation_failure", "Current validation failure") },
                "validation_failure",
                "run-context",
                "validation-followup-001",
                string.Empty,
                "passed_on_retry",
                "Validation passed after retry.",
                Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-followup-001", "validation_result.json"),
                new[] { failureDocument.PrimaryArtifactPath },
                "validation",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                settings);

            service.RefreshLocalIndex(settings);
            var summary = SemanticReuseService.LoadEffectivenessSummary(repoRoot);

            Assert.Equal(2, summary.RecentEvidence.Count);
            Assert.Contains(summary.Contexts, context => context.ContextKind == "planning" && context.CleanValidationPassCount == 1);
            Assert.Contains(summary.Contexts, context => context.ContextKind == "validation_failure" && context.PassedOnRetryCount == 1);
            Assert.Contains(summary.RecentEvidence, entry =>
                entry.ContextKind == "planning" &&
                entry.SuggestionType == "generated_output_pattern" &&
                entry.CaseReference == generatedDocument.Title &&
                entry.LinkedArtifactPaths!.Contains(runPath, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void Playbooks_require_repeated_success_evidence_before_they_are_generated()
    {
        var repoRoot = CreateRepoRoot();
        try
        {
            WriteFailedValidationResult(repoRoot, "run-playbook", "Running UI tests", "Tests failed.", "Shoots.Runtime.Tests.RouteGateTests.TryAdvance_completes_happy_path");
            var settings = CreateSemanticReuseSettings();
            var service = new SemanticReuseService(repoRoot);
            var index = service.RefreshLocalIndex(settings);
            var validationDocument = Assert.Single(index.Entries, entry => entry.CaseType == "validation_failure_record");
            var reference = ToReferenceCase(validationDocument, "validation_failure", "Current validation failure");

            SemanticReuseService.RecordSuggestionOutcome(
                repoRoot,
                new[] { reference },
                "validation_failure",
                "run-playbook",
                "validation-playbook-001",
                string.Empty,
                "passed",
                "Validation passed cleanly.",
                Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-playbook-001", "validation_result.json"),
                new[] { validationDocument.PrimaryArtifactPath },
                "validation",
                DateTimeOffset.UtcNow.AddMinutes(-3),
                settings);
            service.RefreshLocalIndex(settings);
            Assert.Empty(SemanticReuseService.LoadPlaybookCatalog(repoRoot).Entries);

            SemanticReuseService.RecordSuggestionOutcome(
                repoRoot,
                new[] { reference },
                "validation_failure",
                "run-playbook",
                "validation-playbook-002",
                string.Empty,
                "passed",
                "Validation passed cleanly.",
                Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-playbook-002", "validation_result.json"),
                new[] { validationDocument.PrimaryArtifactPath },
                "validation",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                settings);
            service.RefreshLocalIndex(settings);
            var tentative = Assert.Single(SemanticReuseService.LoadPlaybookCatalog(repoRoot).Entries);
            Assert.Equal("tentative", tentative.Confidence);
            Assert.Equal("validation_failure", tentative.ContextKind);

            SemanticReuseService.RecordSuggestionOutcome(
                repoRoot,
                new[] { reference },
                "validation_failure",
                "run-playbook",
                "validation-playbook-003",
                string.Empty,
                "passed_on_retry",
                "Validation passed after retry.",
                Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "validation-playbook-003", "validation_result.json"),
                new[] { validationDocument.PrimaryArtifactPath },
                "validation",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                settings);
            service.RefreshLocalIndex(settings);
            var corroborated = Assert.Single(SemanticReuseService.LoadPlaybookCatalog(repoRoot).Entries);
            Assert.Equal("corroborated", corroborated.Confidence);
            Assert.Contains("clean pass", corroborated.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    private static string CreateRepoRoot()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"shoots-semantic-reuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "Shoots.sln"), "Microsoft Visual Studio Solution File");
        return repoRoot;
    }

    private static void WriteFailedValidationResult(string repoRoot, string runId, string stageLabel, string failureText, string failingTestName)
    {
        var outputFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "runs", runId);
        Directory.CreateDirectory(outputFolder);
        var result = new ValidationRunResult(
            runId,
            "Run full validation loop",
            outputFolder,
            false,
            $"Validation failed: {failureText}",
            failureText,
            Path.Combine(outputFolder, "01.log"),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            new[]
            {
                new ValidationStageResult("stage-001", stageLabel, "failed", failureText, Path.Combine(outputFolder, "01.log"), 1, 42)
            },
            "failed",
            "Failed",
            new ValidationFirstFailure("stage-001", stageLabel, "Shoots.Runtime.Tests.dll", failingTestName, failureText, Path.Combine(outputFolder, "01.log"), failureText, 1),
            Array.Empty<ValidationRetryAudit>(),
            Path.Combine(outputFolder, "validation_stability.json"));
        File.WriteAllText(Path.Combine(outputFolder, "validation_result.json"), JsonSerializer.Serialize(result, JsonOptions()));
        File.WriteAllText(Path.Combine(outputFolder, "validation_stability.json"), "{}");
    }

    private static string WriteGeneratedOutputLink(string repoRoot, string runId, string projectName, string projectDescription, string validationStatus, string validationSummary)
    {
        var projectRoot = Path.Combine(repoRoot, ".state", "projects", runId);
        var runPath = Path.Combine(projectRoot, "runs", runId);
        Directory.CreateDirectory(runPath);
        File.WriteAllText(
            Path.Combine(projectRoot, "project.json"),
            JsonSerializer.Serialize(new
            {
                Id = runId,
                Name = projectName,
                Description = projectDescription,
                ProjectRootPath = projectRoot
            }, JsonOptions()));
        GeneratedOutputValidationLinkService.Save(new GeneratedOutputValidationLink(
            runId,
            runPath,
            projectRoot,
            validationStatus,
            validationSummary,
            "Validate generated output",
            $"{runId}-validation",
            Path.Combine(repoRoot, ".codex", "validation-ui", "runs", $"{runId}-validation"),
            validationStatus == "failed" ? "Tests failed." : null,
            DateTimeOffset.UtcNow));
        return runPath;
    }

    private static ValidationSettings CreateSemanticReuseSettings(bool onlyPositiveCases = false)
        => new(false, false, 5, false, false, false, 20, 5, false, 5, false, true, true, 5, 200, true, onlyPositiveCases, true, true, true, 2, true, 3);

    private static void WriteRepairComparison(string repoRoot, string repairId, string sourceStage, string excerpt, string improvementState)
    {
        var repairFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", repairId);
        Directory.CreateDirectory(repairFolder);
        File.WriteAllText(Path.Combine(repairFolder, "repair_bundle.json"), "{}");
        var comparison = new RepairComparisonRecord(
            repairId,
            "run-001",
            "failed",
            "Validation failed.",
            sourceStage,
            excerpt,
            "run-002",
            "failed",
            "Validation improved.",
            "Running smoke validation",
            "Smoke still failed.",
            improvementState,
            new[] { Path.Combine(repoRoot, "src", "Generated.cs") },
            "Repair captured failure context.",
            Path.Combine(repairFolder, "repair_bundle.json"),
            repairFolder,
            Path.Combine(repoRoot, ".codex", "validation-ui", "runs", "run-002"),
            DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(repairFolder, RepairReviewArtifactsService.ComparisonFileName), JsonSerializer.Serialize(comparison, JsonOptions()));
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

    private static RepairReferenceCase ToReferenceCase(SemanticReuseIndexedCase entry, string contextKind, string contextLabel)
        => new(
            entry.DocumentId,
            contextKind,
            contextLabel,
            entry.CaseType,
            entry.Title,
            entry.Outcome,
            "High",
            "same deterministic context",
            entry.SourceRunId,
            entry.PrimaryArtifactPath,
            new[] { entry.PrimaryArtifactPath },
            string.Empty);

    private sealed class ThrowingVectorStore : ISemanticReuseVectorStore
    {
        public Task UpsertAsync(string repoKey, IReadOnlyList<SemanticReuseVectorPoint> points, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Qdrant is unavailable.");

        public Task<IReadOnlyList<SemanticReuseVectorMatch>> SearchAsync(string repoKey, IReadOnlyList<float> vector, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SemanticReuseVectorMatch>>(Array.Empty<SemanticReuseVectorMatch>());
    }
}
