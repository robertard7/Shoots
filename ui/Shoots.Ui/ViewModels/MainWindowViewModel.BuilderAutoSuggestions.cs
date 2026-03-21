using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderAutoSuggestionRow> _builderAutoSuggestionRows = new();
    private bool _isApplyingBuilderAutoSuggestionOverlay;
    private BuilderAutoSuggestionsRecord? _builderAutoSuggestionArtifact;
    private string _builderAutoSuggestionSummary = "No auto suggestions recorded.";
    private string _builderAutoSuggestionPrimarySummary = "No primary recommendation recorded.";
    private string _builderAutoSuggestionAlternateSummary = "No alternate recommendation recorded.";
    private string _builderAutoSuggestionDivergenceSummary = "No operator divergence from recommendations recorded.";
    private string _builderAutoSuggestionArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderAutoSuggestionRow> BuilderAutoSuggestions { get; private set; } = null!;
    public bool HasBuilderAutoSuggestions => _builderAutoSuggestionRows.Count > 0;
    public string BuilderAutoSuggestionSummary => _builderAutoSuggestionSummary;
    public string BuilderAutoSuggestionPrimarySummary => _builderAutoSuggestionPrimarySummary;
    public bool HasBuilderAutoSuggestionPrimarySummary => !string.IsNullOrWhiteSpace(_builderAutoSuggestionPrimarySummary) &&
                                                          !string.Equals(_builderAutoSuggestionPrimarySummary, "No primary recommendation recorded.", StringComparison.Ordinal);
    public string BuilderAutoSuggestionAlternateSummary => _builderAutoSuggestionAlternateSummary;
    public bool HasBuilderAutoSuggestionAlternateSummary => !string.IsNullOrWhiteSpace(_builderAutoSuggestionAlternateSummary) &&
                                                            !string.Equals(_builderAutoSuggestionAlternateSummary, "No alternate recommendation recorded.", StringComparison.Ordinal);
    public string BuilderAutoSuggestionDivergenceSummary => _builderAutoSuggestionDivergenceSummary;
    public bool HasBuilderAutoSuggestionDivergenceSummary => !string.IsNullOrWhiteSpace(_builderAutoSuggestionDivergenceSummary) &&
                                                             !string.Equals(_builderAutoSuggestionDivergenceSummary, "No operator divergence from recommendations recorded.", StringComparison.Ordinal);
    public string BuilderAutoSuggestionArtifactPath => _builderAutoSuggestionArtifactPath;
    public bool HasBuilderAutoSuggestionArtifactPath => !string.IsNullOrWhiteSpace(_builderAutoSuggestionArtifactPath) && File.Exists(_builderAutoSuggestionArtifactPath);

    public AsyncRelayCommand OpenBuilderAutoSuggestionArtifactCommand { get; private set; } = null!;

    private void InitializeBuilderAutoSuggestionSurface()
    {
        BuilderAutoSuggestions = new ReadOnlyObservableCollection<BuilderAutoSuggestionRow>(_builderAutoSuggestionRows);
        OpenBuilderAutoSuggestionArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderAutoSuggestionArtifactPath), () => HasBuilderAutoSuggestionArtifactPath);
    }

    private BuilderAutoSuggestionsRecord? LoadBuilderAutoSuggestionArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null,
        BuilderExecutionReadinessRecord? readinessArtifact = null,
        BuilderPreventativeGuardrailsReport? guardrailArtifact = null,
        BuilderOperatorDecisionsRecord? decisionsArtifact = null)
    {
        var artifact = BuilderAutoSuggestionService.RefreshAutoSuggestions(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            contextFilterArtifact,
            comparisonArtifact,
            accuracyArtifact,
            readinessArtifact,
            guardrailArtifact,
            decisionsArtifact);
        _builderAutoSuggestionArtifact = artifact;
        if (artifact is null)
        {
            ResetBuilderAutoSuggestionState();
            return null;
        }

        _builderAutoSuggestionSummary = artifact.Summary;
        _builderAutoSuggestionArtifactPath = artifact.ArtifactPath;
        var primary = artifact.Suggestions.FirstOrDefault(entry => string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase));
        var alternate = artifact.Suggestions.FirstOrDefault(entry => string.Equals(entry.SuggestionKind, "alternate", StringComparison.OrdinalIgnoreCase));
        _builderAutoSuggestionPrimarySummary = primary is null
            ? "No primary recommendation recorded."
            : $"{BuilderAutoSuggestionPresentation.FormatToken(primary.TargetType)} {primary.TargetId}: {primary.SelectionReason}";
        _builderAutoSuggestionAlternateSummary = alternate is null
            ? "No alternate recommendation recorded."
            : $"{BuilderAutoSuggestionPresentation.FormatToken(alternate.TargetType)} {alternate.TargetId}: {alternate.SelectionReason}";
        _builderAutoSuggestionDivergenceSummary = artifact.LatestDecisionDivergence?.Summary ?? "No operator divergence from recommendations recorded.";

        _builderAutoSuggestionRows.Clear();
        foreach (var row in artifact.Suggestions
                     .OrderBy(entry => string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenByDescending(entry => entry.SuggestionScore)
                     .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => new BuilderAutoSuggestionRow(entry)))
        {
            _builderAutoSuggestionRows.Add(row);
        }

        ApplyBuilderAutoSuggestionOverlays(
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            contextFilterArtifact,
            comparisonArtifact,
            accuracyArtifact);
        NotifyBuilderAutoSuggestionStateChanged();
        return artifact;
    }

    private void ApplyBuilderAutoSuggestionOverlays(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null)
    {
        _isApplyingBuilderAutoSuggestionOverlay = true;
        try
        {
            playbookArtifact ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(GetBuilderWorkspaceRepoRoot());
            simulationArtifact ??= BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot());
            rankingArtifact ??= BuilderPlaybookRankingService.LoadPlaybookRankings(GetBuilderWorkspaceRepoRoot());
            contextFilterArtifact ??= BuilderPlaybookContextFilterService.LoadContextFilters(GetBuilderWorkspaceRepoRoot());
            comparisonArtifact ??= BuilderRecoveryComparisonService.LoadRecoveryComparisons(GetBuilderWorkspaceRepoRoot());
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
            _isApplyingBuilderAutoSuggestionOverlay = false;
        }
    }

    private BuilderAutoSuggestionPresentation BuildBuilderAutoSuggestionPresentation(
        string playbookId = "",
        string simulationId = "",
        BuilderRecoveryComparisonSetRecord? comparisonSet = null)
    {
        if (_builderAutoSuggestionArtifact is null || _builderAutoSuggestionArtifact.Suggestions.Count == 0)
        {
            return BuilderAutoSuggestionPresentation.Empty;
        }

        var matches = _builderAutoSuggestionArtifact.Suggestions
            .Where(entry =>
                !string.IsNullOrWhiteSpace(simulationId) &&
                string.Equals(entry.SimulationId, simulationId, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(playbookId) &&
                string.Equals(entry.PlaybookId, playbookId, StringComparison.OrdinalIgnoreCase) ||
                comparisonSet is not null &&
                (comparisonSet.PlaybookIds.Contains(entry.PlaybookId, StringComparer.OrdinalIgnoreCase) ||
                 comparisonSet.SimulationIds.Contains(entry.SimulationId, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(entry => string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(entry => entry.SuggestionScore)
            .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return BuilderAutoSuggestionPresentation.FromSuggestions(matches);
    }

    private void ResetBuilderAutoSuggestionState()
    {
        _builderAutoSuggestionArtifact = null;
        _builderAutoSuggestionSummary = "No auto suggestions recorded.";
        _builderAutoSuggestionPrimarySummary = "No primary recommendation recorded.";
        _builderAutoSuggestionAlternateSummary = "No alternate recommendation recorded.";
        _builderAutoSuggestionDivergenceSummary = "No operator divergence from recommendations recorded.";
        _builderAutoSuggestionArtifactPath = string.Empty;
        _builderAutoSuggestionRows.Clear();
        NotifyBuilderAutoSuggestionStateChanged();
    }

    private void NotifyBuilderAutoSuggestionStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderAutoSuggestions));
        OnPropertyChanged(nameof(BuilderAutoSuggestionSummary));
        OnPropertyChanged(nameof(BuilderAutoSuggestionPrimarySummary));
        OnPropertyChanged(nameof(HasBuilderAutoSuggestionPrimarySummary));
        OnPropertyChanged(nameof(BuilderAutoSuggestionAlternateSummary));
        OnPropertyChanged(nameof(HasBuilderAutoSuggestionAlternateSummary));
        OnPropertyChanged(nameof(BuilderAutoSuggestionDivergenceSummary));
        OnPropertyChanged(nameof(HasBuilderAutoSuggestionDivergenceSummary));
        OnPropertyChanged(nameof(BuilderAutoSuggestionArtifactPath));
        OnPropertyChanged(nameof(HasBuilderAutoSuggestionArtifactPath));
        OpenBuilderAutoSuggestionArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderAutoSuggestionRow(BuilderAutoSuggestionRecord Suggestion)
{
    public string Header => $"{BuilderAutoSuggestionPresentation.FormatToken(Suggestion.SuggestionKind)} {BuilderAutoSuggestionPresentation.FormatToken(Suggestion.TargetType)}";
    public string Summary => $"{Suggestion.TargetId}: {Suggestion.SelectionReason}";
    public string ContextSummary => $"Tradeoff: {Suggestion.TradeoffLabel}. Confidence: {BuilderAutoSuggestionPresentation.FormatToken(Suggestion.Confidence)}. Risk: {BuilderAutoSuggestionPresentation.FormatToken(Suggestion.RiskLevel)}. Constraint: {BuilderAutoSuggestionPresentation.FormatToken(Suggestion.ConstraintStatus)}. {Suggestion.SignalBalanceSummary}";
}

public sealed record BuilderAutoSuggestionPresentation(IReadOnlyList<BuilderAutoSuggestionRecord> Matches)
{
    public static BuilderAutoSuggestionPresentation Empty { get; } = new(Array.Empty<BuilderAutoSuggestionRecord>());

    public BuilderAutoSuggestionRecord? Primary => Matches
        .OrderBy(entry => string.Equals(entry.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(entry => string.Equals(entry.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenByDescending(entry => entry.SuggestionScore)
        .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    public bool HasSuggestion => Primary is not null;
    public bool IsPrimarySuggestion => string.Equals(Primary?.SuggestionKind, "primary", StringComparison.OrdinalIgnoreCase);
    public bool IsAlternateSuggestion => string.Equals(Primary?.SuggestionKind, "alternate", StringComparison.OrdinalIgnoreCase);
    public string Badge => Primary is null ? string.Empty : IsPrimarySuggestion ? "Recommended" : "Alternate";
    public string CalibrationProfile => Primary?.CalibrationProfile ?? string.Empty;
    public string Summary => Primary is null
        ? string.Empty
        : $"{Badge}: {FormatToken(Primary.TargetType)} {Primary.TargetId}. Confidence {FormatToken(Primary.Confidence)}. Risk {FormatToken(Primary.RiskLevel)}. {Primary.TradeoffLabel}. {Primary.SignalBalanceSummary}";
    public string Reason => Primary?.SelectionReason ?? string.Empty;
    public IReadOnlyList<string> SignalContributionSummaries => Primary?.SignalContributions.Select(entry => entry.Summary).ToArray() ?? Array.Empty<string>();

    public static BuilderAutoSuggestionPresentation FromSuggestions(IReadOnlyList<BuilderAutoSuggestionRecord> suggestions)
        => suggestions.Count == 0 ? Empty : new BuilderAutoSuggestionPresentation(suggestions);

    public static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}
