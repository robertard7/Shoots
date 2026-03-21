using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderCrossRepoWorkspaceStatusRow> _builderCrossRepoWorkspaceStatuses = new();
    private string _builderCrossRepoPlanSummary = "No cross-repo orchestration plan recorded.";
    private string _builderCrossRepoExecutionSummary = "No cross-repo execution state recorded.";
    private string _builderCrossRepoFinalizeReadiness = "not_recorded";
    private string _builderCrossRepoPlanArtifactPath = string.Empty;
    private string _builderCrossRepoSegmentsArtifactPath = string.Empty;
    private string _builderCrossRepoExecutionArtifactPath = string.Empty;
    private double _builderCrossRepoProgressPercentage;

    public ReadOnlyObservableCollection<BuilderCrossRepoWorkspaceStatusRow> BuilderCrossRepoWorkspaceStatuses { get; private set; } = null!;
    public bool HasBuilderCrossRepoWorkspaceStatuses => _builderCrossRepoWorkspaceStatuses.Count > 0;
    public string BuilderCrossRepoPlanSummary => _builderCrossRepoPlanSummary;
    public bool HasBuilderCrossRepoPlanSummary => !string.IsNullOrWhiteSpace(_builderCrossRepoPlanSummary) &&
                                                  !string.Equals(_builderCrossRepoPlanSummary, "No cross-repo orchestration plan recorded.", StringComparison.Ordinal);
    public string BuilderCrossRepoExecutionSummary => _builderCrossRepoExecutionSummary;
    public bool HasBuilderCrossRepoExecutionSummary => !string.IsNullOrWhiteSpace(_builderCrossRepoExecutionSummary) &&
                                                       !string.Equals(_builderCrossRepoExecutionSummary, "No cross-repo execution state recorded.", StringComparison.Ordinal);
    public string BuilderCrossRepoFinalizeReadiness => _builderCrossRepoFinalizeReadiness;
    public string BuilderCrossRepoFinalizeReadinessBadge => _builderCrossRepoFinalizeReadiness switch
    {
        "blocked_by_rejection" => "Blocked by rejection",
        "blocked_by_revision_request" => "Blocked by revision request",
        "ready_for_independent_finalize" => "Ready for independent finalize",
        "pending_review" => "Pending review",
        _ => "No orchestration"
    };
    public string BuilderCrossRepoPlanArtifactPath => _builderCrossRepoPlanArtifactPath;
    public bool HasBuilderCrossRepoPlanArtifactPath => !string.IsNullOrWhiteSpace(_builderCrossRepoPlanArtifactPath) && File.Exists(_builderCrossRepoPlanArtifactPath);
    public string BuilderCrossRepoSegmentsArtifactPath => _builderCrossRepoSegmentsArtifactPath;
    public bool HasBuilderCrossRepoSegmentsArtifactPath => !string.IsNullOrWhiteSpace(_builderCrossRepoSegmentsArtifactPath) && File.Exists(_builderCrossRepoSegmentsArtifactPath);
    public string BuilderCrossRepoExecutionArtifactPath => _builderCrossRepoExecutionArtifactPath;
    public bool HasBuilderCrossRepoExecutionArtifactPath => !string.IsNullOrWhiteSpace(_builderCrossRepoExecutionArtifactPath) && File.Exists(_builderCrossRepoExecutionArtifactPath);
    public double BuilderCrossRepoProgressPercentage => _builderCrossRepoProgressPercentage;
    public string BuilderCrossRepoProgressSummary => $"Cross-repo progress: {_builderCrossRepoProgressPercentage:0.##}% of workspaces are ready for independent finalize.";

    public AsyncRelayCommand OpenBuilderCrossRepoPlanArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderCrossRepoSegmentsArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderCrossRepoExecutionArtifactCommand { get; private set; } = null!;

    private void InitializeBuilderCrossRepoSurface()
    {
        BuilderCrossRepoWorkspaceStatuses = new ReadOnlyObservableCollection<BuilderCrossRepoWorkspaceStatusRow>(_builderCrossRepoWorkspaceStatuses);
        OpenBuilderCrossRepoPlanArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderCrossRepoPlanArtifactPath), () => HasBuilderCrossRepoPlanArtifactPath);
        OpenBuilderCrossRepoSegmentsArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderCrossRepoSegmentsArtifactPath), () => HasBuilderCrossRepoSegmentsArtifactPath);
        OpenBuilderCrossRepoExecutionArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderCrossRepoExecutionArtifactPath), () => HasBuilderCrossRepoExecutionArtifactPath);
    }

    private void LoadBuilderCrossRepoArtifacts()
    {
        if (_builderWorkspaceOptions.Count == 0)
        {
            ResetBuilderCrossRepoState();
            ResetBuilderKnowledgeState();
            ResetBuilderRouteIntelligenceState();
            ResetBuilderRecoveryState();
            ResetBuilderOperatorDecisionState();
            return;
        }

        var descriptors = _builderWorkspaceOptions
            .Select(option => BuilderWorkspaceService.CreateDescriptor(option.RepoRoot, option.RepoName))
            .ToArray();
        var requestId = ResolveBuilderCrossRepoRequestId();
        var context = BuilderCrossRepoOrchestrationService.RefreshOrchestrationArtifacts(
            descriptors,
            _selectedBuilderWorkspaceId,
            requestId);
        if (context is null)
        {
            ResetBuilderCrossRepoState();
            ResetBuilderKnowledgeState();
            ResetBuilderRouteIntelligenceState();
            ResetBuilderRecoveryState();
            ResetBuilderOperatorDecisionState();
            return;
        }

        ApplyBuilderCrossRepoContext(context);
        LoadBuilderKnowledgeArtifacts(context);
        LoadBuilderRouteIntelligenceArtifacts(context);
        LoadBuilderRecoveryArtifacts(context);
        LoadBuilderOperatorDecisionArtifacts();
        NotifyBuilderCrossRepoStateChanged();
    }

    private void ApplyBuilderCrossRepoContext(BuilderCrossRepoOrchestrationContext context)
    {
        _builderCrossRepoPlanSummary = context.Plan.Summary;
        _builderCrossRepoExecutionSummary = context.ExecutionState.Summary;
        _builderCrossRepoFinalizeReadiness = context.ExecutionState.FinalizeReadiness;
        _builderCrossRepoPlanArtifactPath = context.Plan.ArtifactPath;
        _builderCrossRepoSegmentsArtifactPath = context.Segments.ArtifactPath;
        _builderCrossRepoExecutionArtifactPath = context.ExecutionState.ArtifactPath;

        _builderCrossRepoWorkspaceStatuses.Clear();
        foreach (var status in context.ExecutionState.WorkspaceStatusList)
        {
            var segment = context.Segments.Segments.FirstOrDefault(entry =>
                string.Equals(entry.WorkspaceId, status.WorkspaceId, StringComparison.OrdinalIgnoreCase));
            _builderCrossRepoWorkspaceStatuses.Add(new BuilderCrossRepoWorkspaceStatusRow(
                status.WorkspaceId,
                status.RepoName,
                status.RepoRoot,
                segment?.TaskDescription ?? status.Summary,
                status.RouteDecision,
                status.ModelTier,
                status.ExecutionState,
                status.ReviewState,
                status.FinalizeReadiness,
                status.PendingReviews,
                status.ChangedFiles,
                status.RejectedSegment,
                status.Finalized,
                string.Equals(status.WorkspaceId, _selectedBuilderWorkspaceId, StringComparison.OrdinalIgnoreCase)));
        }

        _builderCrossRepoProgressPercentage = _builderCrossRepoWorkspaceStatuses.Count == 0
            ? 0d
            : Math.Round(
                _builderCrossRepoWorkspaceStatuses.Count(status =>
                    status.IsFinalized ||
                    string.Equals(status.FinalizeReadiness, "ready_to_finalize", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status.FinalizeReadiness, "no_changed_files", StringComparison.OrdinalIgnoreCase)) * 100d /
                _builderCrossRepoWorkspaceStatuses.Count,
                2);
    }

    private void ResetBuilderCrossRepoState()
    {
        _builderCrossRepoPlanSummary = "No cross-repo orchestration plan recorded.";
        _builderCrossRepoExecutionSummary = "No cross-repo execution state recorded.";
        _builderCrossRepoFinalizeReadiness = "not_recorded";
        _builderCrossRepoPlanArtifactPath = string.Empty;
        _builderCrossRepoSegmentsArtifactPath = string.Empty;
        _builderCrossRepoExecutionArtifactPath = string.Empty;
        _builderCrossRepoProgressPercentage = 0d;
        _builderCrossRepoWorkspaceStatuses.Clear();
        NotifyBuilderCrossRepoStateChanged();
    }

    private void NotifyBuilderCrossRepoStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderCrossRepoWorkspaceStatuses));
        OnPropertyChanged(nameof(BuilderCrossRepoPlanSummary));
        OnPropertyChanged(nameof(HasBuilderCrossRepoPlanSummary));
        OnPropertyChanged(nameof(BuilderCrossRepoExecutionSummary));
        OnPropertyChanged(nameof(HasBuilderCrossRepoExecutionSummary));
        OnPropertyChanged(nameof(BuilderCrossRepoFinalizeReadiness));
        OnPropertyChanged(nameof(BuilderCrossRepoFinalizeReadinessBadge));
        OnPropertyChanged(nameof(BuilderCrossRepoPlanArtifactPath));
        OnPropertyChanged(nameof(HasBuilderCrossRepoPlanArtifactPath));
        OnPropertyChanged(nameof(BuilderCrossRepoSegmentsArtifactPath));
        OnPropertyChanged(nameof(HasBuilderCrossRepoSegmentsArtifactPath));
        OnPropertyChanged(nameof(BuilderCrossRepoExecutionArtifactPath));
        OnPropertyChanged(nameof(HasBuilderCrossRepoExecutionArtifactPath));
        OnPropertyChanged(nameof(BuilderCrossRepoProgressPercentage));
        OnPropertyChanged(nameof(BuilderCrossRepoProgressSummary));
        OpenBuilderCrossRepoPlanArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderCrossRepoSegmentsArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderCrossRepoExecutionArtifactCommand.RaiseCanExecuteChanged();
    }

    private string ResolveBuilderCrossRepoRequestId()
    {
        var repoRoot = GetBuilderWorkspaceRepoRoot();
        var routeResolution = BuilderWorkspaceService.LoadRouteResolution(repoRoot);
        if (!string.IsNullOrWhiteSpace(routeResolution?.RequestId))
        {
            return routeResolution.RequestId;
        }

        return string.IsNullOrWhiteSpace(_selectedBuilderWorkspaceId)
            ? "cross_repo_task"
            : $"cross_repo::{_selectedBuilderWorkspaceId}";
    }
}

public sealed record BuilderCrossRepoWorkspaceStatusRow(
    string WorkspaceId,
    string RepoName,
    string RepoRoot,
    string TaskDescription,
    string RouteDecision,
    string ModelTier,
    string ExecutionState,
    string ReviewState,
    string FinalizeReadiness,
    int PendingReviews,
    int ChangedFiles,
    bool IsRejectedBlocker,
    bool IsFinalized,
    bool IsSelectedWorkspace)
{
    public string Header => IsSelectedWorkspace
        ? $"{RepoName} ({WorkspaceId}) [selected]"
        : $"{RepoName} ({WorkspaceId})";

    public string StatusSummary => $"Execution: {FormatState(ExecutionState)}. Review: {FormatState(ReviewState)}. Finalize: {FormatState(FinalizeReadiness)}. Pending reviews: {PendingReviews}. Changed files: {ChangedFiles}. Route: {RouteDecision}. Model tier: {FormatState(ModelTier)}.";

    private static string FormatState(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Replace('_', ' ');
}
