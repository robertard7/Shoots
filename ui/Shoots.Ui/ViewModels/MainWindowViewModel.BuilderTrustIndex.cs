using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderTrustMetricRow> _builderTrustMetrics = new();
    private bool _isApplyingBuilderTrustOverlay;
    private BuilderTrustIndexRecord? _builderTrustIndexArtifact;
    private string _builderTrustIndexSummary = "No trust index recorded.";
    private string _builderTrustIndexProfileSummary = "No trust profile recorded.";
    private string _builderTrustIndexOperatorAlignmentSummary = "No operator alignment score recorded.";
    private string _builderTrustIndexArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderTrustMetricRow> BuilderTrustMetrics { get; private set; } = null!;
    public bool HasBuilderTrustIndex => !string.IsNullOrWhiteSpace(_builderTrustIndexSummary) &&
                                        !string.Equals(_builderTrustIndexSummary, "No trust index recorded.", StringComparison.Ordinal);
    public bool HasBuilderTrustMetrics => _builderTrustMetrics.Count > 0;
    public string BuilderTrustIndexSummary => _builderTrustIndexSummary;
    public string BuilderTrustIndexProfileSummary => _builderTrustIndexProfileSummary;
    public bool HasBuilderTrustIndexProfileSummary => !string.IsNullOrWhiteSpace(_builderTrustIndexProfileSummary) &&
                                                      !string.Equals(_builderTrustIndexProfileSummary, "No trust profile recorded.", StringComparison.Ordinal);
    public string BuilderTrustIndexOperatorAlignmentSummary => _builderTrustIndexOperatorAlignmentSummary;
    public bool HasBuilderTrustIndexOperatorAlignmentSummary => !string.IsNullOrWhiteSpace(_builderTrustIndexOperatorAlignmentSummary) &&
                                                                !string.Equals(_builderTrustIndexOperatorAlignmentSummary, "No operator alignment score recorded.", StringComparison.Ordinal);
    public string BuilderTrustIndexArtifactPath => _builderTrustIndexArtifactPath;
    public bool HasBuilderTrustIndexArtifactPath => !string.IsNullOrWhiteSpace(_builderTrustIndexArtifactPath) && File.Exists(_builderTrustIndexArtifactPath);

    public AsyncRelayCommand OpenBuilderTrustIndexArtifactCommand { get; private set; } = null!;

    private void InitializeBuilderTrustIndexSurface()
    {
        BuilderTrustMetrics = new ReadOnlyObservableCollection<BuilderTrustMetricRow>(_builderTrustMetrics);
        OpenBuilderTrustIndexArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderTrustIndexArtifactPath), () => HasBuilderTrustIndexArtifactPath);
    }

    private BuilderTrustIndexRecord? LoadBuilderTrustIndexArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null,
        BuilderOperatorDecisionsRecord? decisionsArtifact = null,
        BuilderAutoSuggestionsRecord? autoSuggestionArtifact = null,
        BuilderExecutionReadinessRecord? readinessArtifact = null,
        BuilderPreventativeGuardrailsReport? guardrailArtifact = null,
        BuilderExecutionAuditReport? auditArtifact = null)
    {
        var artifact = BuilderTrustIndexService.RefreshTrustIndex(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact,
            simulationArtifact,
            accuracyArtifact,
            decisionsArtifact,
            autoSuggestionArtifact,
            readinessArtifact,
            guardrailArtifact,
            auditArtifact);
        _builderTrustIndexArtifact = artifact;
        if (artifact is null)
        {
            ResetBuilderTrustIndexState();
            return null;
        }

        _builderTrustIndexSummary = artifact.Summary;
        _builderTrustIndexProfileSummary = $"Trust score {artifact.TrustScore:0.##}. Profile: {BuilderTrustPresentation.FormatProfile(artifact.ConfidenceProfile)}.";
        _builderTrustIndexOperatorAlignmentSummary = $"Operator alignment {artifact.OperatorAlignmentScore:0.##}. Current workspace guidance remains advisory only.";
        _builderTrustIndexArtifactPath = artifact.ArtifactPath;

        _builderTrustMetrics.Clear();
        foreach (var row in artifact.Metrics
                     .OrderBy(metric => metric.MetricId, StringComparer.OrdinalIgnoreCase)
                     .Select(metric => new BuilderTrustMetricRow(metric)))
        {
            _builderTrustMetrics.Add(row);
        }

        ApplyBuilderTrustOverlays(playbookArtifact, simulationArtifact, accuracyArtifact);
        NotifyBuilderTrustIndexStateChanged();
        return artifact;
    }

    private void ApplyBuilderTrustOverlays(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null)
    {
        _isApplyingBuilderTrustOverlay = true;
        try
        {
            playbookArtifact ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(GetBuilderWorkspaceRepoRoot());
            simulationArtifact ??= BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot());
            var rankingArtifact = BuilderPlaybookRankingService.LoadPlaybookRankings(GetBuilderWorkspaceRepoRoot());
            var contextFilterArtifact = BuilderPlaybookContextFilterService.LoadContextFilters(GetBuilderWorkspaceRepoRoot());
            var comparisonArtifact = BuilderRecoveryComparisonService.LoadRecoveryComparisons(GetBuilderWorkspaceRepoRoot());
            accuracyArtifact ??= BuilderSimulationAccuracyService.LoadSimulationAccuracy(GetBuilderWorkspaceRepoRoot());

            if (playbookArtifact is not null)
            {
                var rankingIndex = (rankingArtifact?.Rankings ?? Array.Empty<BuilderPlaybookRankingRecord>())
                    .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var contextFilterIndex = (contextFilterArtifact?.RelevanceScores ?? Array.Empty<BuilderPlaybookContextFilterEntryRecord>())
                    .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                _builderRecoveryAllPlaybooks = OrderBuilderRecoveryRows(
                        playbookArtifact.Playbooks
                            .Select(playbook => new BuilderRecoveryPlaybookRow(
                                playbook,
                                rankingIndex.TryGetValue(playbook.PlaybookId, out var ranking) ? ranking : null,
                                contextFilterIndex.TryGetValue(playbook.PlaybookId, out var contextFilter) ? contextFilter : null,
                                BuildBuilderPreventativeGuardrailPresentation(
                                    playbookId: playbook.PlaybookId,
                                    route: playbook.AppliesToRoutes.FirstOrDefault() ?? string.Empty),
                                BuildBuilderAutoSuggestionPresentation(playbookId: playbook.PlaybookId),
                                BuildBuilderTrustPresentation(playbookId: playbook.PlaybookId),
                                BuildBuilderPredictiveDriftPresentation(playbookId: playbook.PlaybookId)))
                            .ToArray())
                    .ToArray();
                PopulateBuilderRecoveryFilterOptions(_builderRecoveryAllPlaybooks);
                ApplyBuilderRecoveryFilters();
            }

            if (simulationArtifact is not null)
            {
                var titlesByPlaybookId = (playbookArtifact?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>())
                    .ToDictionary(playbook => playbook.PlaybookId, playbook => playbook.Title, StringComparer.OrdinalIgnoreCase);
                _builderRecoveryAllSimulations = simulationArtifact.Simulations
                    .Select(simulation => new BuilderRecoverySimulationRow(
                        simulation,
                        titlesByPlaybookId.TryGetValue(simulation.PlaybookId, out var title) ? title : "Recovery playbook",
                        BuildAccuracyPresentation(simulation, accuracyArtifact),
                        BuildBuilderPreventativeGuardrailPresentation(
                            playbookId: simulation.PlaybookId,
                            simulationId: simulation.SimulationId,
                            route: simulation.TargetRoute),
                        BuildBuilderAutoSuggestionPresentation(
                            playbookId: simulation.PlaybookId,
                            simulationId: simulation.SimulationId),
                        BuildBuilderTrustPresentation(
                            playbookId: simulation.PlaybookId,
                            simulationId: simulation.SimulationId),
                        BuildBuilderPredictiveDriftPresentation(simulationId: simulation.SimulationId)))
                    .ToArray();
                ApplyBuilderRecoverySimulationSelection();
            }

            if (comparisonArtifact is not null)
            {
                _builderRecoveryAllComparisonSets = comparisonArtifact.ComparisonSets
                    .Select(set => new BuilderRecoveryComparisonSetRow(
                        set,
                        set.ComparisonMetrics.Count(metric => !_showBuilderRecoveryViolatingOptions || !string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)),
                        set.ComparisonMetrics.Count(metric => string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)),
                        set.ComparisonMetrics.Count(metric => BuildBuilderPreventativeGuardrailPresentation(
                            playbookId: metric.PlaybookId,
                            simulationId: metric.SimulationId).HasEscalation),
                        set.ComparisonMetrics.Count(metric => BuildBuilderPreventativeGuardrailPresentation(
                            playbookId: metric.PlaybookId,
                            simulationId: metric.SimulationId).HasCriticalEscalation),
                        BuildBuilderAutoSuggestionPresentation(comparisonSet: set),
                        BuildBuilderTrustPresentation(comparisonSet: set),
                        BuildBuilderPredictiveDriftPresentation(comparisonSet: set)))
                    .ToArray();
                ApplyBuilderRecoveryComparisonSelection();
            }
        }
        finally
        {
            _isApplyingBuilderTrustOverlay = false;
        }
    }

    private BuilderTrustPresentation BuildBuilderTrustPresentation(
        string playbookId = "",
        string simulationId = "",
        BuilderRecoveryComparisonSetRecord? comparisonSet = null)
    {
        if (_builderTrustIndexArtifact is null || _builderTrustIndexArtifact.TargetProfiles.Count == 0)
        {
            return BuilderTrustPresentation.Empty;
        }

        var matches = _builderTrustIndexArtifact.TargetProfiles
            .Where(entry =>
                !string.IsNullOrWhiteSpace(simulationId) &&
                string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, simulationId, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(playbookId) &&
                string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetId, playbookId, StringComparison.OrdinalIgnoreCase) ||
                comparisonSet is not null &&
                (string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                 comparisonSet.PlaybookIds.Contains(entry.TargetId, StringComparer.OrdinalIgnoreCase) ||
                 string.Equals(entry.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                 comparisonSet.SimulationIds.Contains(entry.TargetId, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.TrustScore)
            .ThenBy(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return BuilderTrustPresentation.FromProfiles(matches);
    }

    private void ResetBuilderTrustIndexState()
    {
        _builderTrustIndexArtifact = null;
        _builderTrustIndexSummary = "No trust index recorded.";
        _builderTrustIndexProfileSummary = "No trust profile recorded.";
        _builderTrustIndexOperatorAlignmentSummary = "No operator alignment score recorded.";
        _builderTrustIndexArtifactPath = string.Empty;
        _builderTrustMetrics.Clear();
        NotifyBuilderTrustIndexStateChanged();
    }

    private void NotifyBuilderTrustIndexStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderTrustIndex));
        OnPropertyChanged(nameof(HasBuilderTrustMetrics));
        OnPropertyChanged(nameof(BuilderTrustIndexSummary));
        OnPropertyChanged(nameof(BuilderTrustIndexProfileSummary));
        OnPropertyChanged(nameof(HasBuilderTrustIndexProfileSummary));
        OnPropertyChanged(nameof(BuilderTrustIndexOperatorAlignmentSummary));
        OnPropertyChanged(nameof(HasBuilderTrustIndexOperatorAlignmentSummary));
        OnPropertyChanged(nameof(BuilderTrustIndexArtifactPath));
        OnPropertyChanged(nameof(HasBuilderTrustIndexArtifactPath));
        OpenBuilderTrustIndexArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderTrustMetricRow(BuilderTrustMetricRecord Metric)
{
    public string Header => Metric.DisplayName;
    public string ScoreSummary => $"Score {Metric.Score:0.##} across {Metric.SampleSize} evidence sample(s).";
    public string Summary => Metric.Summary;
}

public sealed record BuilderTrustPresentation(IReadOnlyList<BuilderTrustTargetProfileRecord> Matches)
{
    public static BuilderTrustPresentation Empty { get; } = new(Array.Empty<BuilderTrustTargetProfileRecord>());

    public BuilderTrustTargetProfileRecord? Primary => Matches
        .OrderByDescending(entry => entry.TrustScore)
        .ThenBy(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    public bool HasProfile => Primary is not null;
    public string Badge => Primary is null ? string.Empty : $"Trust: {FormatProfile(Primary.ConfidenceProfile)}";
    public string Summary => Primary is null
        ? string.Empty
        : $"{Badge}. Score {Primary.TrustScore:0.##}. Operator alignment {Primary.OperatorAlignmentScore:0.##}.";
    public string Reason => Primary?.Summary ?? string.Empty;

    public static BuilderTrustPresentation FromProfiles(IReadOnlyList<BuilderTrustTargetProfileRecord> profiles)
        => profiles.Count == 0 ? Empty : new BuilderTrustPresentation(profiles);

    public static string FormatProfile(string value)
        => value switch
        {
            "high_trust" => "High Trust",
            "moderate_trust" => "Moderate Trust",
            "low_trust" => "Low Trust",
            _ => "Unstable"
        };
}
