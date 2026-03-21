using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderPredictiveDriftRow> _builderPredictiveDriftRows = new();
    private readonly ObservableCollection<BuilderPredictiveDriftEvidenceStepRow> _builderSelectedPredictiveDriftEvidenceSteps = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderSelectedPredictiveDriftArtifactLinks = new();
    private bool _isApplyingBuilderPredictiveDriftOverlay;
    private BuilderPredictiveDriftReport? _builderPredictiveDriftArtifact;
    private BuilderPredictiveDriftRow? _selectedBuilderPredictiveDrift;
    private string _builderPredictiveDriftSummary = "No predictive drift recorded.";
    private string _builderPredictiveDriftAdvisoryBanner = "Predictive drift is advisory only. Forecasts highlight evidence-backed failure trajectories before operator action, but they do not block routes, execute recovery, approve files, or finalize work.";
    private string _builderPredictiveDriftArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderPredictiveDriftRow> BuilderPredictiveDriftRows { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderPredictiveDriftEvidenceStepRow> BuilderSelectedPredictiveDriftEvidenceSteps { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderSelectedPredictiveDriftArtifactLinks { get; private set; } = null!;

    public bool HasBuilderPredictiveDrift => _builderPredictiveDriftRows.Count > 0;
    public string BuilderPredictiveDriftSummary => _builderPredictiveDriftSummary;
    public string BuilderPredictiveDriftAdvisoryBanner => _builderPredictiveDriftAdvisoryBanner;
    public string BuilderPredictiveDriftArtifactPath => _builderPredictiveDriftArtifactPath;
    public bool HasBuilderPredictiveDriftArtifactPath => !string.IsNullOrWhiteSpace(_builderPredictiveDriftArtifactPath) && File.Exists(_builderPredictiveDriftArtifactPath);
    public bool HasSelectedBuilderPredictiveDrift => _selectedBuilderPredictiveDrift is not null;
    public string BuilderSelectedPredictiveDriftTitle => _selectedBuilderPredictiveDrift?.Header ?? "No predictive drift signal selected.";
    public string BuilderSelectedPredictiveDriftSummary => _selectedBuilderPredictiveDrift?.Summary ?? "No predictive drift signal selected.";
    public string BuilderSelectedPredictiveDriftTargetSummary => _selectedBuilderPredictiveDrift?.TargetSummary ?? string.Empty;
    public bool HasBuilderSelectedPredictiveDriftTargetSummary => !string.IsNullOrWhiteSpace(BuilderSelectedPredictiveDriftTargetSummary);
    public string BuilderSelectedPredictiveDriftProbabilitySummary => _selectedBuilderPredictiveDrift?.ProbabilitySummary ?? string.Empty;
    public bool HasBuilderSelectedPredictiveDriftProbabilitySummary => !string.IsNullOrWhiteSpace(BuilderSelectedPredictiveDriftProbabilitySummary);
    public string BuilderSelectedPredictiveDriftReason => _selectedBuilderPredictiveDrift?.Prediction.Summary ?? string.Empty;
    public bool HasBuilderSelectedPredictiveDriftReason => !string.IsNullOrWhiteSpace(BuilderSelectedPredictiveDriftReason);
    public bool HasBuilderSelectedPredictiveDriftEvidenceSteps => _builderSelectedPredictiveDriftEvidenceSteps.Count > 0;
    public bool HasBuilderSelectedPredictiveDriftArtifactLinks => _builderSelectedPredictiveDriftArtifactLinks.Count > 0;
    public string BuilderPredictiveDriftSelectionSummary
        => _selectedBuilderPredictiveDrift is null
            ? "Select a forecasted playbook, simulation, or comparison branch to inspect predicted failure evidence."
            : $"Forecast focus: {_selectedBuilderPredictiveDrift.TargetSummary}.";

    public AsyncRelayCommand OpenBuilderPredictiveDriftArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPredictiveDriftRow> SelectBuilderPredictiveDriftCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderPredictiveDriftArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderPredictiveDriftSurface()
    {
        BuilderPredictiveDriftRows = new ReadOnlyObservableCollection<BuilderPredictiveDriftRow>(_builderPredictiveDriftRows);
        BuilderSelectedPredictiveDriftEvidenceSteps = new ReadOnlyObservableCollection<BuilderPredictiveDriftEvidenceStepRow>(_builderSelectedPredictiveDriftEvidenceSteps);
        BuilderSelectedPredictiveDriftArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderSelectedPredictiveDriftArtifactLinks);
        OpenBuilderPredictiveDriftArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPredictiveDriftArtifactPath), () => HasBuilderPredictiveDriftArtifactPath);
        SelectBuilderPredictiveDriftCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPredictiveDriftRow>(SelectBuilderPredictiveDriftAsync, row => row is not null);
        OpenBuilderPredictiveDriftArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderPredictiveDriftArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private BuilderPredictiveDriftReport? LoadBuilderPredictiveDriftArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null,
        BuilderOperatorDecisionsRecord? decisionsArtifact = null,
        BuilderAutoSuggestionsRecord? autoSuggestionArtifact = null,
        BuilderTrustIndexRecord? trustArtifact = null,
        BuilderPreventativeGuardrailsReport? guardrailArtifact = null,
        BuilderExecutionAuditReport? auditArtifact = null,
        BuilderExecutionReadinessRecord? readinessArtifact = null)
    {
        var artifact = BuilderPredictiveDriftService.RefreshPredictiveDrift(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact,
            simulationArtifact,
            comparisonArtifact,
            accuracyArtifact,
            decisionsArtifact,
            autoSuggestionArtifact,
            trustArtifact,
            guardrailArtifact,
            auditArtifact,
            readinessArtifact);
        _builderPredictiveDriftArtifact = artifact;
        if (artifact is null)
        {
            ResetBuilderPredictiveDriftState();
            return null;
        }

        var calibration = _builderSignalCalibrationArtifact ?? BuilderSignalCalibrationService.LoadSignalCalibration(GetBuilderWorkspaceRepoRoot());
        _builderPredictiveDriftSummary = calibration is null
            ? artifact.Summary
            : $"{artifact.Summary} Active signal profile: {calibration.ActiveProfileName} ({calibration.ProfileOverrideHash}).";
        _builderPredictiveDriftArtifactPath = artifact.ArtifactPath;
        _builderPredictiveDriftRows.Clear();
        foreach (var row in artifact.Predictions
                     .Select(prediction => new BuilderPredictiveDriftRow(prediction))
                     .OrderBy(row => row.RiskRank)
                     .ThenBy(row => row.ScopeRank)
                     .ThenByDescending(row => row.Prediction.FailureProbability)
                     .ThenBy(row => row.TargetSummary, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.Prediction.PredictionId, StringComparer.OrdinalIgnoreCase))
        {
            _builderPredictiveDriftRows.Add(row);
        }

        ApplyBuilderPredictiveDriftOverlays(playbookArtifact, simulationArtifact, comparisonArtifact, accuracyArtifact);
        SyncBuilderPredictiveDriftSelection();
        NotifyBuilderPredictiveDriftStateChanged();
        return artifact;
    }

    private void ApplyBuilderPredictiveDriftOverlays(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null)
    {
        _isApplyingBuilderPredictiveDriftOverlay = true;
        try
        {
            playbookArtifact ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(GetBuilderWorkspaceRepoRoot());
            simulationArtifact ??= BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot());
            comparisonArtifact ??= BuilderRecoveryComparisonService.LoadRecoveryComparisons(GetBuilderWorkspaceRepoRoot());
            var rankingArtifact = BuilderPlaybookRankingService.LoadPlaybookRankings(GetBuilderWorkspaceRepoRoot());
            var contextFilterArtifact = BuilderPlaybookContextFilterService.LoadContextFilters(GetBuilderWorkspaceRepoRoot());
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
            _isApplyingBuilderPredictiveDriftOverlay = false;
        }
    }

    private BuilderPredictiveDriftPresentation BuildBuilderPredictiveDriftPresentation(
        string playbookId = "",
        string simulationId = "",
        BuilderRecoveryComparisonSetRecord? comparisonSet = null)
        => BuilderPredictiveDriftPresentation.FromPredictions(
            BuilderPredictiveDriftService.ResolveMatchingPredictions(
                _builderPredictiveDriftArtifact,
                playbookId,
                simulationId,
                comparisonSet?.ComparisonId ?? string.Empty));

    private void ResetBuilderPredictiveDriftState()
    {
        _builderPredictiveDriftArtifact = null;
        _builderPredictiveDriftSummary = "No predictive drift recorded.";
        _builderPredictiveDriftArtifactPath = string.Empty;
        _builderPredictiveDriftRows.Clear();
        ApplySelectedBuilderPredictiveDrift(null);
        NotifyBuilderPredictiveDriftStateChanged();
    }

    private void ApplySelectedBuilderPredictiveDrift(BuilderPredictiveDriftRow? row)
    {
        _selectedBuilderPredictiveDrift = row;
        _builderSelectedPredictiveDriftEvidenceSteps.Clear();
        _builderSelectedPredictiveDriftArtifactLinks.Clear();
        if (row is not null)
        {
            foreach (var step in row.Prediction.EvidenceChain
                         .OrderBy(step => step.StepId, StringComparer.OrdinalIgnoreCase))
            {
                _builderSelectedPredictiveDriftEvidenceSteps.Add(new BuilderPredictiveDriftEvidenceStepRow(step));
            }

            foreach (var link in row.ArtifactLinks)
            {
                _builderSelectedPredictiveDriftArtifactLinks.Add(link);
            }
        }
    }

    private void SyncBuilderPredictiveDriftSelection()
    {
        if (_builderPredictiveDriftRows.Count == 0)
        {
            ApplySelectedBuilderPredictiveDrift(null);
            return;
        }

        var selectedPredictionId = _selectedBuilderPredictiveDrift?.Prediction.PredictionId;
        var nextSelected = _builderPredictiveDriftRows.FirstOrDefault(row =>
                               string.Equals(row.Prediction.PredictionId, selectedPredictionId, StringComparison.OrdinalIgnoreCase))
                           ?? FindPredictiveDriftForCurrentSelection()
                           ?? _builderPredictiveDriftRows.FirstOrDefault();
        ApplySelectedBuilderPredictiveDrift(nextSelected);
    }

    private BuilderPredictiveDriftRow? FindPredictiveDriftForCurrentSelection()
    {
        if (_selectedBuilderRecoverySimulation is not null)
        {
            return _builderPredictiveDriftRows.FirstOrDefault(row =>
                string.Equals(row.Prediction.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Prediction.TargetId, _selectedBuilderRecoverySimulation.SimulationId, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedBuilderRecoveryComparisonSet is not null)
        {
            return _builderPredictiveDriftRows.FirstOrDefault(row =>
                string.Equals(row.Prediction.TargetType, "comparison", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Prediction.TargetId, _selectedBuilderRecoveryComparisonSet.ComparisonId, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedBuilderRecoveryPlaybook is not null)
        {
            return _builderPredictiveDriftRows.FirstOrDefault(row =>
                string.Equals(row.Prediction.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Prediction.TargetId, _selectedBuilderRecoveryPlaybook.PlaybookId, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private Task SelectBuilderPredictiveDriftAsync(BuilderPredictiveDriftRow? row)
    {
        ApplySelectedBuilderPredictiveDrift(row);
        NotifyBuilderPredictiveDriftStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderPredictiveDriftArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderPredictiveDriftStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderPredictiveDrift));
        OnPropertyChanged(nameof(BuilderPredictiveDriftSummary));
        OnPropertyChanged(nameof(BuilderPredictiveDriftAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderPredictiveDriftArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPredictiveDriftArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderPredictiveDrift));
        OnPropertyChanged(nameof(BuilderSelectedPredictiveDriftTitle));
        OnPropertyChanged(nameof(BuilderSelectedPredictiveDriftSummary));
        OnPropertyChanged(nameof(BuilderSelectedPredictiveDriftTargetSummary));
        OnPropertyChanged(nameof(HasBuilderSelectedPredictiveDriftTargetSummary));
        OnPropertyChanged(nameof(BuilderSelectedPredictiveDriftProbabilitySummary));
        OnPropertyChanged(nameof(HasBuilderSelectedPredictiveDriftProbabilitySummary));
        OnPropertyChanged(nameof(BuilderSelectedPredictiveDriftReason));
        OnPropertyChanged(nameof(HasBuilderSelectedPredictiveDriftReason));
        OnPropertyChanged(nameof(HasBuilderSelectedPredictiveDriftEvidenceSteps));
        OnPropertyChanged(nameof(HasBuilderSelectedPredictiveDriftArtifactLinks));
        OnPropertyChanged(nameof(BuilderPredictiveDriftSelectionSummary));
        OpenBuilderPredictiveDriftArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderPredictiveDriftRow(BuilderPredictiveDriftRecord Prediction)
{
    public string PredictionId => Prediction.PredictionId;
    public int RiskRank => BuilderPredictiveDriftPresentation.RiskRank(Prediction.RiskEscalation);
    public int ScopeRank => Prediction.TargetType switch
    {
        "playbook" => 0,
        "simulation" => 1,
        "comparison" => 2,
        _ => 3
    };
    public string Header => $"{BuilderPredictiveDriftPresentation.FormatToken(Prediction.RiskEscalation).ToUpperInvariant()} {BuilderPredictiveDriftPresentation.FormatToken(Prediction.TargetType)}";
    public string Summary => Prediction.Summary;
    public string TargetSummary => $"{BuilderPredictiveDriftPresentation.FormatToken(Prediction.TargetType)}: {Prediction.TargetId}";
    public string ProbabilitySummary => $"Failure likelihood {Prediction.FailureProbability:P0}. Trend: {BuilderPredictiveDriftPresentation.FormatToken(Prediction.DriftTrend)}.";
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Prediction.LinkedArtifacts
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();
}

public sealed record BuilderPredictiveDriftEvidenceStepRow(BuilderPredictiveDriftEvidenceStepRecord Step)
{
    public string Header => Step.AppliedRule.Replace('_', ' ');
    public string Summary => Step.IntermediateResult;
    public string Source => Step.InputSource;
}

public sealed record BuilderPredictiveDriftPresentation(IReadOnlyList<BuilderPredictiveDriftRecord> Matches)
{
    public static BuilderPredictiveDriftPresentation Empty { get; } = new(Array.Empty<BuilderPredictiveDriftRecord>());

    public BuilderPredictiveDriftRecord? Primary => Matches
        .OrderBy(record => RiskRank(record.RiskEscalation))
        .ThenByDescending(record => record.FailureProbability)
        .ThenBy(record => record.TargetType, StringComparer.OrdinalIgnoreCase)
        .ThenBy(record => record.TargetId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(record => record.PredictionId, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    public bool HasPrediction => Primary is not null;
    public bool HasCriticalRisk => Matches.Any(record => string.Equals(record.RiskEscalation, "critical", StringComparison.OrdinalIgnoreCase));
    public string Badge => Primary is null
        ? string.Empty
        : $"Predicted Risk: {FormatToken(Primary.RiskEscalation)}";
    public string Summary => Primary is null
        ? string.Empty
        : $"{Badge}. Failure likelihood {Primary.FailureProbability:P0}. Trend {FormatToken(Primary.DriftTrend)}.";
    public string Reason => Primary?.Summary ?? string.Empty;

    public static BuilderPredictiveDriftPresentation FromPredictions(IReadOnlyList<BuilderPredictiveDriftRecord> predictions)
        => predictions.Count == 0 ? Empty : new BuilderPredictiveDriftPresentation(predictions);

    public static int RiskRank(string riskLevel)
        => riskLevel switch
        {
            "critical" => 0,
            "high" => 1,
            "moderate" => 2,
            _ => 3
        };

    public static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}
