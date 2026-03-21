using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderRecoveryComparisonSetRow> _builderRecoveryComparisonSets = new();
    private readonly ObservableCollection<BuilderRecoveryComparisonScenarioRow> _builderRecoverySelectedComparisonScenarios = new();
    private readonly ObservableCollection<BuilderRecoveryComparisonTradeoffRecord> _builderRecoverySelectedComparisonTradeoffs = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderRecoverySelectedComparisonArtifactLinks = new();
    private BuilderRecoveryComparisonSetRow[] _builderRecoveryAllComparisonSets = Array.Empty<BuilderRecoveryComparisonSetRow>();
    private BuilderRecoveryComparisonSetRow? _selectedBuilderRecoveryComparisonSet;
    private string _builderRecoveryComparisonSummary = "No recovery comparisons recorded.";
    private string _builderRecoveryComparisonAdvisoryBanner = "Recovery comparisons are advisory only. They compare deterministic scenarios without executing routes, changing approvals, or mutating workspace state.";
    private string _builderRecoveryComparisonArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderRecoveryComparisonSetRow> BuilderRecoveryComparisonSets { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryComparisonScenarioRow> BuilderRecoverySelectedComparisonScenarios { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryComparisonTradeoffRecord> BuilderRecoverySelectedComparisonTradeoffs { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderRecoverySelectedComparisonArtifactLinks { get; private set; } = null!;

    public bool HasBuilderRecoveryComparisonSets => _builderRecoveryComparisonSets.Count > 0;
    public string BuilderRecoveryComparisonSummary => _builderRecoveryComparisonSummary;
    public string BuilderRecoveryComparisonAdvisoryBanner => _builderRecoveryComparisonAdvisoryBanner;
    public string BuilderRecoveryComparisonArtifactPath => _builderRecoveryComparisonArtifactPath;
    public bool HasBuilderRecoveryComparisonArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoveryComparisonArtifactPath) && File.Exists(_builderRecoveryComparisonArtifactPath);
    public bool HasSelectedBuilderRecoveryComparisonSet => _selectedBuilderRecoveryComparisonSet is not null;
    public string BuilderRecoverySelectedComparisonTitle => _selectedBuilderRecoveryComparisonSet?.Header ?? "No recovery comparison selected.";
    public string BuilderRecoverySelectedComparisonSummary => _selectedBuilderRecoveryComparisonSet?.Summary ?? "No recovery comparison selected.";
    public bool HasBuilderRecoverySelectedComparisonScenarios => _builderRecoverySelectedComparisonScenarios.Count > 0;
    public bool HasBuilderRecoverySelectedComparisonTradeoffs => _builderRecoverySelectedComparisonTradeoffs.Count > 0;
    public bool HasBuilderRecoverySelectedComparisonArtifactLinks => _builderRecoverySelectedComparisonArtifactLinks.Count > 0;

    public AsyncRelayCommand OpenBuilderRecoveryComparisonArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryComparisonSetRow> SelectBuilderRecoveryComparisonSetCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryComparisonScenarioRow> FocusBuilderRecoveryComparisonPlaybookCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryComparisonScenarioRow> FocusBuilderRecoveryComparisonSimulationCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderRecoveryComparisonArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderRecoveryComparisonSurface()
    {
        BuilderRecoveryComparisonSets = new ReadOnlyObservableCollection<BuilderRecoveryComparisonSetRow>(_builderRecoveryComparisonSets);
        BuilderRecoverySelectedComparisonScenarios = new ReadOnlyObservableCollection<BuilderRecoveryComparisonScenarioRow>(_builderRecoverySelectedComparisonScenarios);
        BuilderRecoverySelectedComparisonTradeoffs = new ReadOnlyObservableCollection<BuilderRecoveryComparisonTradeoffRecord>(_builderRecoverySelectedComparisonTradeoffs);
        BuilderRecoverySelectedComparisonArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderRecoverySelectedComparisonArtifactLinks);
        OpenBuilderRecoveryComparisonArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoveryComparisonArtifactPath), () => HasBuilderRecoveryComparisonArtifactPath);
        SelectBuilderRecoveryComparisonSetCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryComparisonSetRow>(SelectBuilderRecoveryComparisonSetAsync, row => row is not null);
        FocusBuilderRecoveryComparisonPlaybookCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryComparisonScenarioRow>(FocusBuilderRecoveryComparisonPlaybookAsync, row => row is not null);
        FocusBuilderRecoveryComparisonSimulationCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryComparisonScenarioRow>(FocusBuilderRecoveryComparisonSimulationAsync, row => row is not null);
        OpenBuilderRecoveryComparisonArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderRecoveryComparisonArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderRecoveryComparisonArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null)
    {
        var artifact = BuilderRecoveryComparisonService.RefreshRecoveryComparisons(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            accuracyArtifact,
            contextFilters: contextFilterArtifact);
        if (artifact is null)
        {
            ResetBuilderRecoveryComparisonState();
            return;
        }

        _builderRecoveryComparisonSummary = artifact.Summary;
        _builderRecoveryComparisonArtifactPath = artifact.ArtifactPath;
        _builderRecoveryAllComparisonSets = artifact.ComparisonSets
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

    private void ResetBuilderRecoveryComparisonState()
    {
        _builderRecoveryComparisonSummary = "No recovery comparisons recorded.";
        _builderRecoveryComparisonArtifactPath = string.Empty;
        _builderRecoveryAllComparisonSets = Array.Empty<BuilderRecoveryComparisonSetRow>();
        _builderRecoveryComparisonSets.Clear();
        ApplySelectedBuilderRecoveryComparisonSet(null);
        NotifyBuilderRecoveryComparisonStateChanged();
    }

    private void ApplyBuilderRecoveryComparisonSelection()
    {
        var filtered = _builderRecoveryAllComparisonSets
            .Select(row => row with
            {
                VisibleScenarioCount = row.Set.ComparisonMetrics.Count(metric => _showBuilderRecoveryViolatingOptions || !string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)),
                BlockedScenarioCount = row.Set.ComparisonMetrics.Count(metric => string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase))
            })
            .Where(row => row.VisibleScenarioCount > 0)
            .OrderBy(row => row.Set.BranchId == "all_candidates" ? 0 : 1)
            .ThenBy(row => row.Set.BranchId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Set.ComparisonId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _builderRecoveryComparisonSets.Clear();
        foreach (var row in filtered)
        {
            _builderRecoveryComparisonSets.Add(row);
        }

        var selectedComparisonId = _selectedBuilderRecoveryComparisonSet?.ComparisonId;
        var nextSelected = filtered.FirstOrDefault(row => string.Equals(row.ComparisonId, selectedComparisonId, StringComparison.OrdinalIgnoreCase));
        if (nextSelected is null && _selectedBuilderRecoveryPlaybook is not null)
        {
            nextSelected = filtered.FirstOrDefault(row =>
                row.Set.PlaybookIds.Contains(_selectedBuilderRecoveryPlaybook.PlaybookId, StringComparer.OrdinalIgnoreCase));
        }

        ApplySelectedBuilderRecoveryComparisonSet(nextSelected ?? filtered.FirstOrDefault());
        ApplyBuilderDecisionJustificationSelection();
        NotifyBuilderRecoveryComparisonStateChanged();
    }

    private void ApplySelectedBuilderRecoveryComparisonSet(BuilderRecoveryComparisonSetRow? row)
    {
        _selectedBuilderRecoveryComparisonSet = row;
        _builderRecoverySelectedComparisonScenarios.Clear();
        _builderRecoverySelectedComparisonTradeoffs.Clear();
        _builderRecoverySelectedComparisonArtifactLinks.Clear();
        if (row is not null)
        {
            foreach (var scenario in row.Set.ComparisonMetrics
                         .Where(metric => _showBuilderRecoveryViolatingOptions || !string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(metric => string.Equals(metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                         .ThenByDescending(metric => metric.ComparisonScore)
                         .ThenBy(metric => metric.PlaybookTitle, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(metric => metric.SimulationId, StringComparer.OrdinalIgnoreCase)
                         .Select(metric => new BuilderRecoveryComparisonScenarioRow(
                             metric,
                             BuildBuilderPreventativeGuardrailPresentation(
                                 playbookId: metric.PlaybookId,
                                 simulationId: metric.SimulationId),
                             BuildBuilderAutoSuggestionPresentation(
                                 playbookId: metric.PlaybookId,
                                 simulationId: metric.SimulationId),
                             BuildBuilderTrustPresentation(
                                 playbookId: metric.PlaybookId,
                                 simulationId: metric.SimulationId),
                             BuildBuilderPredictiveDriftPresentation(simulationId: metric.SimulationId))))
            {
                _builderRecoverySelectedComparisonScenarios.Add(scenario);
            }

            foreach (var tradeoff in row.Set.Tradeoffs
                         .OrderBy(tradeoff => tradeoff.Dimension, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(tradeoff => tradeoff.TradeoffId, StringComparer.OrdinalIgnoreCase))
            {
                _builderRecoverySelectedComparisonTradeoffs.Add(tradeoff);
            }

            foreach (var artifactLink in row.Set.ComparisonMetrics
                         .SelectMany(metric => metric.EvidenceLinks)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path)))
            {
                _builderRecoverySelectedComparisonArtifactLinks.Add(artifactLink);
            }
        }

        SyncBuilderPredictiveDriftSelection();
    }

    private Task SelectBuilderRecoveryComparisonSetAsync(BuilderRecoveryComparisonSetRow? row)
    {
        _builderDecisionJustificationPreferredTargetType = "comparison";
        ApplySelectedBuilderRecoveryComparisonSet(row);
        SyncBuilderPreventativeGuardrailSelection();
        SyncBuilderPredictiveDriftSelection();
        LoadBuilderExecutionReadinessArtifacts();
        NotifyBuilderRecoveryComparisonStateChanged();
        NotifyBuilderPredictiveDriftStateChanged();
        NotifyBuilderDecisionJustificationStateChanged();
        return Task.CompletedTask;
    }

    private Task FocusBuilderRecoveryComparisonPlaybookAsync(BuilderRecoveryComparisonScenarioRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        var playbook = _builderRecoveryAllPlaybooks.FirstOrDefault(entry =>
            string.Equals(entry.PlaybookId, row.PlaybookId, StringComparison.OrdinalIgnoreCase));
        _builderDecisionJustificationPreferredTargetType = "playbook";
        ApplySelectedBuilderRecoveryPlaybook(playbook);
        SyncBuilderPreventativeGuardrailSelection();
        SyncBuilderPredictiveDriftSelection();
        LoadBuilderExecutionReadinessArtifacts();
        NotifyBuilderRecoveryStateChanged();
        NotifyBuilderRecoveryComparisonStateChanged();
        NotifyBuilderPredictiveDriftStateChanged();
        NotifyBuilderDecisionJustificationStateChanged();
        return Task.CompletedTask;
    }

    private Task FocusBuilderRecoveryComparisonSimulationAsync(BuilderRecoveryComparisonScenarioRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        var playbook = _builderRecoveryAllPlaybooks.FirstOrDefault(entry =>
            string.Equals(entry.PlaybookId, row.PlaybookId, StringComparison.OrdinalIgnoreCase));
        _builderDecisionJustificationPreferredTargetType = "simulation";
        ApplySelectedBuilderRecoveryPlaybook(playbook);
        var simulation = _builderRecoveryAllSimulations.FirstOrDefault(entry =>
            string.Equals(entry.SimulationId, row.SimulationId, StringComparison.OrdinalIgnoreCase));
        ApplySelectedBuilderRecoverySimulation(simulation);
        SyncBuilderPreventativeGuardrailSelection();
        SyncBuilderPredictiveDriftSelection();
        LoadBuilderExecutionReadinessArtifacts();
        NotifyBuilderRecoveryStateChanged();
        NotifyBuilderRecoverySimulationStateChanged();
        NotifyBuilderRecoveryComparisonStateChanged();
        NotifyBuilderPredictiveDriftStateChanged();
        NotifyBuilderDecisionJustificationStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderRecoveryComparisonArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderRecoveryComparisonStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderRecoveryComparisonSets));
        OnPropertyChanged(nameof(BuilderRecoveryComparisonSummary));
        OnPropertyChanged(nameof(BuilderRecoveryComparisonAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderRecoveryComparisonArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoveryComparisonArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderRecoveryComparisonSet));
        OnPropertyChanged(nameof(BuilderRecoverySelectedComparisonTitle));
        OnPropertyChanged(nameof(BuilderRecoverySelectedComparisonSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedComparisonScenarios));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedComparisonTradeoffs));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedComparisonArtifactLinks));
        OpenBuilderRecoveryComparisonArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderRecoveryComparisonSetRow(
    BuilderRecoveryComparisonSetRecord Set,
    int VisibleScenarioCount,
    int BlockedScenarioCount,
    int EscalatedScenarioCount,
    int CriticalScenarioCount,
    BuilderAutoSuggestionPresentation Suggestion,
    BuilderTrustPresentation Trust,
    BuilderPredictiveDriftPresentation PredictiveDrift)
{
    public string ComparisonId => Set.ComparisonId;
    public string Header => $"{Set.BranchLabel} [{VisibleScenarioCount}]";
    public string Summary => $"{Set.Summary} Blocked scenarios: {BlockedScenarioCount}. Escalated scenarios: {EscalatedScenarioCount}. Critical: {CriticalScenarioCount}.";
    public bool HasSuggestedRecommendation => Suggestion.HasSuggestion;
    public bool IsPrimarySuggestedRecommendation => Suggestion.IsPrimarySuggestion;
    public string SuggestedRecommendationBadge => Suggestion.Badge;
    public string SuggestedRecommendationSummary => Suggestion.Summary;
    public bool HasTrustProfile => Trust.HasProfile;
    public string TrustSummary => Trust.Summary;
    public bool HasPredictedRisk => PredictiveDrift.HasPrediction;
    public string PredictedRiskSummary => PredictiveDrift.Summary;
}

public sealed record BuilderRecoveryComparisonScenarioRow(
    BuilderRecoveryComparisonMetricRecord Metric,
    BuilderPreventativeGuardrailPresentation Guardrail,
    BuilderAutoSuggestionPresentation Suggestion,
    BuilderTrustPresentation Trust,
    BuilderPredictiveDriftPresentation PredictiveDrift)
{
    public string MetricId => Metric.MetricId;
    public string PlaybookId => Metric.PlaybookId;
    public string SimulationId => Metric.SimulationId;
    public string Header => $"{Metric.PlaybookTitle} / {FormatToken(Metric.Scenario)}";
    public string ScoreSummary => Metric.ScoreSummary;
    public string RiskSummary => Metric.RiskSummary;
    public string BlockingSummary => $"Expected blocking gate: {FormatToken(Metric.ExpectedBlockingGate)}. Confidence: {FormatToken(Metric.ConfidenceBand)}.";
    public string ConstraintSummary => string.Equals(Metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase)
        ? $"Constraint blocked. {Metric.RiskSummary}"
        : $"Constraint compatible. {Metric.RiskSummary}";
    public string SignalBalanceSummary => Metric.SignalBalanceSummary;
    public IReadOnlyList<string> SignalContributionSummaries => Metric.SignalContributions.Select(entry => entry.Summary).ToArray();
    public bool IsBlockedByConstraints => string.Equals(Metric.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase);
    public bool HasRiskEscalation => Guardrail.HasEscalation;
    public bool HasCriticalRiskEscalation => Guardrail.HasCriticalEscalation;
    public string GuardrailBadge => Guardrail.Badge;
    public string GuardrailSummary => Guardrail.Summary;
    public string GuardrailReason => Guardrail.Reason;
    public bool HasSuggestedRecommendation => Suggestion.HasSuggestion;
    public bool IsPrimarySuggestedRecommendation => Suggestion.IsPrimarySuggestion;
    public string SuggestedRecommendationBadge => Suggestion.Badge;
    public string SuggestedRecommendationSummary => Suggestion.Summary;
    public bool HasTrustProfile => Trust.HasProfile;
    public string TrustBadge => Trust.Badge;
    public string TrustSummary => Trust.Summary;
    public bool HasPredictedRisk => PredictiveDrift.HasPrediction;
    public string PredictedRiskBadge => PredictiveDrift.Badge;
    public string PredictedRiskSummary => PredictiveDrift.Summary;

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}
