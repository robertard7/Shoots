using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderRecoverySimulationRow> _builderRecoverySimulations = new();
    private readonly ObservableCollection<string> _builderRecoverySelectedSimulationStateChanges = new();
    private readonly ObservableCollection<string> _builderRecoverySelectedSimulationBlockingConditions = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderRecoverySelectedSimulationArtifactLinks = new();
    private readonly ObservableCollection<string> _builderRecoverySelectedSimulationAccuracyHistory = new();
    private readonly ObservableCollection<string> _builderRecoverySelectedSimulationSignalContributions = new();
    private BuilderRecoverySimulationRow[] _builderRecoveryAllSimulations = Array.Empty<BuilderRecoverySimulationRow>();
    private BuilderRecoverySimulationRow? _selectedBuilderRecoverySimulation;
    private string _builderRecoverySimulationSummary = "No recovery simulations recorded.";
    private string _builderRecoverySimulationAdvisoryBanner = "What-if analysis is advisory only. Simulations do not execute routes, apply patches, approve changes, or finalize work.";
    private string _builderRecoverySimulationArtifactPath = string.Empty;
    private string _builderRecoverySimulationAccuracySummary = "No simulation accuracy recorded.";
    private string _builderRecoverySimulationAccuracyArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderRecoverySimulationRow> BuilderRecoverySimulations { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoverySelectedSimulationStateChanges { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoverySelectedSimulationBlockingConditions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderRecoverySelectedSimulationArtifactLinks { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoverySelectedSimulationAccuracyHistory { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoverySelectedSimulationSignalContributions { get; private set; } = null!;

    public bool HasBuilderRecoverySimulations => _builderRecoverySimulations.Count > 0;
    public string BuilderRecoverySimulationSummary => _builderRecoverySimulationSummary;
    public string BuilderRecoverySimulationAdvisoryBanner => _builderRecoverySimulationAdvisoryBanner;
    public string BuilderRecoverySimulationArtifactPath => _builderRecoverySimulationArtifactPath;
    public bool HasBuilderRecoverySimulationArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoverySimulationArtifactPath) && File.Exists(_builderRecoverySimulationArtifactPath);
    public string BuilderRecoverySimulationAccuracySummary => _builderRecoverySimulationAccuracySummary;
    public string BuilderRecoverySimulationAccuracyArtifactPath => _builderRecoverySimulationAccuracyArtifactPath;
    public bool HasBuilderRecoverySimulationAccuracyArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoverySimulationAccuracyArtifactPath) && File.Exists(_builderRecoverySimulationAccuracyArtifactPath);
    public string BuilderRecoverySimulationSelectionSummary => _selectedBuilderRecoveryPlaybook is null
        ? "Select a recovery playbook to inspect deterministic what-if scenarios."
        : $"Showing {_builderRecoverySimulations.Count} what-if scenario(s) for {_selectedBuilderRecoveryPlaybook.Header} under {FormatBuilderRecoveryContextMode(SelectedBuilderRecoveryContextMode)} mode and {BuilderOperatorIntentService.GetIntentLabel(SelectedBuilderRecoveryIntent)} intent. Show violating options: {_showBuilderRecoveryViolatingOptions}.";
    public bool HasBuilderRecoverySelectedSimulation => _selectedBuilderRecoverySimulation is not null;
    public string BuilderRecoverySelectedSimulationTitle => _selectedBuilderRecoverySimulation?.Header ?? "No what-if scenario selected.";
    public string BuilderRecoverySelectedSimulationSummary => _selectedBuilderRecoverySimulation?.Simulation.Summary ?? "No what-if scenario selected.";
    public string BuilderRecoverySelectedSimulationPrediction => _selectedBuilderRecoverySimulation?.Simulation.PredictedOutcome ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationPrediction => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationPrediction);
    public string BuilderRecoverySelectedSimulationRiskSummary => _selectedBuilderRecoverySimulation?.RiskSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationRiskSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationRiskSummary);
    public string BuilderRecoverySelectedSimulationGateSummary => _selectedBuilderRecoverySimulation?.GateSummary ?? string.Empty;
    public string BuilderRecoverySelectedSimulationConfidenceSummary => _selectedBuilderRecoverySimulation?.ConfidenceSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationConfidenceSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationConfidenceSummary);
    public string BuilderRecoverySelectedSimulationAccuracySummary => _selectedBuilderRecoverySimulation?.AccuracySummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationAccuracySummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationAccuracySummary);
    public string BuilderRecoverySelectedSimulationTrustIndicator => _selectedBuilderRecoverySimulation?.TrustIndicator ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationTrustIndicator => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationTrustIndicator);
    public string BuilderRecoverySelectedSimulationTrustSummary => _selectedBuilderRecoverySimulation?.TrustSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationTrustSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationTrustSummary);
    public string BuilderRecoverySelectedSimulationTrustReason => _selectedBuilderRecoverySimulation?.TrustReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationTrustReason => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationTrustReason);
    public string BuilderRecoverySelectedSimulationPredictiveRiskSummary => _selectedBuilderRecoverySimulation?.PredictedRiskSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationPredictiveRiskSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationPredictiveRiskSummary);
    public string BuilderRecoverySelectedSimulationPredictiveRiskReason => _selectedBuilderRecoverySimulation?.PredictedRiskReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationPredictiveRiskReason => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationPredictiveRiskReason);
    public string BuilderRecoverySelectedSimulationSignalBalanceSummary => _selectedBuilderRecoverySimulation?.SignalBalanceSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationSignalBalanceSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationSignalBalanceSummary);
    public string BuilderRecoverySelectedSimulationIntentSummary => _selectedBuilderRecoverySimulation?.BuildIntentSummary(SelectedBuilderRecoveryIntent) ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationIntentSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationIntentSummary);
    public string BuilderRecoverySelectedSimulationConstraintSummary => _selectedBuilderRecoverySimulation?.ConstraintSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationConstraintSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationConstraintSummary);
    public string BuilderRecoverySelectedSimulationGuardrailSummary => _selectedBuilderRecoverySimulation?.GuardrailSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationGuardrailSummary => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationGuardrailSummary);
    public string BuilderRecoverySelectedSimulationGuardrailReason => _selectedBuilderRecoverySimulation?.GuardrailReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedSimulationGuardrailReason => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedSimulationGuardrailReason);
    public bool HasBuilderRecoverySelectedSimulationStateChanges => _builderRecoverySelectedSimulationStateChanges.Count > 0;
    public bool HasBuilderRecoverySelectedSimulationBlockingConditions => _builderRecoverySelectedSimulationBlockingConditions.Count > 0;
    public bool HasBuilderRecoverySelectedSimulationArtifactLinks => _builderRecoverySelectedSimulationArtifactLinks.Count > 0;
    public bool HasBuilderRecoverySelectedSimulationAccuracyHistory => _builderRecoverySelectedSimulationAccuracyHistory.Count > 0;
    public bool HasBuilderRecoverySelectedSimulationSignalContributions => _builderRecoverySelectedSimulationSignalContributions.Count > 0;

    public AsyncRelayCommand OpenBuilderRecoverySimulationArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderRecoverySimulationAccuracyArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoverySimulationRow> SelectBuilderRecoverySimulationCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderRecoverySimulationArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderRecoverySimulationSurface()
    {
        BuilderRecoverySimulations = new ReadOnlyObservableCollection<BuilderRecoverySimulationRow>(_builderRecoverySimulations);
        BuilderRecoverySelectedSimulationStateChanges = new ReadOnlyObservableCollection<string>(_builderRecoverySelectedSimulationStateChanges);
        BuilderRecoverySelectedSimulationBlockingConditions = new ReadOnlyObservableCollection<string>(_builderRecoverySelectedSimulationBlockingConditions);
        BuilderRecoverySelectedSimulationArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderRecoverySelectedSimulationArtifactLinks);
        BuilderRecoverySelectedSimulationAccuracyHistory = new ReadOnlyObservableCollection<string>(_builderRecoverySelectedSimulationAccuracyHistory);
        BuilderRecoverySelectedSimulationSignalContributions = new ReadOnlyObservableCollection<string>(_builderRecoverySelectedSimulationSignalContributions);
        OpenBuilderRecoverySimulationArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoverySimulationArtifactPath), () => HasBuilderRecoverySimulationArtifactPath);
        OpenBuilderRecoverySimulationAccuracyArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoverySimulationAccuracyArtifactPath), () => HasBuilderRecoverySimulationAccuracyArtifactPath);
        SelectBuilderRecoverySimulationCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoverySimulationRow>(SelectBuilderRecoverySimulationAsync, row => row is not null);
        OpenBuilderRecoverySimulationArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderRecoverySimulationArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderRecoverySimulationArtifacts(BuilderRecoveryPlaybooksRecord? playbookArtifact = null, BuilderCrossRepoOrchestrationContext? orchestration = null)
    {
        BuilderRecoverySimulationsRecord? artifact;
        if (orchestration is not null && _builderWorkspaceOptions.Count > 0)
        {
            var descriptors = _builderWorkspaceOptions
                .Select(option => BuilderWorkspaceService.CreateDescriptor(option.RepoRoot, option.RepoName))
                .ToArray();
            artifact = BuilderRecoverySimulationService.RefreshRecoverySimulations(
                descriptors,
                orchestration,
                _selectedBuilderWorkspaceId,
                orchestration.Plan.RequestId);
        }
        else
        {
            artifact = BuilderRecoverySimulationService.RefreshRecoverySimulations(
                GetBuilderWorkspaceRepoRoot(),
                playbookArtifact);
        }

        if (artifact is null)
        {
            ResetBuilderRecoverySimulationState();
            return;
        }

        playbookArtifact ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(GetBuilderWorkspaceRepoRoot());
        var accuracyArtifact = LoadBuilderRecoverySimulationAccuracyArtifacts(artifact);
        var titlesByPlaybookId = (playbookArtifact?.Playbooks ?? Array.Empty<BuilderRecoveryPlaybookRecord>())
            .ToDictionary(playbook => playbook.PlaybookId, playbook => playbook.Title, StringComparer.OrdinalIgnoreCase);
        _builderRecoverySimulationSummary = artifact.Summary;
        _builderRecoverySimulationArtifactPath = artifact.ArtifactPath;
        _builderRecoveryAllSimulations = artifact.Simulations
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

    private BuilderSimulationAccuracyReport? LoadBuilderRecoverySimulationAccuracyArtifacts(BuilderRecoverySimulationsRecord artifact)
    {
        var accuracyArtifact = BuilderSimulationAccuracyService.RefreshSimulationAccuracy(
            GetBuilderWorkspaceRepoRoot(),
            artifact);
        _builderRecoverySimulationAccuracySummary = accuracyArtifact?.Summary ?? "No simulation accuracy recorded.";
        _builderRecoverySimulationAccuracyArtifactPath = accuracyArtifact?.ArtifactPath ?? string.Empty;
        return accuracyArtifact;
    }

    private static BuilderRecoverySimulationAccuracyPresentation BuildAccuracyPresentation(
        BuilderRecoverySimulationRecord simulation,
        BuilderSimulationAccuracyReport? accuracyArtifact)
    {
        if (accuracyArtifact is null)
        {
            return BuilderRecoverySimulationAccuracyPresentation.Empty;
        }

        var scenarioCalibration = accuracyArtifact.SimulationTypeCalibration.FirstOrDefault(entry =>
            string.Equals(entry.Key, simulation.Scenario, StringComparison.OrdinalIgnoreCase));
        var routeCalibration = accuracyArtifact.RouteCalibration.FirstOrDefault(entry =>
            string.Equals(entry.Key, simulation.TargetRoute, StringComparison.OrdinalIgnoreCase));
        var failureCalibration = accuracyArtifact.FailureClassCalibration.FirstOrDefault(entry =>
            string.Equals(entry.Key, simulation.FailureClass, StringComparison.OrdinalIgnoreCase));
        var recentHistory = accuracyArtifact.AccuracyRecords
            .Where(entry => string.Equals(entry.SimulationId, simulation.SimulationId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.RecordId, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        return new BuilderRecoverySimulationAccuracyPresentation(
            scenarioCalibration,
            routeCalibration,
            failureCalibration,
            recentHistory);
    }

    private void ResetBuilderRecoverySimulationState()
    {
        _builderRecoverySimulationSummary = "No recovery simulations recorded.";
        _builderRecoverySimulationArtifactPath = string.Empty;
        _builderRecoverySimulationAccuracySummary = "No simulation accuracy recorded.";
        _builderRecoverySimulationAccuracyArtifactPath = string.Empty;
        _builderRecoveryAllSimulations = Array.Empty<BuilderRecoverySimulationRow>();
        _builderRecoverySimulations.Clear();
        ApplySelectedBuilderRecoverySimulation(null);
        NotifyBuilderRecoverySimulationStateChanged();
    }

    private void ApplyBuilderRecoverySimulationSelection()
    {
        var selectedPlaybookId = _selectedBuilderRecoveryPlaybook?.PlaybookId;
        var filtered = string.IsNullOrWhiteSpace(selectedPlaybookId)
            ? Array.Empty<BuilderRecoverySimulationRow>()
            : _builderRecoveryAllSimulations
                .Where(row => string.Equals(row.PlaybookId, selectedPlaybookId, StringComparison.OrdinalIgnoreCase))
                .Where(row => _showBuilderRecoveryViolatingOptions || !row.IsBlockedByConstraints)
                .OrderBy(row => row.IsBlockedByConstraints ? 1 : 0)
                .ThenBy(row => row.ScenarioRank)
                .ThenBy(row => row.SimulationId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        _builderRecoverySimulations.Clear();
        foreach (var row in filtered)
        {
            _builderRecoverySimulations.Add(row);
        }

        UpdateBuilderRecoveryConstraintVisibilitySummary();
        var selectedSimulationId = _selectedBuilderRecoverySimulation?.SimulationId;
        var nextSelected = filtered.FirstOrDefault(row => string.Equals(row.SimulationId, selectedSimulationId, StringComparison.OrdinalIgnoreCase))
                           ?? filtered.FirstOrDefault();
        ApplySelectedBuilderRecoverySimulation(nextSelected);
        ApplyBuilderDecisionJustificationSelection();
        NotifyBuilderRecoverySimulationStateChanged();
    }

    private void ApplySelectedBuilderRecoverySimulation(BuilderRecoverySimulationRow? row)
    {
        _selectedBuilderRecoverySimulation = row;
        _builderRecoverySelectedSimulationStateChanges.Clear();
        _builderRecoverySelectedSimulationBlockingConditions.Clear();
        _builderRecoverySelectedSimulationArtifactLinks.Clear();
        _builderRecoverySelectedSimulationAccuracyHistory.Clear();
        _builderRecoverySelectedSimulationSignalContributions.Clear();
        if (row is not null)
        {
            foreach (var stateChange in row.Simulation.ExpectedStateChanges)
            {
                _builderRecoverySelectedSimulationStateChanges.Add(stateChange);
            }

            foreach (var blockingCondition in row.Simulation.BlockingConditions)
            {
                _builderRecoverySelectedSimulationBlockingConditions.Add(blockingCondition);
            }

            foreach (var link in row.ArtifactLinks)
            {
                _builderRecoverySelectedSimulationArtifactLinks.Add(link);
            }

            foreach (var entry in row.AccuracyHistory)
            {
                _builderRecoverySelectedSimulationAccuracyHistory.Add(entry);
            }

            foreach (var entry in row.SignalContributionSummaries)
            {
                _builderRecoverySelectedSimulationSignalContributions.Add(entry);
            }
        }

        SyncBuilderPredictiveDriftSelection();
    }

    private Task SelectBuilderRecoverySimulationAsync(BuilderRecoverySimulationRow? row)
    {
        _builderDecisionJustificationPreferredTargetType = "simulation";
        ApplySelectedBuilderRecoverySimulation(row);
        SyncBuilderPreventativeGuardrailSelection();
        SyncBuilderPredictiveDriftSelection();
        LoadBuilderExecutionReadinessArtifacts();
        NotifyBuilderRecoverySimulationStateChanged();
        NotifyBuilderPredictiveDriftStateChanged();
        NotifyBuilderDecisionJustificationStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderRecoverySimulationArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderRecoverySimulationStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderRecoverySimulations));
        OnPropertyChanged(nameof(BuilderRecoverySimulationSummary));
        OnPropertyChanged(nameof(BuilderRecoverySimulationAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderRecoverySimulationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoverySimulationArtifactPath));
        OnPropertyChanged(nameof(BuilderRecoverySimulationAccuracySummary));
        OnPropertyChanged(nameof(BuilderRecoverySimulationAccuracyArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoverySimulationAccuracyArtifactPath));
        OnPropertyChanged(nameof(BuilderRecoverySimulationSelectionSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulation));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationTitle));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationPrediction));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationPrediction));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationRiskSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationRiskSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationGateSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationConfidenceSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationConfidenceSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationAccuracySummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationAccuracySummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationTrustIndicator));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationTrustIndicator));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationTrustSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationTrustSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationTrustReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationTrustReason));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationPredictiveRiskSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationPredictiveRiskSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationPredictiveRiskReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationPredictiveRiskReason));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationSignalBalanceSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationSignalBalanceSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationIntentSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationIntentSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationConstraintSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationConstraintSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationGuardrailSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationGuardrailSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSimulationGuardrailReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationGuardrailReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationStateChanges));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationBlockingConditions));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationArtifactLinks));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationAccuracyHistory));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSimulationSignalContributions));
        OpenBuilderRecoverySimulationArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderRecoverySimulationAccuracyArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderRecoverySimulationRow(
    BuilderRecoverySimulationRecord Simulation,
    string PlaybookTitle,
    BuilderRecoverySimulationAccuracyPresentation Accuracy,
    BuilderPreventativeGuardrailPresentation Guardrail,
    BuilderAutoSuggestionPresentation Suggestion,
    BuilderTrustPresentation Trust,
    BuilderPredictiveDriftPresentation PredictiveDrift)
{
    public string SimulationId => Simulation.SimulationId;
    public string PlaybookId => Simulation.PlaybookId;
    public string Header => $"{ScenarioLabel} [{ConfidenceLabel}]";
    public string Summary => Simulation.Summary;
    public string ScenarioLabel => FormatToken(Simulation.Scenario);
    public string ConfidenceLabel => FormatToken(Simulation.ConfidenceLevel);
    public bool IsBlockedByConstraints => string.Equals(Simulation.ConstraintCompatibility, "blocked_by_constraints", StringComparison.OrdinalIgnoreCase);
    public string ConstraintBadge => IsBlockedByConstraints ? "Blocked by constraints" : "Constraint compatible";
    public string ConstraintSummary => IsBlockedByConstraints
        ? $"{ConstraintBadge}. {Simulation.ConstraintReason}"
        : Simulation.ConstraintReason;
    public bool HasRiskEscalation => Guardrail.HasEscalation;
    public bool HasCriticalRiskEscalation => Guardrail.HasCriticalEscalation;
    public string GuardrailBadge => Guardrail.Badge;
    public string GuardrailSummary => Guardrail.Summary;
    public string GuardrailReason => Guardrail.Reason;
    public bool HasSuggestedRecommendation => Suggestion.HasSuggestion;
    public bool IsPrimarySuggestedRecommendation => Suggestion.IsPrimarySuggestion;
    public string SuggestedRecommendationBadge => Suggestion.Badge;
    public string SuggestedRecommendationSummary => Suggestion.Summary;
    public string SuggestedRecommendationReason => Suggestion.Reason;
    public bool HasTrustProfile => Trust.HasProfile;
    public string TrustBadge => Trust.Badge;
    public string TrustSummary => Trust.Summary;
    public string TrustReason => Trust.Reason;
    public bool HasPredictedRisk => PredictiveDrift.HasPrediction;
    public bool HasCriticalPredictedRisk => PredictiveDrift.HasCriticalRisk;
    public string PredictedRiskBadge => PredictiveDrift.Badge;
    public string PredictedRiskSummary => PredictiveDrift.Summary;
    public string PredictedRiskReason => PredictiveDrift.Reason;
    public string SignalBalanceSummary => Suggestion.Summary;
    public IReadOnlyList<string> SignalContributionSummaries => Suggestion.SignalContributionSummaries;
    public string GateSummary => $"Next gate: {FormatToken(Simulation.ExpectedNextBlockingGate)}. Success: {FormatToken(Simulation.SuccessLikelihood)}. Failure: {FormatToken(Simulation.FailureLikelihood)}.";
    public string RiskSummary => Simulation.RiskFlags.Count == 0
        ? $"Risk escalation: {FormatToken(Simulation.RiskEscalation)}."
        : $"Risk escalation: {FormatToken(Simulation.RiskEscalation)}. Flags: {string.Join(", ", Simulation.RiskFlags.Select(FormatToken))}.";
    public string ConfidenceSummary
        => Accuracy.ScenarioCalibration is null
            ? $"Predicted confidence: {ConfidenceLabel} ({Simulation.ConfidenceScore:P0}). Calibrated confidence is unstable because no completed comparison history is recorded yet."
            : $"Predicted confidence: {ConfidenceLabel} ({Simulation.ConfidenceScore:P0}). Calibrated confidence: {FormatToken(Accuracy.ScenarioCalibration.CalibratedConfidence)} with historical accuracy {Accuracy.ScenarioCalibration.HistoricalAccuracyRate:P0} across {Accuracy.ScenarioCalibration.SampleSize} similar simulation(s).";
    public string AccuracySummary
    {
        get
        {
            var parts = new[]
            {
                Accuracy.RouteCalibration is null
                    ? string.Empty
                    : $"Route accuracy: {Accuracy.RouteCalibration.HistoricalAccuracyRate:P0} across {Accuracy.RouteCalibration.SampleSize} route comparison(s).",
                Accuracy.FailureCalibration is null
                    ? string.Empty
                    : $"Failure-class accuracy: {Accuracy.FailureCalibration.HistoricalAccuracyRate:P0} across {Accuracy.FailureCalibration.SampleSize} failure comparison(s)."
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

            return parts.Length == 0
                ? "No route or failure-class calibration history is recorded yet."
                : string.Join(" ", parts);
        }
    }
    public string TrustIndicator => Accuracy.ScenarioCalibration?.AccuracyIndicator switch
    {
        "high_confidence" => "Trust indicator: high confidence (historically accurate).",
        "low_confidence" => "Trust indicator: low confidence (frequent mismatch history).",
        _ => "Trust indicator: unstable confidence (low sample or mixed results)."
    };
    public string BuildIntentSummary(string selectedIntent)
    {
        if (string.IsNullOrWhiteSpace(selectedIntent) || !BuilderOperatorIntentService.IsSupportedIntent(selectedIntent))
        {
            return "No explicit operator intent is selected for this what-if analysis.";
        }

        var label = BuilderOperatorIntentService.GetIntentLabel(selectedIntent);
        return selectedIntent switch
        {
            BuilderOperatorIntentService.FastRecoveryIntent when string.Equals(Simulation.Scenario, "retry_same_route", StringComparison.OrdinalIgnoreCase) ||
                                                                 string.Equals(Simulation.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase)
                => $"{label}: this scenario prioritizes short recovery loops and faster re-entry, but may leave broader corrective work for later review.",
            BuilderOperatorIntentService.SafeRecoveryIntent when string.Equals(Simulation.Scenario, "isolate_high_risk_files", StringComparison.OrdinalIgnoreCase) ||
                                                                 string.Equals(Simulation.Scenario, "switch_route_manual", StringComparison.OrdinalIgnoreCase)
                => $"{label}: this scenario emphasizes risk isolation and stronger review clarity before another attempt.",
            BuilderOperatorIntentService.MinimalChangeIntent when string.Equals(Simulation.Scenario, "reduce_scope", StringComparison.OrdinalIgnoreCase)
                => $"{label}: this scenario narrows the change surface and limits rework to the smallest recorded scope.",
            BuilderOperatorIntentService.FullResolutionIntent when string.Equals(Simulation.Scenario, "switch_route_manual", StringComparison.OrdinalIgnoreCase) ||
                                                                   string.Equals(Simulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase)
                => $"{label}: this scenario trades speed for broader corrective coverage and a more complete recovery path.",
            BuilderOperatorIntentService.UnblockOrchestrationIntent when string.Equals(Simulation.Scenario, "staged_orchestration", StringComparison.OrdinalIgnoreCase)
                => $"{label}: this scenario stages recovery by repo so blocked orchestration segments can clear in a controlled order.",
            _ => $"{label}: this scenario remains available, but its main tradeoff is {ScenarioLabel} with {ConfidenceLabel} confidence rather than a direct goal match."
        };
    }
    public IReadOnlyList<string> AccuracyHistory => Accuracy.RecentHistory
        .Select(record => record.Summary)
        .ToArray();
    public int ScenarioRank => Simulation.Scenario switch
    {
        "retry_same_route" => 0,
        "switch_route_manual" => 1,
        "reduce_scope" => 2,
        "staged_orchestration" => 3,
        "isolate_high_risk_files" => 4,
        _ => 5
    };
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Simulation.ArtifactLinks
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();

    private static string FormatToken(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}

public sealed record BuilderRecoverySimulationAccuracyPresentation(
    BuilderSimulationCalibrationRecord? ScenarioCalibration,
    BuilderSimulationCalibrationRecord? RouteCalibration,
    BuilderSimulationCalibrationRecord? FailureCalibration,
    IReadOnlyList<BuilderSimulationAccuracyRecord> RecentHistory)
{
    public static BuilderRecoverySimulationAccuracyPresentation Empty { get; }
        = new(null, null, null, Array.Empty<BuilderSimulationAccuracyRecord>());
}
