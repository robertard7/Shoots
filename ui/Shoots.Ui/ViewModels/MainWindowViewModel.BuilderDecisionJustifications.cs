using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderDecisionJustificationRow> _builderDecisionJustifications = new();
    private readonly ObservableCollection<BuilderDecisionJustificationStepRow> _builderSelectedDecisionJustificationSteps = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderSelectedDecisionJustificationArtifactLinks = new();
    private BuilderDecisionJustificationRow[] _builderAllDecisionJustifications = Array.Empty<BuilderDecisionJustificationRow>();
    private BuilderDecisionJustificationRow? _selectedBuilderDecisionJustification;
    private string _builderDecisionJustificationSummary = "No decision justifications recorded.";
    private string _builderDecisionJustificationAdvisoryBanner = "Decision justifications are advisory only. They explain ranking, filtering, simulation, and comparison outcomes without changing routing, review, approval, or finalize state.";
    private string _builderDecisionJustificationArtifactPath = string.Empty;
    private string _builderDecisionJustificationPreferredTargetType = "playbook";

    public ReadOnlyObservableCollection<BuilderDecisionJustificationRow> BuilderDecisionJustifications { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderDecisionJustificationStepRow> BuilderSelectedDecisionJustificationSteps { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderSelectedDecisionJustificationArtifactLinks { get; private set; } = null!;

    public bool HasBuilderDecisionJustifications => _builderDecisionJustifications.Count > 0;
    public string BuilderDecisionJustificationSummary => _builderDecisionJustificationSummary;
    public string BuilderDecisionJustificationAdvisoryBanner => _builderDecisionJustificationAdvisoryBanner;
    public string BuilderDecisionJustificationArtifactPath => _builderDecisionJustificationArtifactPath;
    public bool HasBuilderDecisionJustificationArtifactPath => !string.IsNullOrWhiteSpace(_builderDecisionJustificationArtifactPath) && File.Exists(_builderDecisionJustificationArtifactPath);
    public bool HasSelectedBuilderDecisionJustification => _selectedBuilderDecisionJustification is not null;
    public string BuilderSelectedDecisionJustificationTitle => _selectedBuilderDecisionJustification?.Header ?? "No decision justification selected.";
    public string BuilderSelectedDecisionJustificationSummary => _selectedBuilderDecisionJustification?.Justification.Summary ?? "No decision justification selected.";
    public string BuilderSelectedDecisionJustificationAuditNarrative => _selectedBuilderDecisionJustification?.Justification.AuditNarrative ?? string.Empty;
    public bool HasBuilderSelectedDecisionJustificationAuditNarrative => !string.IsNullOrWhiteSpace(BuilderSelectedDecisionJustificationAuditNarrative);
    public bool HasBuilderSelectedDecisionJustificationSteps => _builderSelectedDecisionJustificationSteps.Count > 0;
    public bool HasBuilderSelectedDecisionJustificationArtifactLinks => _builderSelectedDecisionJustificationArtifactLinks.Count > 0;
    public string BuilderDecisionJustificationSelectionSummary
        => _selectedBuilderDecisionJustification is null
            ? "Select a playbook, what-if scenario, or comparison set to inspect the reasoning chain."
            : $"Explaining {_selectedBuilderDecisionJustification.TargetTypeLabel.ToLowerInvariant()} {_selectedBuilderDecisionJustification.TargetLabel}.";

    public AsyncRelayCommand OpenBuilderDecisionJustificationArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderDecisionJustificationRow> SelectBuilderDecisionJustificationCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderDecisionJustificationArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderDecisionJustificationSurface()
    {
        BuilderDecisionJustifications = new ReadOnlyObservableCollection<BuilderDecisionJustificationRow>(_builderDecisionJustifications);
        BuilderSelectedDecisionJustificationSteps = new ReadOnlyObservableCollection<BuilderDecisionJustificationStepRow>(_builderSelectedDecisionJustificationSteps);
        BuilderSelectedDecisionJustificationArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderSelectedDecisionJustificationArtifactLinks);
        OpenBuilderDecisionJustificationArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderDecisionJustificationArtifactPath), () => HasBuilderDecisionJustificationArtifactPath);
        SelectBuilderDecisionJustificationCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderDecisionJustificationRow>(SelectBuilderDecisionJustificationAsync, row => row is not null);
        OpenBuilderDecisionJustificationArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderDecisionJustificationArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderDecisionJustificationArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null)
    {
        var artifact = BuilderDecisionJustificationService.RefreshDecisionJustifications(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            contextFilterArtifact,
            comparisonArtifact,
            accuracyArtifact);
        if (artifact is null)
        {
            ResetBuilderDecisionJustificationState();
            return;
        }

        _builderDecisionJustificationSummary = artifact.Summary;
        _builderDecisionJustificationArtifactPath = artifact.ArtifactPath;
        _builderAllDecisionJustifications = artifact.Justifications
            .Select(justification => new BuilderDecisionJustificationRow(justification))
            .OrderBy(row => row.TargetTypeRank)
            .ThenBy(row => row.TargetLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ApplyBuilderDecisionJustificationSelection();
    }

    private void ResetBuilderDecisionJustificationState()
    {
        _builderDecisionJustificationSummary = "No decision justifications recorded.";
        _builderDecisionJustificationArtifactPath = string.Empty;
        _builderDecisionJustificationPreferredTargetType = "playbook";
        _builderAllDecisionJustifications = Array.Empty<BuilderDecisionJustificationRow>();
        _builderDecisionJustifications.Clear();
        ApplySelectedBuilderDecisionJustification(null);
        NotifyBuilderDecisionJustificationStateChanged();
    }

    private void ApplyBuilderDecisionJustificationSelection()
    {
        _builderDecisionJustifications.Clear();
        foreach (var row in _builderAllDecisionJustifications)
        {
            _builderDecisionJustifications.Add(row);
        }

        var selectedId = _selectedBuilderDecisionJustification?.JustificationId;
        var nextSelected = _builderAllDecisionJustifications.FirstOrDefault(row =>
            string.Equals(row.JustificationId, selectedId, StringComparison.OrdinalIgnoreCase));
        nextSelected ??= ResolvePreferredDecisionJustification();
        ApplySelectedBuilderDecisionJustification(nextSelected ?? _builderAllDecisionJustifications.FirstOrDefault());
        NotifyBuilderDecisionJustificationStateChanged();
    }

    private BuilderDecisionJustificationRow? ResolvePreferredDecisionJustification()
    {
        if (string.Equals(_builderDecisionJustificationPreferredTargetType, "comparison", StringComparison.OrdinalIgnoreCase) &&
            _selectedBuilderRecoveryComparisonSet is not null)
        {
            return _builderAllDecisionJustifications.FirstOrDefault(row =>
                string.Equals(row.TargetType, "comparison", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.TargetId, _selectedBuilderRecoveryComparisonSet.ComparisonId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(_builderDecisionJustificationPreferredTargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
            _selectedBuilderRecoverySimulation is not null)
        {
            return _builderAllDecisionJustifications.FirstOrDefault(row =>
                string.Equals(row.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.TargetId, _selectedBuilderRecoverySimulation.SimulationId, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedBuilderRecoveryPlaybook is not null)
        {
            return _builderAllDecisionJustifications.FirstOrDefault(row =>
                string.Equals(row.TargetType, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.TargetId, _selectedBuilderRecoveryPlaybook.PlaybookId, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedBuilderRecoverySimulation is not null)
        {
            return _builderAllDecisionJustifications.FirstOrDefault(row =>
                string.Equals(row.TargetType, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.TargetId, _selectedBuilderRecoverySimulation.SimulationId, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedBuilderRecoveryComparisonSet is not null)
        {
            return _builderAllDecisionJustifications.FirstOrDefault(row =>
                string.Equals(row.TargetType, "comparison", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.TargetId, _selectedBuilderRecoveryComparisonSet.ComparisonId, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private void ApplySelectedBuilderDecisionJustification(BuilderDecisionJustificationRow? row)
    {
        _selectedBuilderDecisionJustification = row;
        _builderSelectedDecisionJustificationSteps.Clear();
        _builderSelectedDecisionJustificationArtifactLinks.Clear();
        if (row is not null)
        {
            foreach (var step in row.Justification.ReasoningChain
                         .OrderBy(step => step.StepId, StringComparer.OrdinalIgnoreCase))
            {
                _builderSelectedDecisionJustificationSteps.Add(new BuilderDecisionJustificationStepRow(step));
            }

            foreach (var link in row.Justification.EvidenceLinks
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path)))
            {
                _builderSelectedDecisionJustificationArtifactLinks.Add(link);
            }
        }
    }

    private Task SelectBuilderDecisionJustificationAsync(BuilderDecisionJustificationRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        _builderDecisionJustificationPreferredTargetType = row.TargetType;
        ApplySelectedBuilderDecisionJustification(row);
        switch (row.TargetType)
        {
            case "playbook":
            {
                var playbook = _builderRecoveryAllPlaybooks.FirstOrDefault(entry =>
                    string.Equals(entry.PlaybookId, row.TargetId, StringComparison.OrdinalIgnoreCase));
                ApplySelectedBuilderRecoveryPlaybook(playbook);
                NotifyBuilderRecoveryStateChanged();
                NotifyBuilderRecoverySimulationStateChanged();
                NotifyBuilderRecoveryComparisonStateChanged();
                break;
            }
            case "simulation":
            {
                var simulation = _builderRecoveryAllSimulations.FirstOrDefault(entry =>
                    string.Equals(entry.SimulationId, row.TargetId, StringComparison.OrdinalIgnoreCase));
                if (simulation is not null)
                {
                    var playbook = _builderRecoveryAllPlaybooks.FirstOrDefault(entry =>
                        string.Equals(entry.PlaybookId, simulation.PlaybookId, StringComparison.OrdinalIgnoreCase));
                    ApplySelectedBuilderRecoveryPlaybook(playbook);
                    ApplySelectedBuilderRecoverySimulation(simulation);
                    NotifyBuilderRecoveryStateChanged();
                    NotifyBuilderRecoverySimulationStateChanged();
                    NotifyBuilderRecoveryComparisonStateChanged();
                }

                break;
            }
            case "comparison":
            {
                var comparison = _builderRecoveryAllComparisonSets.FirstOrDefault(entry =>
                    string.Equals(entry.ComparisonId, row.TargetId, StringComparison.OrdinalIgnoreCase));
                ApplySelectedBuilderRecoveryComparisonSet(comparison);
                NotifyBuilderRecoveryComparisonStateChanged();
                break;
            }
        }

        ApplyBuilderDecisionJustificationSelection();
        LoadBuilderExecutionReadinessArtifacts();
        return Task.CompletedTask;
    }

    private Task OpenBuilderDecisionJustificationArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderDecisionJustificationStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderDecisionJustifications));
        OnPropertyChanged(nameof(BuilderDecisionJustificationSummary));
        OnPropertyChanged(nameof(BuilderDecisionJustificationAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderDecisionJustificationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderDecisionJustificationArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderDecisionJustification));
        OnPropertyChanged(nameof(BuilderSelectedDecisionJustificationTitle));
        OnPropertyChanged(nameof(BuilderSelectedDecisionJustificationSummary));
        OnPropertyChanged(nameof(BuilderSelectedDecisionJustificationAuditNarrative));
        OnPropertyChanged(nameof(HasBuilderSelectedDecisionJustificationAuditNarrative));
        OnPropertyChanged(nameof(HasBuilderSelectedDecisionJustificationSteps));
        OnPropertyChanged(nameof(HasBuilderSelectedDecisionJustificationArtifactLinks));
        OnPropertyChanged(nameof(BuilderDecisionJustificationSelectionSummary));
        OpenBuilderDecisionJustificationArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderDecisionJustificationRow(BuilderDecisionJustificationRecord Justification)
{
    public string JustificationId => Justification.JustificationId;
    public string TargetType => Justification.TargetType;
    public string TargetId => Justification.TargetId;
    public string TargetLabel => Justification.TargetLabel;
    public int TargetTypeRank => TargetType switch
    {
        "playbook" => 0,
        "simulation" => 1,
        "comparison" => 2,
        _ => 3
    };
    public string TargetTypeLabel => TargetType switch
    {
        "playbook" => "Playbook",
        "simulation" => "Simulation",
        "comparison" => "Comparison",
        _ => "Explanation"
    };
    public string Header => $"[{TargetTypeLabel}] {TargetLabel}";
    public string Summary => Justification.Summary;
    public string AuditBadge => $"{Justification.ReasoningChain.Count} reasoning step(s)";
}

public sealed record BuilderDecisionJustificationStepRow(BuilderDecisionJustificationStepRecord Step)
{
    public string StepId => Step.StepId;
    public string Header => $"{Path.GetFileName(Step.InputSource)} -> {Step.AppliedRule}";
    public string Summary => Step.IntermediateResult;
}
