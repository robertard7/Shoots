using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderExecutionReadinessConditionRow> _builderExecutionReadinessBlockingConditions = new();
    private readonly ObservableCollection<BuilderExecutionReadinessWarningRow> _builderExecutionReadinessWarnings = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderExecutionReadinessArtifactLinks = new();
    private readonly ObservableCollection<string> _builderExecutionReadinessSignalContributions = new();
    private string _builderExecutionReadinessState = "caution";
    private string _builderExecutionReadinessSummary = "No execution readiness snapshot recorded.";
    private string _builderExecutionReadinessSelectionSummary = "No execution readiness selection recorded.";
    private string _builderExecutionReadinessIntentAlignmentSummary = "No intent alignment recorded.";
    private string _builderExecutionReadinessConstraintSummary = "No constraint alignment recorded.";
    private string _builderExecutionReadinessSignalBalanceSummary = "No signal balance recorded.";
    private string _builderExecutionReadinessAdvisoryBanner = "Go / No-Go is advisory only. It summarizes current readiness but does not block routes, apply patches, approve files, or finalize work.";
    private string _builderExecutionReadinessArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderExecutionReadinessConditionRow> BuilderExecutionReadinessBlockingConditions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderExecutionReadinessWarningRow> BuilderExecutionReadinessWarnings { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderExecutionReadinessArtifactLinks { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderExecutionReadinessSignalContributions { get; private set; } = null!;

    public bool HasBuilderExecutionReadiness => !string.IsNullOrWhiteSpace(_builderExecutionReadinessSummary) &&
                                                !string.Equals(_builderExecutionReadinessSummary, "No execution readiness snapshot recorded.", StringComparison.Ordinal);
    public string BuilderExecutionReadinessStateLabel => _builderExecutionReadinessState switch
    {
        "go" => "GO",
        "no_go" => "NO-GO",
        _ => "CAUTION"
    };
    public string BuilderExecutionReadinessSummary => _builderExecutionReadinessSummary;
    public string BuilderExecutionReadinessSelectionSummary => _builderExecutionReadinessSelectionSummary;
    public string BuilderExecutionReadinessIntentAlignmentSummary => _builderExecutionReadinessIntentAlignmentSummary;
    public string BuilderExecutionReadinessConstraintSummary => _builderExecutionReadinessConstraintSummary;
    public string BuilderExecutionReadinessSignalBalanceSummary => _builderExecutionReadinessSignalBalanceSummary;
    public string BuilderExecutionReadinessAdvisoryBanner => _builderExecutionReadinessAdvisoryBanner;
    public string BuilderExecutionReadinessArtifactPath => _builderExecutionReadinessArtifactPath;
    public bool HasBuilderExecutionReadinessArtifactPath => !string.IsNullOrWhiteSpace(_builderExecutionReadinessArtifactPath) && File.Exists(_builderExecutionReadinessArtifactPath);
    public bool HasBuilderExecutionReadinessBlockingConditions => _builderExecutionReadinessBlockingConditions.Count > 0;
    public bool HasBuilderExecutionReadinessWarnings => _builderExecutionReadinessWarnings.Count > 0;
    public bool HasBuilderExecutionReadinessArtifactLinks => _builderExecutionReadinessArtifactLinks.Count > 0;
    public bool HasBuilderExecutionReadinessSignalContributions => _builderExecutionReadinessSignalContributions.Count > 0;

    public AsyncRelayCommand OpenBuilderExecutionReadinessArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderExecutionReadinessArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderExecutionReadinessSurface()
    {
        BuilderExecutionReadinessBlockingConditions = new ReadOnlyObservableCollection<BuilderExecutionReadinessConditionRow>(_builderExecutionReadinessBlockingConditions);
        BuilderExecutionReadinessWarnings = new ReadOnlyObservableCollection<BuilderExecutionReadinessWarningRow>(_builderExecutionReadinessWarnings);
        BuilderExecutionReadinessArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderExecutionReadinessArtifactLinks);
        BuilderExecutionReadinessSignalContributions = new ReadOnlyObservableCollection<string>(_builderExecutionReadinessSignalContributions);
        OpenBuilderExecutionReadinessArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderExecutionReadinessArtifactPath), () => HasBuilderExecutionReadinessArtifactPath);
        OpenBuilderExecutionReadinessArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderExecutionReadinessArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderExecutionReadinessArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null,
        BuilderDecisionJustificationsRecord? justificationsArtifact = null)
    {
        var decisionsArtifact = BuilderOperatorDecisionService.LoadOperatorDecisions(GetBuilderWorkspaceRepoRoot());
        var artifact = BuilderExecutionReadinessService.RefreshExecutionReadiness(
            GetBuilderWorkspaceRepoRoot(),
            _selectedBuilderRecoveryPlaybook?.PlaybookId ?? string.Empty,
            _selectedBuilderRecoverySimulation?.SimulationId ?? string.Empty,
            _selectedBuilderRecoveryComparisonSet?.ComparisonId ?? string.Empty,
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            contextFilterArtifact,
            comparisonArtifact,
            accuracyArtifact,
            decisionsArtifact,
            justifications: justificationsArtifact);
        if (artifact is null)
        {
            ResetBuilderExecutionReadinessState();
            return;
        }

        _builderExecutionReadinessState = artifact.ReadinessState;
        _builderExecutionReadinessSummary = artifact.Summary;
        _builderExecutionReadinessSelectionSummary = artifact.SelectionSummary;
        _builderExecutionReadinessIntentAlignmentSummary = artifact.IntentAlignmentSummary;
        _builderExecutionReadinessConstraintSummary = artifact.ConstraintSummary;
        _builderExecutionReadinessSignalBalanceSummary = artifact.SignalBalanceSummary;
        _builderExecutionReadinessArtifactPath = artifact.ArtifactPath;

        _builderExecutionReadinessBlockingConditions.Clear();
        foreach (var condition in artifact.BlockingConditions
                     .OrderBy(condition => condition.ConditionId, StringComparer.OrdinalIgnoreCase))
        {
            _builderExecutionReadinessBlockingConditions.Add(new BuilderExecutionReadinessConditionRow(condition));
        }

        _builderExecutionReadinessWarnings.Clear();
        foreach (var warning in artifact.Warnings
                     .OrderBy(warning => warning.WarningId, StringComparer.OrdinalIgnoreCase))
        {
            _builderExecutionReadinessWarnings.Add(new BuilderExecutionReadinessWarningRow(warning));
        }

        _builderExecutionReadinessArtifactLinks.Clear();
        foreach (var link in artifact.LinkedArtifacts
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                     .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path)))
        {
            _builderExecutionReadinessArtifactLinks.Add(link);
        }

        _builderExecutionReadinessSignalContributions.Clear();
        foreach (var contribution in artifact.SignalContributions
                     .OrderByDescending(entry => entry.WeightedContribution)
                     .ThenBy(entry => entry.SignalId, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => entry.Summary))
        {
            _builderExecutionReadinessSignalContributions.Add(contribution);
        }

        LoadBuilderExecutionAuditArtifacts(
            decisionsArtifact,
            simulationArtifact,
            accuracyArtifact,
            artifact);
        var auditArtifact = BuilderExecutionAuditService.LoadExecutionAudit(GetBuilderWorkspaceRepoRoot());
        var guardrailArtifact = LoadBuilderPreventativeGuardrailArtifacts(
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            contextFilterArtifact,
            comparisonArtifact,
            accuracyArtifact,
            decisionsArtifact,
            artifact,
            auditArtifact);
        LoadBuilderSignalCalibrationArtifacts(
            rankingArtifact,
            contextFilterArtifact,
            accuracyArtifact,
            decisionsArtifact,
            guardrailArtifact);
        var autoSuggestionArtifact = LoadBuilderAutoSuggestionArtifacts(
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            contextFilterArtifact,
            comparisonArtifact,
            accuracyArtifact,
            artifact,
            guardrailArtifact,
            decisionsArtifact);
        var trustArtifact = LoadBuilderTrustIndexArtifacts(
            playbookArtifact,
            simulationArtifact,
            accuracyArtifact,
            decisionsArtifact,
            autoSuggestionArtifact,
            artifact,
            guardrailArtifact,
            auditArtifact);
        LoadBuilderPredictiveDriftArtifacts(
            playbookArtifact,
            simulationArtifact,
            comparisonArtifact,
            accuracyArtifact,
            decisionsArtifact,
            autoSuggestionArtifact,
            trustArtifact,
            guardrailArtifact,
            auditArtifact,
            artifact);
        NotifyBuilderExecutionReadinessStateChanged();
    }

    private void ResetBuilderExecutionReadinessState()
    {
        _builderExecutionReadinessState = "caution";
        _builderExecutionReadinessSummary = "No execution readiness snapshot recorded.";
        _builderExecutionReadinessSelectionSummary = "No execution readiness selection recorded.";
        _builderExecutionReadinessIntentAlignmentSummary = "No intent alignment recorded.";
        _builderExecutionReadinessConstraintSummary = "No constraint alignment recorded.";
        _builderExecutionReadinessSignalBalanceSummary = "No signal balance recorded.";
        _builderExecutionReadinessArtifactPath = string.Empty;
        _builderExecutionReadinessBlockingConditions.Clear();
        _builderExecutionReadinessWarnings.Clear();
        _builderExecutionReadinessArtifactLinks.Clear();
        _builderExecutionReadinessSignalContributions.Clear();
        ResetBuilderPreventativeGuardrailState();
        ResetBuilderAutoSuggestionState();
        ResetBuilderSignalCalibrationState();
        ResetBuilderTrustIndexState();
        ResetBuilderPredictiveDriftState();
        NotifyBuilderExecutionReadinessStateChanged();
    }

    private Task OpenBuilderExecutionReadinessArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderExecutionReadinessStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderExecutionReadiness));
        OnPropertyChanged(nameof(BuilderExecutionReadinessStateLabel));
        OnPropertyChanged(nameof(BuilderExecutionReadinessSummary));
        OnPropertyChanged(nameof(BuilderExecutionReadinessSelectionSummary));
        OnPropertyChanged(nameof(BuilderExecutionReadinessIntentAlignmentSummary));
        OnPropertyChanged(nameof(BuilderExecutionReadinessConstraintSummary));
        OnPropertyChanged(nameof(BuilderExecutionReadinessSignalBalanceSummary));
        OnPropertyChanged(nameof(BuilderExecutionReadinessAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderExecutionReadinessArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExecutionReadinessArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExecutionReadinessBlockingConditions));
        OnPropertyChanged(nameof(HasBuilderExecutionReadinessWarnings));
        OnPropertyChanged(nameof(HasBuilderExecutionReadinessArtifactLinks));
        OnPropertyChanged(nameof(HasBuilderExecutionReadinessSignalContributions));
        OpenBuilderExecutionReadinessArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderExecutionReadinessConditionRow(BuilderExecutionReadinessBlockingConditionRecord Condition)
{
    public string ConditionId => Condition.ConditionId;
    public string Header => $"{Condition.Severity.ToUpperInvariant()} blocker";
    public string Summary => Condition.Reason;
    public string EvidenceSummary => Condition.EvidenceBasis;
}

public sealed record BuilderExecutionReadinessWarningRow(BuilderExecutionReadinessWarningRecord Warning)
{
    public string WarningId => Warning.WarningId;
    public string Header => $"{Warning.Severity.ToUpperInvariant()} warning";
    public string Summary => Warning.Reason;
    public string EvidenceSummary => Warning.EvidenceBasis;
}
