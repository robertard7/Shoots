using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderOperatorDecisionRow> _builderOperatorDecisionRows = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderOperatorDecisionTriggerArtifacts = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderOperatorDecisionResultArtifacts = new();
    private BuilderOperatorDecisionRow? _selectedBuilderOperatorDecision;
    private string _builderOperatorDecisionSummary = "No operator decisions recorded.";
    private string _builderOperatorDecisionArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderOperatorDecisionRow> BuilderOperatorDecisionRows { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderOperatorDecisionTriggerArtifacts { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderOperatorDecisionResultArtifacts { get; private set; } = null!;

    public bool HasBuilderOperatorDecisions => _builderOperatorDecisionRows.Count > 0;
    public string BuilderOperatorDecisionSummary => _builderOperatorDecisionSummary;
    public string BuilderOperatorDecisionArtifactPath => _builderOperatorDecisionArtifactPath;
    public bool HasBuilderOperatorDecisionArtifactPath => !string.IsNullOrWhiteSpace(_builderOperatorDecisionArtifactPath) && File.Exists(_builderOperatorDecisionArtifactPath);
    public bool HasSelectedBuilderOperatorDecision => _selectedBuilderOperatorDecision is not null;
    public string BuilderOperatorDecisionSelectedTitle => _selectedBuilderOperatorDecision?.Header ?? "No operator decision selected.";
    public string BuilderOperatorDecisionSelectedSummary => _selectedBuilderOperatorDecision?.Summary ?? "No operator decision selected.";
    public string BuilderOperatorDecisionSelectedContext => _selectedBuilderOperatorDecision?.ContextSummary ?? string.Empty;
    public bool HasBuilderOperatorDecisionSelectedContext => !string.IsNullOrWhiteSpace(BuilderOperatorDecisionSelectedContext);
    public string BuilderOperatorDecisionSelectedOutcome => _selectedBuilderOperatorDecision?.Decision.OutcomeSummary ?? string.Empty;
    public bool HasBuilderOperatorDecisionSelectedOutcome => !string.IsNullOrWhiteSpace(BuilderOperatorDecisionSelectedOutcome);
    public bool HasBuilderOperatorDecisionTriggerArtifacts => _builderOperatorDecisionTriggerArtifacts.Count > 0;
    public bool HasBuilderOperatorDecisionResultArtifacts => _builderOperatorDecisionResultArtifacts.Count > 0;

    public AsyncRelayCommand OpenBuilderOperatorDecisionArtifactCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderOperatorDecisionRow> SelectBuilderOperatorDecisionCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderOperatorDecisionArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderOperatorDecisionSurface()
    {
        BuilderOperatorDecisionRows = new ReadOnlyObservableCollection<BuilderOperatorDecisionRow>(_builderOperatorDecisionRows);
        BuilderOperatorDecisionTriggerArtifacts = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderOperatorDecisionTriggerArtifacts);
        BuilderOperatorDecisionResultArtifacts = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderOperatorDecisionResultArtifacts);
        OpenBuilderOperatorDecisionArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderOperatorDecisionArtifactPath), () => HasBuilderOperatorDecisionArtifactPath);
        SelectBuilderOperatorDecisionCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderOperatorDecisionRow>(SelectBuilderOperatorDecisionAsync, row => row is not null);
        OpenBuilderOperatorDecisionArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderOperatorDecisionArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
    }

    private void LoadBuilderOperatorDecisionArtifacts(BuilderOperatorDecisionsRecord? artifact = null)
    {
        artifact ??= BuilderOperatorDecisionService.LoadOperatorDecisions(GetBuilderWorkspaceRepoRoot());
        if (artifact is null)
        {
            ResetBuilderOperatorDecisionState();
            return;
        }

        _builderOperatorDecisionSummary = artifact.Summary;
        _builderOperatorDecisionArtifactPath = artifact.ArtifactPath;
        _builderOperatorDecisionRows.Clear();
        foreach (var row in artifact.Decisions
                     .Select(decision => new BuilderOperatorDecisionRow(decision))
                     .OrderBy(row => row.Decision.Timestamp)
                     .ThenBy(row => row.DecisionId, StringComparer.OrdinalIgnoreCase))
        {
            _builderOperatorDecisionRows.Add(row);
        }

        var selectedId = _selectedBuilderOperatorDecision?.DecisionId;
        var nextSelected = _builderOperatorDecisionRows.FirstOrDefault(row => string.Equals(row.DecisionId, selectedId, StringComparison.OrdinalIgnoreCase))
                           ?? _builderOperatorDecisionRows.LastOrDefault();
        ApplySelectedBuilderOperatorDecision(nextSelected);
        LoadBuilderExecutionAuditArtifacts(
            artifact,
            BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot()),
            BuilderSimulationAccuracyService.LoadSimulationAccuracy(GetBuilderWorkspaceRepoRoot()),
            BuilderExecutionReadinessService.LoadExecutionReadiness(GetBuilderWorkspaceRepoRoot()));
        NotifyBuilderOperatorDecisionStateChanged();
    }

    private void ResetBuilderOperatorDecisionState()
    {
        _builderOperatorDecisionSummary = "No operator decisions recorded.";
        _builderOperatorDecisionArtifactPath = string.Empty;
        _builderOperatorDecisionRows.Clear();
        ApplySelectedBuilderOperatorDecision(null);
        ResetBuilderExecutionAuditState();
        NotifyBuilderOperatorDecisionStateChanged();
    }

    private void ApplySelectedBuilderOperatorDecision(BuilderOperatorDecisionRow? row)
    {
        _selectedBuilderOperatorDecision = row;
        _builderOperatorDecisionTriggerArtifacts.Clear();
        _builderOperatorDecisionResultArtifacts.Clear();
        if (row is not null)
        {
            foreach (var artifact in row.TriggerArtifacts)
            {
                _builderOperatorDecisionTriggerArtifacts.Add(artifact);
            }

            foreach (var artifact in row.ResultArtifacts)
            {
                _builderOperatorDecisionResultArtifacts.Add(artifact);
            }
        }
    }

    private Task SelectBuilderOperatorDecisionAsync(BuilderOperatorDecisionRow? row)
    {
        ApplySelectedBuilderOperatorDecision(row);
        SyncBuilderExecutionAuditSelection();
        NotifyBuilderExecutionAuditStateChanged();
        NotifyBuilderOperatorDecisionStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderOperatorDecisionArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void RecordBuilderReviewDecision(
        string actionTaken,
        BuilderReviewWorkspaceContext context,
        DateTimeOffset observedUtc)
    {
        var selection = CaptureBuilderDecisionSelection();
        var resultState = ClassifyReviewDecisionOutcome(actionTaken, selection.FailureClass, context.Workspace.ReviewCounts.FinalizeEligibilityState);
        var mappedFailureClass = ResolveReviewFailureClass(context.Workspace.ReviewCounts.FinalizeEligibilityState, selection.FailureClass);
        RecordBuilderOperatorDecision(
            actionTaken,
            selection.TargetRoute,
            context.BatchReviewActions.SessionId,
            resultState,
            IsSuccessfulOutcome(resultState),
            mappedFailureClass,
            new[]
            {
                context.BatchReviewActions.ArtifactPath,
                context.Workspace.ArtifactPath,
                context.NavigationState.ArtifactPath,
                context.EfficiencySummary.ArtifactPath,
                context.Queue.ArtifactPath,
                context.QueueNavigation.ArtifactPath,
                context.HighRiskFlags.ArtifactPath
            },
            observedUtc);
    }

    private void RecordBuilderProofDecision(
        string actionTaken,
        string targetRoute,
        string resultRunId,
        string outcomeClassification,
        bool successFlag,
        string failureClass,
        IReadOnlyList<string> resultArtifacts,
        DateTimeOffset observedUtc)
    {
        var selection = CaptureBuilderDecisionSelection(targetRoute);
        var resultState = ClassifyRouteDecisionOutcome(outcomeClassification, successFlag, selection.FailureClass);
        var mappedFailureClass = successFlag
            ? string.Empty
            : string.IsNullOrWhiteSpace(selection.FailureClass)
                ? failureClass
                : selection.FailureClass;
        RecordBuilderOperatorDecision(
            actionTaken,
            targetRoute,
            resultRunId,
            resultState,
            successFlag,
            mappedFailureClass,
            resultArtifacts,
            observedUtc);
    }

    private void RecordBuilderOperatorDecision(
        string actionTaken,
        string targetRoute,
        string resultRunId,
        string resultState,
        bool successFlag,
        string failureClass,
        IReadOnlyList<string> resultArtifacts,
        DateTimeOffset observedUtc)
    {
        var selection = CaptureBuilderDecisionSelection(targetRoute);
        var artifact = BuilderOperatorDecisionService.RecordDecision(
            GetBuilderWorkspaceRepoRoot(),
            new BuilderOperatorDecisionRequest(
                selection.PlaybookId,
                selection.SimulationId,
                actionTaken,
                selection.TargetRepo,
                string.IsNullOrWhiteSpace(targetRoute) ? selection.TargetRoute : targetRoute,
                selection.TriggerArtifacts,
                resultRunId,
                resultState,
                successFlag,
                failureClass,
                resultArtifacts,
                selection.SimulationScenario,
                selection.PredictedOutcome,
                selection.PredictedOutcomeClass,
                selection.PredictedConfidenceLevel,
                selection.PredictedConfidenceScore,
                selection.ActiveSignalProfileId,
                selection.ProfileOverrideHash,
                selection.CalibrationSnapshotLink,
                selection.PatternEntryId,
                selection.PatternMatchId,
                selection.PatternLibrarySnapshotId,
                selection.PatchCandidateId,
                selection.PatchProvenanceId),
            observedUtc);
        LoadBuilderOperatorDecisionArtifacts(artifact);
        LoadBuilderRecoverySimulationArtifacts();
        RefreshBuilderRecoveryRankingState();
    }

    private BuilderDecisionSelectionContext CaptureBuilderDecisionSelection(string? fallbackRoute = null)
    {
        var repoRoot = GetBuilderWorkspaceRepoRoot();
        var routeResolution = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
        var playbook = _selectedBuilderRecoveryPlaybook?.Playbook;
        var simulation = _selectedBuilderRecoverySimulation?.Simulation;
        var calibration = _builderSignalCalibrationArtifact ?? BuilderSignalCalibrationService.LoadSignalCalibration(repoRoot);
        var patternMatches = BuilderPatternLibraryService.LoadPatternLibraryMatches(repoRoot);
        var patternEntries = BuilderPatternLibraryService.LoadPatternLibraryEntries(repoRoot);
        var selectedPatchCandidate = _selectedBuilderPatternPatchCandidate?.Candidate;
        var selectedPatchProvenance = _selectedBuilderPatternPatchCandidate?.Provenance;
        var attachedPatternEntryId = patternMatches?.AttachedPatternEntryId ?? string.Empty;
        var attachedPatternMatchId = patternMatches?.AttachedPatternMatchId ?? string.Empty;
        var attachedPatternSnapshotId = patternEntries?.Entries.FirstOrDefault(entry =>
            string.Equals(entry.PatternEntryId, attachedPatternEntryId, StringComparison.OrdinalIgnoreCase))?.SourceSnapshotId ?? string.Empty;
        var targetRoute = playbook?.AppliesToRoutes.FirstOrDefault();
        var triggerArtifacts = (playbook?.ArtifactLinks ?? Array.Empty<string>())
            .Concat(simulation?.ArtifactLinks ?? Array.Empty<string>())
            .Concat(new[]
            {
                _builderRecoveryArtifactPath,
                _builderRecoverySimulationArtifactPath,
                _builderAutoSuggestionArtifactPath,
                calibration?.ArtifactPath,
                calibration?.ProfileArtifactPath,
                patternMatches?.ArtifactPath,
                patternEntries?.ArtifactPath
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderDecisionSelectionContext(
            playbook?.PlaybookId ?? string.Empty,
            simulation?.SimulationId ?? string.Empty,
            playbook?.FailureClass ?? string.Empty,
            BuilderWorkspaceService.ResolveWorkspaceId(repoRoot),
            firstNonEmpty(targetRoute, simulation?.TargetRoute, fallbackRoute, routeResolution?.RouteDecision),
            triggerArtifacts,
            simulation?.Scenario ?? string.Empty,
            simulation?.PredictedOutcome ?? string.Empty,
            simulation?.PredictedOutcomeClass ?? string.Empty,
            simulation?.ConfidenceLevel ?? string.Empty,
            simulation?.ConfidenceScore ?? 0d,
            calibration?.ActiveProfileId ?? string.Empty,
            calibration?.ProfileOverrideHash ?? string.Empty,
            calibration?.ArtifactPath ?? string.Empty,
            attachedPatternEntryId,
            attachedPatternMatchId,
            attachedPatternSnapshotId,
            selectedPatchCandidate?.PatchCandidateId ?? string.Empty,
            selectedPatchProvenance?.PatchProvenanceId ?? string.Empty);

        static string firstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }
    }

    private static string ClassifyReviewDecisionOutcome(string actionTaken, string selectedFailureClass, string finalizeState)
    {
        if (string.Equals(finalizeState, "ready_to_finalize", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(finalizeState, "no_changed_files", StringComparison.OrdinalIgnoreCase))
        {
            return "resolved_block";
        }

        if (actionTaken.Contains("approve", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(finalizeState, "blocked_by_rejection", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(finalizeState, "blocked_by_revision_request", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(selectedFailureClass))
        {
            return "failed_same_pattern";
        }

        return "partial_success";
    }

    private static string ResolveReviewFailureClass(string finalizeState, string selectedFailureClass)
        => finalizeState switch
        {
            "blocked_by_rejection" => "patch_rejected",
            "blocked_by_revision_request" => "finalize_blocked",
            "partially_approved" => "review_blocked",
            "pending_review" => "review_blocked",
            _ => string.IsNullOrWhiteSpace(selectedFailureClass) ? string.Empty : selectedFailureClass
        };

    private static string ClassifyRouteDecisionOutcome(string outcomeClassification, bool successFlag, string selectedFailureClass)
    {
        if (successFlag)
        {
            return "success";
        }

        if (!string.IsNullOrWhiteSpace(selectedFailureClass))
        {
            return "failed_same_pattern";
        }

        return outcomeClassification.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               outcomeClassification.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            ? "new_failure_pattern"
            : "partial_success";
    }

    private static bool IsSuccessfulOutcome(string resultState)
        => string.Equals(resultState, "success", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(resultState, "partial_success", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(resultState, "resolved_block", StringComparison.OrdinalIgnoreCase);

    private void NotifyBuilderOperatorDecisionStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderOperatorDecisions));
        OnPropertyChanged(nameof(BuilderOperatorDecisionSummary));
        OnPropertyChanged(nameof(BuilderOperatorDecisionArtifactPath));
        OnPropertyChanged(nameof(HasBuilderOperatorDecisionArtifactPath));
        OnPropertyChanged(nameof(HasSelectedBuilderOperatorDecision));
        OnPropertyChanged(nameof(BuilderOperatorDecisionSelectedTitle));
        OnPropertyChanged(nameof(BuilderOperatorDecisionSelectedSummary));
        OnPropertyChanged(nameof(BuilderOperatorDecisionSelectedContext));
        OnPropertyChanged(nameof(HasBuilderOperatorDecisionSelectedContext));
        OnPropertyChanged(nameof(BuilderOperatorDecisionSelectedOutcome));
        OnPropertyChanged(nameof(HasBuilderOperatorDecisionSelectedOutcome));
        OnPropertyChanged(nameof(HasBuilderOperatorDecisionTriggerArtifacts));
        OnPropertyChanged(nameof(HasBuilderOperatorDecisionResultArtifacts));
        OpenBuilderOperatorDecisionArtifactCommand.RaiseCanExecuteChanged();
    }

    private sealed record BuilderDecisionSelectionContext(
        string PlaybookId,
        string SimulationId,
        string FailureClass,
        string TargetRepo,
        string TargetRoute,
        IReadOnlyList<string> TriggerArtifacts,
        string SimulationScenario,
        string PredictedOutcome,
        string PredictedOutcomeClass,
        string PredictedConfidenceLevel,
        double PredictedConfidenceScore,
        string ActiveSignalProfileId,
        string ProfileOverrideHash,
        string CalibrationSnapshotLink,
        string PatternEntryId,
        string PatternMatchId,
        string PatternLibrarySnapshotId,
        string PatchCandidateId,
        string PatchProvenanceId);
}

public sealed record BuilderOperatorDecisionRow(BuilderOperatorDecisionRecord Decision)
{
    public string DecisionId => Decision.DecisionId;
    public string Header => $"{FormatValue(Decision.ActionTaken)} [{FormatValue(Decision.ResultState)}]";
    public string Summary => Decision.Summary;
    public string ContextSummary => $"Time: {Decision.Timestamp:O}. Repo: {FormatValue(Decision.TargetRepo)}. Route: {FormatValue(Decision.TargetRoute)}. Playbook: {FormatValue(Decision.PlaybookId)}. Simulation: {FormatValue(Decision.SimulationId)}. Run: {FormatValue(Decision.ResultRunId)}. Signal profile: {FormatValue(Decision.ActiveSignalProfileId)} ({FormatValue(Decision.ProfileOverrideHash)}). Pattern reference: {FormatValue(Decision.PatternEntryId)} ({FormatValue(Decision.PatternMatchId)}). Patch candidate: {FormatValue(Decision.PatchCandidateId)} ({FormatValue(Decision.PatchProvenanceId)}).";
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> TriggerArtifacts => Decision.TriggerArtifacts
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ResultArtifacts => Decision.ResultArtifacts
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not recorded" : value.Replace('_', ' ');
}
