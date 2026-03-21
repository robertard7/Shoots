using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderPreventativeGuardrailRow> _builderPreventativeGuardrails = new();
    private readonly ObservableCollection<string> _builderSelectedPreventativeGuardrailTriggers = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderSelectedPreventativeGuardrailArtifactLinks = new();
    private bool _isApplyingPreventativeGuardrailOverlay;
    private BuilderPreventativeGuardrailsReport? _builderPreventativeGuardrailArtifact;
    private BuilderPreventativeGuardrailRow? _selectedBuilderPreventativeGuardrail;
    private string _builderPreventativeGuardrailSummary = "No preventative guardrails recorded.";
    private string _builderPreventativeGuardrailAdvisoryBanner = "Preventative guardrails are advisory only. They escalate known risk before operator action but do not block routes, apply patches, approve work, or finalize changes.";
    private string _builderPreventativeGuardrailArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderPreventativeGuardrailRow> BuilderPreventativeGuardrails { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderSelectedPreventativeGuardrailTriggers { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderSelectedPreventativeGuardrailArtifactLinks { get; private set; } = null!;

    public bool HasBuilderPreventativeGuardrails => _builderPreventativeGuardrails.Count > 0;
    public string BuilderPreventativeGuardrailSummary => _builderPreventativeGuardrailSummary;
    public string BuilderPreventativeGuardrailAdvisoryBanner => _builderPreventativeGuardrailAdvisoryBanner;
    public string BuilderPreventativeGuardrailArtifactPath => _builderPreventativeGuardrailArtifactPath;
    public bool HasBuilderPreventativeGuardrailArtifactPath => !string.IsNullOrWhiteSpace(_builderPreventativeGuardrailArtifactPath) && File.Exists(_builderPreventativeGuardrailArtifactPath);
    public bool HasSelectedBuilderPreventativeGuardrail => _selectedBuilderPreventativeGuardrail is not null;
    public string BuilderSelectedPreventativeGuardrailTitle => _selectedBuilderPreventativeGuardrail?.Header ?? "No preventative guardrail selected.";
    public string BuilderSelectedPreventativeGuardrailSummary => _selectedBuilderPreventativeGuardrail?.Summary ?? "No preventative guardrail selected.";
    public string BuilderSelectedPreventativeGuardrailTargetSummary => _selectedBuilderPreventativeGuardrail?.TargetSummary ?? string.Empty;
    public bool HasBuilderSelectedPreventativeGuardrailTargetSummary => !string.IsNullOrWhiteSpace(BuilderSelectedPreventativeGuardrailTargetSummary);
    public string BuilderSelectedPreventativeGuardrailReason => _selectedBuilderPreventativeGuardrail?.Guardrail.EscalationReason ?? string.Empty;
    public bool HasBuilderSelectedPreventativeGuardrailReason => !string.IsNullOrWhiteSpace(BuilderSelectedPreventativeGuardrailReason);
    public bool HasBuilderSelectedPreventativeGuardrailTriggers => _builderSelectedPreventativeGuardrailTriggers.Count > 0;
    public bool HasBuilderSelectedPreventativeGuardrailArtifactLinks => _builderSelectedPreventativeGuardrailArtifactLinks.Count > 0;
    public string BuilderPreventativeGuardrailSelectionSummary
        => _selectedBuilderPreventativeGuardrail is null
            ? "Select a playbook, simulation, comparison branch, or guardrail to inspect escalated risk history."
            : $"Guardrail focus: {_selectedBuilderPreventativeGuardrail.TargetSummary}.";

    public AsyncRelayCommand OpenBuilderPreventativeGuardrailArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPreventativeGuardrailRow> SelectBuilderPreventativeGuardrailCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderPreventativeGuardrailArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderPreventativeGuardrailSurface()
    {
        BuilderPreventativeGuardrails = new ReadOnlyObservableCollection<BuilderPreventativeGuardrailRow>(_builderPreventativeGuardrails);
        BuilderSelectedPreventativeGuardrailTriggers = new ReadOnlyObservableCollection<string>(_builderSelectedPreventativeGuardrailTriggers);
        BuilderSelectedPreventativeGuardrailArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderSelectedPreventativeGuardrailArtifactLinks);
        OpenBuilderPreventativeGuardrailArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderPreventativeGuardrailArtifactPath), () => HasBuilderPreventativeGuardrailArtifactPath);
        SelectBuilderPreventativeGuardrailCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderPreventativeGuardrailRow>(SelectBuilderPreventativeGuardrailAsync, row => row is not null);
        OpenBuilderPreventativeGuardrailArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderPreventativeGuardrailArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private BuilderPreventativeGuardrailsReport? LoadBuilderPreventativeGuardrailArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null,
        BuilderOperatorDecisionsRecord? decisionsArtifact = null,
        BuilderExecutionReadinessRecord? readinessArtifact = null,
        BuilderExecutionAuditReport? auditArtifact = null)
    {
        var artifact = BuilderPreventativeGuardrailService.RefreshPreventativeGuardrails(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact,
            simulationArtifact,
            accuracyArtifact,
            decisionsArtifact,
            BuilderOperatorConstraintService.LoadOperatorConstraints(GetBuilderWorkspaceRepoRoot()),
            readinessArtifact,
            auditArtifact);
        _builderPreventativeGuardrailArtifact = artifact;
        if (artifact is null)
        {
            ResetBuilderPreventativeGuardrailState();
            return null;
        }

        _builderPreventativeGuardrailSummary = artifact.Summary;
        _builderPreventativeGuardrailArtifactPath = artifact.ArtifactPath;
        _builderPreventativeGuardrails.Clear();
        foreach (var row in artifact.Guardrails
                     .Select(guardrail => new BuilderPreventativeGuardrailRow(guardrail))
                     .OrderBy(row => row.RiskRank)
                     .ThenBy(row => row.ScopeRank)
                     .ThenBy(row => row.TargetSummary, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.GuardrailId, StringComparer.OrdinalIgnoreCase))
        {
            _builderPreventativeGuardrails.Add(row);
        }

        ApplyBuilderPreventativeGuardrailOverlays(
            playbookArtifact,
            simulationArtifact,
            rankingArtifact,
            contextFilterArtifact,
            comparisonArtifact,
            accuracyArtifact);
        SyncBuilderPreventativeGuardrailSelection();
        NotifyBuilderPreventativeGuardrailStateChanged();
        return artifact;
    }

    private void ApplyBuilderPreventativeGuardrailOverlays(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderRecoverySimulationsRecord? simulationArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null,
        BuilderPlaybookContextFiltersRecord? contextFilterArtifact = null,
        BuilderRecoveryComparisonsRecord? comparisonArtifact = null,
        BuilderSimulationAccuracyReport? accuracyArtifact = null)
    {
        _isApplyingPreventativeGuardrailOverlay = true;
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
            _isApplyingPreventativeGuardrailOverlay = false;
        }
    }

    private BuilderPreventativeGuardrailPresentation BuildBuilderPreventativeGuardrailPresentation(
        string playbookId = "",
        string simulationId = "",
        string route = "")
        => BuilderPreventativeGuardrailPresentation.FromGuardrails(
            BuilderPreventativeGuardrailService.ResolveMatchingGuardrails(
                _builderPreventativeGuardrailArtifact,
                playbookId,
                simulationId,
                route,
                _builderWorkspaceSurfaceContext?.Context.ActiveWorkspaceId ?? BuilderWorkspaceService.ResolveWorkspaceId(GetBuilderWorkspaceRepoRoot())));

    private void ResetBuilderPreventativeGuardrailState()
    {
        _builderPreventativeGuardrailArtifact = null;
        _builderPreventativeGuardrailSummary = "No preventative guardrails recorded.";
        _builderPreventativeGuardrailArtifactPath = string.Empty;
        _builderPreventativeGuardrails.Clear();
        ApplySelectedBuilderPreventativeGuardrail(null);
        NotifyBuilderPreventativeGuardrailStateChanged();
    }

    private void ApplySelectedBuilderPreventativeGuardrail(BuilderPreventativeGuardrailRow? row)
    {
        _selectedBuilderPreventativeGuardrail = row;
        _builderSelectedPreventativeGuardrailTriggers.Clear();
        _builderSelectedPreventativeGuardrailArtifactLinks.Clear();
        if (row is not null)
        {
            foreach (var trigger in row.Guardrail.TriggerPatterns
                         .OrderBy(trigger => trigger, StringComparer.OrdinalIgnoreCase)
                         .Select(BuilderPreventativeGuardrailPresentation.FormatToken))
            {
                _builderSelectedPreventativeGuardrailTriggers.Add(trigger);
            }

            foreach (var link in row.ArtifactLinks)
            {
                _builderSelectedPreventativeGuardrailArtifactLinks.Add(link);
            }
        }
    }

    private void SyncBuilderPreventativeGuardrailSelection()
    {
        if (_builderPreventativeGuardrails.Count == 0)
        {
            ApplySelectedBuilderPreventativeGuardrail(null);
            return;
        }

        var selectedGuardrailId = _selectedBuilderPreventativeGuardrail?.GuardrailId;
        var nextSelected = _builderPreventativeGuardrails.FirstOrDefault(row =>
                               string.Equals(row.GuardrailId, selectedGuardrailId, StringComparison.OrdinalIgnoreCase))
                           ?? FindGuardrailForCurrentSelection()
                           ?? _builderPreventativeGuardrails.FirstOrDefault();
        ApplySelectedBuilderPreventativeGuardrail(nextSelected);
    }

    private BuilderPreventativeGuardrailRow? FindGuardrailForCurrentSelection()
    {
        if (_selectedBuilderRecoverySimulation is not null)
        {
            return _builderPreventativeGuardrails.FirstOrDefault(row =>
                string.Equals(row.Guardrail.TargetScope, "simulation", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Guardrail.TargetId, _selectedBuilderRecoverySimulation.SimulationId, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedBuilderRecoveryPlaybook is not null)
        {
            return _builderPreventativeGuardrails.FirstOrDefault(row =>
                string.Equals(row.Guardrail.TargetScope, "playbook", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Guardrail.TargetId, _selectedBuilderRecoveryPlaybook.PlaybookId, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedBuilderRecoveryComparisonSet is not null)
        {
            var scenario = _selectedBuilderRecoveryComparisonSet.Set.ComparisonMetrics.FirstOrDefault();
            if (scenario is not null)
            {
                return _builderPreventativeGuardrails.FirstOrDefault(row =>
                    string.Equals(row.Guardrail.TargetScope, "simulation", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.Guardrail.TargetId, scenario.SimulationId, StringComparison.OrdinalIgnoreCase));
            }
        }

        return _builderPreventativeGuardrails.FirstOrDefault(row =>
            string.Equals(row.Guardrail.TargetScope, "repo", StringComparison.OrdinalIgnoreCase));
    }

    private Task SelectBuilderPreventativeGuardrailAsync(BuilderPreventativeGuardrailRow? row)
    {
        ApplySelectedBuilderPreventativeGuardrail(row);
        NotifyBuilderPreventativeGuardrailStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderPreventativeGuardrailArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderPreventativeGuardrailStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderPreventativeGuardrails));
        OnPropertyChanged(nameof(BuilderPreventativeGuardrailSummary));
        OnPropertyChanged(nameof(BuilderPreventativeGuardrailAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderPreventativeGuardrailArtifactPath));
        OnPropertyChanged(nameof(HasBuilderPreventativeGuardrailArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderPreventativeGuardrail));
        OnPropertyChanged(nameof(BuilderSelectedPreventativeGuardrailTitle));
        OnPropertyChanged(nameof(BuilderSelectedPreventativeGuardrailSummary));
        OnPropertyChanged(nameof(BuilderSelectedPreventativeGuardrailTargetSummary));
        OnPropertyChanged(nameof(HasBuilderSelectedPreventativeGuardrailTargetSummary));
        OnPropertyChanged(nameof(BuilderSelectedPreventativeGuardrailReason));
        OnPropertyChanged(nameof(HasBuilderSelectedPreventativeGuardrailReason));
        OnPropertyChanged(nameof(HasBuilderSelectedPreventativeGuardrailTriggers));
        OnPropertyChanged(nameof(HasBuilderSelectedPreventativeGuardrailArtifactLinks));
        OnPropertyChanged(nameof(BuilderPreventativeGuardrailSelectionSummary));
        OpenBuilderPreventativeGuardrailArtifactCommand.RaiseCanExecuteChanged();
    }
}

public sealed record BuilderPreventativeGuardrailRow(BuilderPreventativeGuardrailRecord Guardrail)
{
    public string GuardrailId => Guardrail.GuardrailId;
    public int RiskRank => BuilderPreventativeGuardrailPresentation.RiskRank(Guardrail.RiskLevel);
    public int ScopeRank => Guardrail.TargetScope switch
    {
        "playbook" => 0,
        "simulation" => 1,
        "route" => 2,
        _ => 3
    };
    public string Header => $"{BuilderPreventativeGuardrailPresentation.FormatToken(Guardrail.RiskLevel).ToUpperInvariant()} {BuilderPreventativeGuardrailPresentation.FormatToken(Guardrail.TargetScope)}";
    public string TargetSummary => $"{BuilderPreventativeGuardrailPresentation.FormatToken(Guardrail.TargetScope)}: {Guardrail.TargetId}";
    public string Summary => Guardrail.Summary;
    public string TriggerSummary => Guardrail.TriggerPatterns.Count == 0
        ? "No trigger patterns recorded."
        : string.Join(", ", Guardrail.TriggerPatterns.Select(BuilderPreventativeGuardrailPresentation.FormatToken));
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Guardrail.EvidenceLinks
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();
}

public sealed record BuilderPreventativeGuardrailPresentation(
    IReadOnlyList<BuilderPreventativeGuardrailRecord> Matches)
{
    public static BuilderPreventativeGuardrailPresentation Empty { get; } = new(Array.Empty<BuilderPreventativeGuardrailRecord>());

    public BuilderPreventativeGuardrailRecord? Primary => Matches
        .OrderBy(entry => RiskRank(entry.RiskLevel))
        .ThenBy(entry => entry.TargetScope, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.TargetId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.GuardrailId, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    public bool HasEscalation => Primary is not null;
    public bool HasCriticalEscalation => Matches.Any(entry => string.Equals(entry.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase));
    public string RiskLevel => Primary?.RiskLevel ?? string.Empty;
    public string Badge => Primary is null ? string.Empty : $"Guardrail: {FormatToken(Primary.RiskLevel)}";
    public string Summary => Primary?.Summary ?? string.Empty;
    public string Reason => Primary?.EscalationReason ?? string.Empty;
    public string TriggerSummary => TriggerPatterns.Count == 0 ? string.Empty : string.Join(", ", TriggerPatterns.Select(FormatToken));
    public IReadOnlyList<string> TriggerPatterns => Matches
        .SelectMany(entry => entry.TriggerPatterns)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Matches
        .SelectMany(entry => entry.EvidenceLinks)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();

    public static BuilderPreventativeGuardrailPresentation FromGuardrails(IReadOnlyList<BuilderPreventativeGuardrailRecord> guardrails)
        => guardrails.Count == 0 ? Empty : new BuilderPreventativeGuardrailPresentation(guardrails);

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
