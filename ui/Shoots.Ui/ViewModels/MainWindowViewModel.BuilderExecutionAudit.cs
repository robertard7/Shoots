using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderExecutionAuditRow> _builderExecutionAuditRows = new();
    private readonly ObservableCollection<BuilderExecutionAuditEvidenceStepRow> _builderSelectedExecutionAuditEvidenceSteps = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderSelectedExecutionAuditArtifactLinks = new();
    private BuilderExecutionAuditRow? _selectedBuilderExecutionAudit;
    private string _builderExecutionAuditSummary = "No execution audit recorded.";
    private string _builderExecutionAuditAdvisoryBanner = "Execution auditing is advisory only. It documents drift and impact after operator actions without rolling back changes, correcting outcomes, or changing review and finalize authority.";
    private string _builderExecutionAuditArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderExecutionAuditRow> BuilderExecutionAuditRows { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderExecutionAuditEvidenceStepRow> BuilderSelectedExecutionAuditEvidenceSteps { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderSelectedExecutionAuditArtifactLinks { get; private set; } = null!;

    public bool HasBuilderExecutionAudits => _builderExecutionAuditRows.Count > 0;
    public string BuilderExecutionAuditSummary => _builderExecutionAuditSummary;
    public string BuilderExecutionAuditAdvisoryBanner => _builderExecutionAuditAdvisoryBanner;
    public string BuilderExecutionAuditArtifactPath => _builderExecutionAuditArtifactPath;
    public bool HasBuilderExecutionAuditArtifactPath => !string.IsNullOrWhiteSpace(_builderExecutionAuditArtifactPath) && File.Exists(_builderExecutionAuditArtifactPath);
    public bool HasSelectedBuilderExecutionAudit => _selectedBuilderExecutionAudit is not null;
    public string BuilderSelectedExecutionAuditTitle => _selectedBuilderExecutionAudit?.Header ?? "No execution audit selected.";
    public string BuilderSelectedExecutionAuditSummary => _selectedBuilderExecutionAudit?.Summary ?? "No execution audit selected.";
    public string BuilderSelectedExecutionAuditExpectedOutcome => _selectedBuilderExecutionAudit?.Audit.ExpectedOutcome ?? string.Empty;
    public bool HasBuilderSelectedExecutionAuditExpectedOutcome => !string.IsNullOrWhiteSpace(BuilderSelectedExecutionAuditExpectedOutcome);
    public string BuilderSelectedExecutionAuditActualOutcome => _selectedBuilderExecutionAudit?.Audit.ActualOutcome ?? string.Empty;
    public bool HasBuilderSelectedExecutionAuditActualOutcome => !string.IsNullOrWhiteSpace(BuilderSelectedExecutionAuditActualOutcome);
    public string BuilderSelectedExecutionAuditDriftSummary => _selectedBuilderExecutionAudit?.DriftSummary ?? string.Empty;
    public bool HasBuilderSelectedExecutionAuditDriftSummary => !string.IsNullOrWhiteSpace(BuilderSelectedExecutionAuditDriftSummary);
    public string BuilderSelectedExecutionAuditAlignmentSummary => _selectedBuilderExecutionAudit?.AlignmentSummary ?? string.Empty;
    public bool HasBuilderSelectedExecutionAuditAlignmentSummary => !string.IsNullOrWhiteSpace(BuilderSelectedExecutionAuditAlignmentSummary);
    public string BuilderSelectedExecutionAuditReason => _selectedBuilderExecutionAudit?.Audit.DriftReason ?? string.Empty;
    public bool HasBuilderSelectedExecutionAuditReason => !string.IsNullOrWhiteSpace(BuilderSelectedExecutionAuditReason);
    public bool HasBuilderSelectedExecutionAuditEvidenceSteps => _builderSelectedExecutionAuditEvidenceSteps.Count > 0;
    public bool HasBuilderSelectedExecutionAuditArtifactLinks => _builderSelectedExecutionAuditArtifactLinks.Count > 0;
    public string BuilderExecutionAuditSelectionSummary
        => _selectedBuilderExecutionAudit is null
            ? "Select an operator decision or audit record to compare expected and actual outcomes."
            : $"Auditing decision {_selectedBuilderExecutionAudit.DecisionId} for {BuilderExecutionAuditRow.FormatValue(_selectedBuilderExecutionAudit.Audit.ActionTaken)}.";

    public AsyncRelayCommand OpenBuilderExecutionAuditArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderExecutionAuditRow> SelectBuilderExecutionAuditCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderExecutionAuditArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderExecutionAuditSurface()
    {
        BuilderExecutionAuditRows = new ReadOnlyObservableCollection<BuilderExecutionAuditRow>(_builderExecutionAuditRows);
        BuilderSelectedExecutionAuditEvidenceSteps = new ReadOnlyObservableCollection<BuilderExecutionAuditEvidenceStepRow>(_builderSelectedExecutionAuditEvidenceSteps);
        BuilderSelectedExecutionAuditArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderSelectedExecutionAuditArtifactLinks);
        OpenBuilderExecutionAuditArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderExecutionAuditArtifactPath), () => HasBuilderExecutionAuditArtifactPath);
        SelectBuilderExecutionAuditCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderExecutionAuditRow>(SelectBuilderExecutionAuditAsync, row => row is not null);
        OpenBuilderExecutionAuditArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderExecutionAuditArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderExecutionAuditArtifacts(
        BuilderOperatorDecisionsRecord? decisions = null,
        BuilderRecoverySimulationsRecord? simulations = null,
        BuilderSimulationAccuracyReport? accuracy = null,
        BuilderExecutionReadinessRecord? readiness = null)
    {
        var artifact = BuilderExecutionAuditService.RefreshExecutionAudit(
            GetBuilderWorkspaceRepoRoot(),
            decisions,
            simulations,
            accuracy,
            readiness);
        if (artifact is null)
        {
            ResetBuilderExecutionAuditState();
            return;
        }

        _builderExecutionAuditSummary = artifact.Summary;
        _builderExecutionAuditArtifactPath = artifact.ArtifactPath;
        _builderExecutionAuditRows.Clear();
        foreach (var row in artifact.AuditRecords
                     .Select(audit => new BuilderExecutionAuditRow(audit))
                     .OrderBy(row => row.Audit.ObservedUtc)
                     .ThenBy(row => row.DecisionId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.AuditId, StringComparer.OrdinalIgnoreCase))
        {
            _builderExecutionAuditRows.Add(row);
        }

        var selectedAuditId = _selectedBuilderExecutionAudit?.AuditId;
        var nextSelected = _builderExecutionAuditRows.FirstOrDefault(row => string.Equals(row.AuditId, selectedAuditId, StringComparison.OrdinalIgnoreCase))
                           ?? _builderExecutionAuditRows.FirstOrDefault(row => string.Equals(row.DecisionId, _selectedBuilderOperatorDecision?.DecisionId, StringComparison.OrdinalIgnoreCase))
                           ?? _builderExecutionAuditRows.LastOrDefault();
        ApplySelectedBuilderExecutionAudit(nextSelected);
        NotifyBuilderExecutionAuditStateChanged();
    }

    private void ResetBuilderExecutionAuditState()
    {
        _builderExecutionAuditSummary = "No execution audit recorded.";
        _builderExecutionAuditArtifactPath = string.Empty;
        _builderExecutionAuditRows.Clear();
        ApplySelectedBuilderExecutionAudit(null);
        NotifyBuilderExecutionAuditStateChanged();
    }

    private void ApplySelectedBuilderExecutionAudit(BuilderExecutionAuditRow? row)
    {
        _selectedBuilderExecutionAudit = row;
        _builderSelectedExecutionAuditEvidenceSteps.Clear();
        _builderSelectedExecutionAuditArtifactLinks.Clear();
        if (row is not null)
        {
            foreach (var step in row.Audit.EvidenceChain
                         .OrderBy(step => step.StepId, StringComparer.OrdinalIgnoreCase))
            {
                _builderSelectedExecutionAuditEvidenceSteps.Add(new BuilderExecutionAuditEvidenceStepRow(step));
            }

            foreach (var link in row.ArtifactLinks)
            {
                _builderSelectedExecutionAuditArtifactLinks.Add(link);
            }
        }
    }

    private void SyncBuilderExecutionAuditSelection()
    {
        if (_builderExecutionAuditRows.Count == 0)
        {
            ApplySelectedBuilderExecutionAudit(null);
            return;
        }

        var nextSelected = _builderExecutionAuditRows.FirstOrDefault(row =>
            string.Equals(row.DecisionId, _selectedBuilderOperatorDecision?.DecisionId, StringComparison.OrdinalIgnoreCase))
                           ?? _builderExecutionAuditRows.LastOrDefault();
        ApplySelectedBuilderExecutionAudit(nextSelected);
    }

    private Task SelectBuilderExecutionAuditAsync(BuilderExecutionAuditRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        ApplySelectedBuilderExecutionAudit(row);
        var decisionRow = _builderOperatorDecisionRows.FirstOrDefault(entry =>
            string.Equals(entry.DecisionId, row.DecisionId, StringComparison.OrdinalIgnoreCase));
        ApplySelectedBuilderOperatorDecision(decisionRow);
        NotifyBuilderOperatorDecisionStateChanged();
        NotifyBuilderExecutionAuditStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderExecutionAuditArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderExecutionAuditStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderExecutionAudits));
        OnPropertyChanged(nameof(BuilderExecutionAuditSummary));
        OnPropertyChanged(nameof(BuilderExecutionAuditAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderExecutionAuditArtifactPath));
        OnPropertyChanged(nameof(HasBuilderExecutionAuditArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderExecutionAudit));
        OnPropertyChanged(nameof(BuilderSelectedExecutionAuditTitle));
        OnPropertyChanged(nameof(BuilderSelectedExecutionAuditSummary));
        OnPropertyChanged(nameof(BuilderSelectedExecutionAuditExpectedOutcome));
        OnPropertyChanged(nameof(HasBuilderSelectedExecutionAuditExpectedOutcome));
        OnPropertyChanged(nameof(BuilderSelectedExecutionAuditActualOutcome));
        OnPropertyChanged(nameof(HasBuilderSelectedExecutionAuditActualOutcome));
        OnPropertyChanged(nameof(BuilderSelectedExecutionAuditDriftSummary));
        OnPropertyChanged(nameof(HasBuilderSelectedExecutionAuditDriftSummary));
        OnPropertyChanged(nameof(BuilderSelectedExecutionAuditAlignmentSummary));
        OnPropertyChanged(nameof(HasBuilderSelectedExecutionAuditAlignmentSummary));
        OnPropertyChanged(nameof(BuilderSelectedExecutionAuditReason));
        OnPropertyChanged(nameof(HasBuilderSelectedExecutionAuditReason));
        OnPropertyChanged(nameof(HasBuilderSelectedExecutionAuditEvidenceSteps));
        OnPropertyChanged(nameof(HasBuilderSelectedExecutionAuditArtifactLinks));
        OnPropertyChanged(nameof(BuilderExecutionAuditSelectionSummary));
        OpenBuilderExecutionAuditArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderExecutionAuditRow(BuilderExecutionAuditRecord Audit)
{
    public string AuditId => Audit.AuditId;
    public string DecisionId => Audit.DecisionId;
    public string Header => $"{FormatValue(Audit.ActionTaken)} [{FormatValue(Audit.DriftType)} | {FormatValue(Audit.ImpactLevel)}]";
    public string Summary => Audit.Summary;
    public string ContextSummary => $"Decision: {DecisionId}. Repo: {FormatValue(Audit.TargetRepo)}. Route: {FormatValue(Audit.TargetRoute)}. Playbook: {FormatValue(Audit.PlaybookId)}. Simulation: {FormatValue(Audit.SimulationId)}.";
    public string DriftSummary => $"Drift {FormatValue(Audit.DriftType)} with {FormatValue(Audit.ImpactLevel)} impact. Match: {FormatValue(Audit.MatchType)}. Error: {FormatValue(Audit.ErrorClass)}. Readiness: {FormatValue(Audit.ReadinessState)}.";
    public string AlignmentSummary => $"Constraint drift: {Audit.ConstraintDriftDetected}. Intent drift: {Audit.IntentDriftDetected}.";
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Audit.LinkedArtifacts
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();

    internal static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}

public sealed record BuilderExecutionAuditEvidenceStepRow(BuilderExecutionAuditEvidenceStepRecord Step)
{
    public string StepId => Step.StepId;
    public string Header => $"{Path.GetFileName(Step.InputSource)} -> {Step.AppliedRule}";
    public string Summary => Step.IntermediateResult;
}
