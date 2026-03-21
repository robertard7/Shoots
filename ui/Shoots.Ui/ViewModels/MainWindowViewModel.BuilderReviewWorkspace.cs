using System.IO;
using System.Text;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly IReadOnlyList<BuilderReviewOptionRow> BuilderReviewFilterOptionRows = new[]
    {
        new BuilderReviewOptionRow("all", "All files"),
        new BuilderReviewOptionRow("pending_only", "Pending only"),
        new BuilderReviewOptionRow("approved_only", "Approved only"),
        new BuilderReviewOptionRow("rejected_only", "Rejected only"),
        new BuilderReviewOptionRow("needs_revision_only", "Needs revision only"),
        new BuilderReviewOptionRow("created_only", "Created only"),
        new BuilderReviewOptionRow("modified_only", "Modified only"),
        new BuilderReviewOptionRow("deleted_only", "Deleted only")
    };

    private static readonly IReadOnlyList<BuilderReviewOptionRow> BuilderReviewGroupingOptionRows = new[]
    {
        new BuilderReviewOptionRow("directory", "Directory"),
        new BuilderReviewOptionRow("change_type", "Change type"),
        new BuilderReviewOptionRow("file_category", "File category"),
        new BuilderReviewOptionRow("review_state", "Review state")
    };

    private readonly ObservableCollection<BuilderReviewGroupRow> _builderReviewGroups = new();
    private BuilderReviewFileRow[] _builderReviewFileRows = Array.Empty<BuilderReviewFileRow>();
    private BuilderReviewFileRow? _builderReviewCurrentFile;
    private string _builderReviewWorkspaceSummary = "No builder review workspace recorded.";
    private string _builderReviewCountsSummary = "No builder review counts recorded.";
    private string _builderReviewNavigationSummary = "No builder review navigation state recorded.";
    private string _builderReviewQueueSummary = "No builder review queue recorded.";
    private string _builderReviewHighRiskSummary = "No high-risk review flags recorded.";
    private string _builderReviewBatchActionSummary = "No builder batch review actions recorded.";
    private string _builderReviewFinalizeState = "not_recorded";
    private string _builderReviewWorkspaceArtifactPath = string.Empty;
    private string _builderReviewNavigationArtifactPath = string.Empty;
    private string _builderReviewEfficiencyArtifactPath = string.Empty;
    private string _builderReviewHistoryArtifactPath = string.Empty;
    private string _builderReviewQueueArtifactPath = string.Empty;
    private string _builderReviewQueueNavigationArtifactPath = string.Empty;
    private string _builderReviewHighRiskArtifactPath = string.Empty;
    private string _builderReviewBatchActionsArtifactPath = string.Empty;
    private string _selectedBuilderReviewFilter = "all";
    private string _selectedBuilderReviewGrouping = "directory";
    private string _builderReviewCurrentFilePath = string.Empty;
    private string _builderReviewHighestPriorityFilePath = string.Empty;
    private string _builderReviewNextPendingFilePath = string.Empty;
    private string _builderReviewNextRejectedFilePath = string.Empty;
    private string _builderReviewNextRevisionFilePath = string.Empty;
    private string _builderReviewNextHighRiskFilePath = string.Empty;
    private double _builderReviewProgressPercentage;
    private bool _isBuilderReviewContextExpanded = true;
    private bool _isRefreshingBuilderReviewWorkspace;

    public ReadOnlyObservableCollection<BuilderReviewGroupRow> BuilderReviewGroups { get; private set; } = null!;
    public IReadOnlyList<BuilderReviewOptionRow> BuilderReviewFilterOptions => BuilderReviewFilterOptionRows;
    public IReadOnlyList<BuilderReviewOptionRow> BuilderReviewGroupingOptions => BuilderReviewGroupingOptionRows;

    public string SelectedBuilderReviewFilter
    {
        get => _selectedBuilderReviewFilter;
        set
        {
            var normalized = BuilderReviewWorkspaceService.NormalizeFilter(value);
            if (_selectedBuilderReviewFilter == normalized)
                return;

            _selectedBuilderReviewFilter = normalized;
            OnPropertyChanged(nameof(SelectedBuilderReviewFilter));
            LoadBuilderReviewWorkspaceArtifacts();
        }
    }

    public string SelectedBuilderReviewGrouping
    {
        get => _selectedBuilderReviewGrouping;
        set
        {
            var normalized = BuilderReviewWorkspaceService.NormalizeGrouping(value);
            if (_selectedBuilderReviewGrouping == normalized)
                return;

            _selectedBuilderReviewGrouping = normalized;
            OnPropertyChanged(nameof(SelectedBuilderReviewGrouping));
            LoadBuilderReviewWorkspaceArtifacts();
        }
    }

    public string BuilderReviewWorkspaceSummary => _builderReviewWorkspaceSummary;
    public bool HasBuilderReviewWorkspaceSummary => !string.IsNullOrWhiteSpace(_builderReviewWorkspaceSummary) &&
                                                    !string.Equals(_builderReviewWorkspaceSummary, "No builder review workspace recorded.", StringComparison.Ordinal);
    public string BuilderReviewCountsSummary => _builderReviewCountsSummary;
    public bool HasBuilderReviewCountsSummary => !string.IsNullOrWhiteSpace(_builderReviewCountsSummary) &&
                                                 !string.Equals(_builderReviewCountsSummary, "No builder review counts recorded.", StringComparison.Ordinal);
    public string BuilderReviewNavigationSummary => _builderReviewNavigationSummary;
    public bool HasBuilderReviewNavigationSummary => !string.IsNullOrWhiteSpace(_builderReviewNavigationSummary) &&
                                                     !string.Equals(_builderReviewNavigationSummary, "No builder review navigation state recorded.", StringComparison.Ordinal);
    public string BuilderReviewQueueSummary => _builderReviewQueueSummary;
    public bool HasBuilderReviewQueueSummary => !string.IsNullOrWhiteSpace(_builderReviewQueueSummary) &&
                                                !string.Equals(_builderReviewQueueSummary, "No builder review queue recorded.", StringComparison.Ordinal);
    public string BuilderReviewHighRiskSummary => _builderReviewHighRiskSummary;
    public bool HasBuilderReviewHighRiskSummary => !string.IsNullOrWhiteSpace(_builderReviewHighRiskSummary) &&
                                                   !string.Equals(_builderReviewHighRiskSummary, "No high-risk review flags recorded.", StringComparison.Ordinal);
    public string BuilderReviewBatchActionSummary => _builderReviewBatchActionSummary;
    public bool HasBuilderReviewBatchActionSummary => !string.IsNullOrWhiteSpace(_builderReviewBatchActionSummary) &&
                                                      !string.Equals(_builderReviewBatchActionSummary, "No builder batch review actions recorded.", StringComparison.Ordinal);
    public string BuilderReviewFinalizeState => _builderReviewFinalizeState;
    public string BuilderReviewFinalizeBadge => _builderReviewFinalizeState switch
    {
        "pending_review" => "Pending review",
        "partially_approved" => "Partially approved",
        "blocked_by_rejection" => "Blocked by rejection",
        "blocked_by_revision_request" => "Blocked by revision request",
        "ready_to_finalize" => "Ready to finalize",
        "no_changed_files" => "No changed files",
        _ => "No review workspace"
    };
    public bool HasBuilderReviewGroups => _builderReviewGroups.Count > 0;
    public string BuilderReviewWorkspaceArtifactPath => _builderReviewWorkspaceArtifactPath;
    public bool HasBuilderReviewWorkspaceArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewWorkspaceArtifactPath) && File.Exists(_builderReviewWorkspaceArtifactPath);
    public string BuilderReviewNavigationArtifactPath => _builderReviewNavigationArtifactPath;
    public bool HasBuilderReviewNavigationArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewNavigationArtifactPath) && File.Exists(_builderReviewNavigationArtifactPath);
    public string BuilderReviewEfficiencyArtifactPath => _builderReviewEfficiencyArtifactPath;
    public bool HasBuilderReviewEfficiencyArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewEfficiencyArtifactPath) && File.Exists(_builderReviewEfficiencyArtifactPath);
    public string BuilderReviewHistoryArtifactPath => _builderReviewHistoryArtifactPath;
    public bool HasBuilderReviewHistoryArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewHistoryArtifactPath) && File.Exists(_builderReviewHistoryArtifactPath);
    public string BuilderReviewQueueArtifactPath => _builderReviewQueueArtifactPath;
    public bool HasBuilderReviewQueueArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewQueueArtifactPath) && File.Exists(_builderReviewQueueArtifactPath);
    public string BuilderReviewQueueNavigationArtifactPath => _builderReviewQueueNavigationArtifactPath;
    public bool HasBuilderReviewQueueNavigationArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewQueueNavigationArtifactPath) && File.Exists(_builderReviewQueueNavigationArtifactPath);
    public string BuilderReviewHighRiskArtifactPath => _builderReviewHighRiskArtifactPath;
    public bool HasBuilderReviewHighRiskArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewHighRiskArtifactPath) && File.Exists(_builderReviewHighRiskArtifactPath);
    public string BuilderReviewBatchActionsArtifactPath => _builderReviewBatchActionsArtifactPath;
    public bool HasBuilderReviewBatchActionsArtifactPath => !string.IsNullOrWhiteSpace(_builderReviewBatchActionsArtifactPath) && File.Exists(_builderReviewBatchActionsArtifactPath);
    public double BuilderReviewProgressPercentage => _builderReviewProgressPercentage;
    public string BuilderReviewProgressSummary => $"Review progress: {_builderReviewProgressPercentage:0.##}% complete.";
    public bool HasBuilderReviewCurrentFile => _builderReviewCurrentFile is not null;
    public string BuilderReviewCurrentFileHeader => _builderReviewCurrentFile?.FileHeader ?? "No file selected.";
    public string BuilderReviewCurrentFileApprovalBadge => _builderReviewCurrentFile?.ApprovalBadge ?? "No review state";
    public string BuilderReviewCurrentFilePriorityBadge => _builderReviewCurrentFile?.PriorityBadge ?? "No priority";
    public string BuilderReviewCurrentFileQueueBadge => _builderReviewCurrentFile?.QueueBadge ?? "Queue not recorded";
    public string BuilderReviewCurrentFileContextSummary => _builderReviewCurrentFile?.ContextSummary ?? "No file selected.";
    public string BuilderReviewCurrentFileDiffSummary => _builderReviewCurrentFile?.DiffSummary ?? string.Empty;
    public bool HasBuilderReviewCurrentFileDiffSummary => !string.IsNullOrWhiteSpace(BuilderReviewCurrentFileDiffSummary);
    public string BuilderReviewCurrentFileDiffExcerpt => _builderReviewCurrentFile?.BoundedDiffExcerpt ?? "No bounded diff excerpt recorded.";
    public bool HasBuilderReviewCurrentFileRejectionReason => _builderReviewCurrentFile?.HasRejectionReason == true;
    public string BuilderReviewCurrentFileRejectionReason => _builderReviewCurrentFile?.RejectionReason ?? string.Empty;
    public bool HasBuilderReviewCurrentFileRelatedTestPath => _builderReviewCurrentFile?.HasRelatedTestFile == true;
    public string BuilderReviewCurrentFileRelatedTestPath => _builderReviewCurrentFile?.RelatedTestFilePath ?? string.Empty;
    public bool HasBuilderReviewCurrentFileHighRisk => _builderReviewCurrentFile?.IsHighRisk == true;
    public string BuilderReviewCurrentFileHighRiskBadge => _builderReviewCurrentFile?.HighRiskBadge ?? string.Empty;
    public string BuilderReviewCurrentGroupLabel => _builderReviewGroups.FirstOrDefault(row => row.IsCurrentGroup)?.GroupLabel ?? string.Empty;

    public bool IsBuilderReviewContextExpanded
    {
        get => _isBuilderReviewContextExpanded;
        set
        {
            if (_isBuilderReviewContextExpanded == value)
                return;

            _isBuilderReviewContextExpanded = value;
            OnPropertyChanged(nameof(IsBuilderReviewContextExpanded));
        }
    }

    public AsyncRelayCommand OpenBuilderReviewWorkspaceArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderReviewQueueArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand CopyBuilderCurrentReviewSummaryCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderReviewFileRow> SelectBuilderReviewFileCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderReviewGroupRow> SelectFirstBuilderReviewFileInGroupCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectNextBuilderReviewPendingFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectNextBuilderReviewRejectedFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectNextBuilderReviewRevisionFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectNextBuilderReviewHighRiskFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectHighestPriorityBuilderReviewFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectPreviousBuilderReviewFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectFirstBuilderReviewRejectedFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectFirstBuilderReviewPendingFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SelectFirstBuilderReviewGroupFileCommand { get; private set; } = null!;
    public AsyncRelayCommand ApprovePendingBuilderReviewGroupCommand { get; private set; } = null!;
    public AsyncRelayCommand ApprovePendingBuilderReviewDirectoryCommand { get; private set; } = null!;
    public AsyncRelayCommand ApprovePendingBuilderReviewFilterCommand { get; private set; } = null!;
    public AsyncRelayCommand RejectBuilderReviewGroupCommand { get; private set; } = null!;
    public AsyncRelayCommand RequestBuilderReviewGroupRevisionCommand { get; private set; } = null!;

    private void InitializeBuilderReviewWorkspaceSurface()
    {
        BuilderReviewGroups = new ReadOnlyObservableCollection<BuilderReviewGroupRow>(_builderReviewGroups);
        OpenBuilderReviewWorkspaceArtifactCommand = new AsyncRelayCommand(OpenBuilderReviewWorkspaceArtifactAsync, () => HasBuilderReviewWorkspaceArtifactPath);
        OpenBuilderReviewQueueArtifactCommand = new AsyncRelayCommand(OpenBuilderReviewQueueArtifactAsync, () => HasBuilderReviewQueueArtifactPath);
        CopyBuilderCurrentReviewSummaryCommand = new AsyncRelayCommand(CopyBuilderCurrentReviewSummaryAsync, () => HasBuilderReviewCurrentFile);
        SelectBuilderReviewFileCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderReviewFileRow>(SelectBuilderReviewFileAsync, row => row is not null);
        SelectFirstBuilderReviewFileInGroupCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderReviewGroupRow>(SelectFirstBuilderReviewFileInGroupAsync, row => row is { Files.Count: > 0 });
        SelectNextBuilderReviewPendingFileCommand = new AsyncRelayCommand(SelectNextBuilderReviewPendingFileAsync, CanSelectNextBuilderReviewPendingFile);
        SelectNextBuilderReviewRejectedFileCommand = new AsyncRelayCommand(SelectNextBuilderReviewRejectedFileAsync, CanSelectNextBuilderReviewRejectedFile);
        SelectNextBuilderReviewRevisionFileCommand = new AsyncRelayCommand(SelectNextBuilderReviewRevisionFileAsync, CanSelectNextBuilderReviewRevisionFile);
        SelectNextBuilderReviewHighRiskFileCommand = new AsyncRelayCommand(SelectNextBuilderReviewHighRiskFileAsync, CanSelectNextBuilderReviewHighRiskFile);
        SelectHighestPriorityBuilderReviewFileCommand = new AsyncRelayCommand(SelectHighestPriorityBuilderReviewFileAsync, CanSelectHighestPriorityBuilderReviewFile);
        SelectPreviousBuilderReviewFileCommand = new AsyncRelayCommand(SelectPreviousBuilderReviewFileAsync, CanSelectPreviousBuilderReviewFile);
        SelectFirstBuilderReviewRejectedFileCommand = new AsyncRelayCommand(SelectFirstBuilderReviewRejectedFileAsync, CanSelectFirstBuilderReviewRejectedFile);
        SelectFirstBuilderReviewPendingFileCommand = new AsyncRelayCommand(SelectFirstBuilderReviewPendingFileAsync, CanSelectFirstBuilderReviewPendingFile);
        SelectFirstBuilderReviewGroupFileCommand = new AsyncRelayCommand(SelectFirstBuilderReviewGroupFileAsync, CanSelectFirstBuilderReviewGroupFile);
        ApprovePendingBuilderReviewGroupCommand = new AsyncRelayCommand(ApprovePendingBuilderReviewGroupAsync, CanApplyBuilderReviewBatchAction);
        ApprovePendingBuilderReviewDirectoryCommand = new AsyncRelayCommand(ApprovePendingBuilderReviewDirectoryAsync, CanApplyBuilderReviewBatchAction);
        ApprovePendingBuilderReviewFilterCommand = new AsyncRelayCommand(ApprovePendingBuilderReviewFilterAsync, CanApplyBuilderReviewBatchAction);
        RejectBuilderReviewGroupCommand = new AsyncRelayCommand(RejectBuilderReviewGroupAsync, CanApplyBuilderReviewBatchAction);
        RequestBuilderReviewGroupRevisionCommand = new AsyncRelayCommand(RequestBuilderReviewGroupRevisionAsync, CanApplyBuilderReviewBatchAction);
    }

    private void LoadBuilderReviewWorkspaceArtifacts()
    {
        if (_isRefreshingBuilderReviewWorkspace)
            return;

        var repoRoot = GetBuilderWorkspaceRepoRoot();
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            ResetBuilderReviewWorkspaceState();
            return;
        }

        _isRefreshingBuilderReviewWorkspace = true;
        try
        {
            var context = BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoRoot,
                new BuilderReviewWorkspacePreferences(_selectedBuilderReviewFilter, _selectedBuilderReviewGrouping, _builderReviewCurrentFilePath));
            if (context is null)
            {
                ResetBuilderReviewWorkspaceState();
                return;
            }

            ApplyBuilderReviewWorkspaceContext(context);
        }
        finally
        {
            _isRefreshingBuilderReviewWorkspace = false;
        }

        NotifyBuilderReviewWorkspaceChanged();
    }

    private void ApplyBuilderReviewWorkspaceContext(BuilderReviewWorkspaceContext context)
    {
        _selectedBuilderReviewFilter = context.NavigationState.CurrentFilter;
        _selectedBuilderReviewGrouping = context.Workspace.GroupingUsed;
        _builderReviewCurrentFilePath = context.NavigationState.CurrentFilePath;
        _builderReviewWorkspaceSummary = context.Workspace.Summary;
        _builderReviewCountsSummary = BuildBuilderReviewCountsSummary(context.Workspace.ReviewCounts, context.EfficiencySummary, context.Queue);
        _builderReviewNavigationSummary = BuildBuilderReviewNavigationSummary(context.NavigationState, context.QueueNavigation);
        _builderReviewQueueSummary = context.Queue.Summary;
        _builderReviewHighRiskSummary = context.HighRiskFlags.Summary;
        _builderReviewBatchActionSummary = context.BatchReviewActions.Summary;
        _builderReviewFinalizeState = context.Workspace.ReviewCounts.FinalizeEligibilityState;
        _builderReviewWorkspaceArtifactPath = context.Workspace.ArtifactPath;
        _builderReviewNavigationArtifactPath = context.NavigationState.ArtifactPath;
        _builderReviewEfficiencyArtifactPath = context.EfficiencySummary.ArtifactPath;
        _builderReviewHistoryArtifactPath = context.WorkspaceHistory.ArtifactPath;
        _builderReviewQueueArtifactPath = context.Queue.ArtifactPath;
        _builderReviewQueueNavigationArtifactPath = context.QueueNavigation.ArtifactPath;
        _builderReviewHighRiskArtifactPath = context.HighRiskFlags.ArtifactPath;
        _builderReviewBatchActionsArtifactPath = context.BatchReviewActions.ArtifactPath;
        _builderReviewHighestPriorityFilePath = context.QueueNavigation.NextPriorityFile;
        _builderReviewNextPendingFilePath = context.QueueNavigation.NextPendingFile;
        _builderReviewNextRejectedFilePath = context.QueueNavigation.NextRejectedFile;
        _builderReviewNextRevisionFilePath = context.QueueNavigation.NextRevisionFile;
        _builderReviewNextHighRiskFilePath = context.QueueNavigation.NextHighRiskFile;
        _builderReviewProgressPercentage = context.EfficiencySummary.ApprovalCompletionPercentage;

        RebuildBuilderReviewRows(context.Workspace, context.NavigationState, context.Queue);
    }

    private void RebuildBuilderReviewRows(
        BuilderReviewWorkspaceRecord workspace,
        BuilderReviewNavigationStateRecord navigation,
        BuilderReviewQueueRecord queue)
    {
        _builderReviewGroups.Clear();
        var queuePositionByPath = queue.QueueOrder
            .Select((path, index) => new { path, index })
            .ToDictionary(entry => entry.path, entry => entry.index + 1, StringComparer.OrdinalIgnoreCase);
        var fileRows = new List<BuilderReviewFileRow>();
        foreach (var group in workspace.FileGroups)
        {
            var rows = group.Files
                .Select(file => new BuilderReviewFileRow(
                    file.RelativePath,
                    file.DirectoryPath,
                    file.FileHeader,
                    file.ChangeKind,
                    file.ChangeSummary,
                    file.DiffSummary,
                    file.BoundedDiffExcerpt,
                    file.ApprovalState,
                    file.RejectionReason,
                    file.RelatedTestFilePath,
                    file.ContextSummary,
                    file.HighRiskCategory,
                    file.RequiresExplicitApproval,
                    file.PriorityRank,
                    file.PriorityLabel,
                    group.GroupKey,
                    group.GroupLabel,
                    queuePositionByPath.TryGetValue(file.RelativePath, out var position) ? position : 0,
                    string.Equals(file.RelativePath, navigation.CurrentFilePath, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            fileRows.AddRange(rows);
            _builderReviewGroups.Add(new BuilderReviewGroupRow(
                group.GroupKey,
                group.GroupLabel,
                group.TotalFiles,
                group.PendingFiles,
                group.ApprovedFiles,
                group.RejectedFiles,
                group.NeedsRevisionFiles,
                group.HighRiskFiles,
                string.Equals(group.GroupKey, navigation.CurrentGroup, StringComparison.OrdinalIgnoreCase),
                rows));
        }

        _builderReviewFileRows = fileRows
            .OrderBy(row => row.QueuePosition == 0 ? int.MaxValue : row.QueuePosition)
            .ThenBy(row => row.RelativePath, StringComparer.Ordinal)
            .ToArray();
        _builderReviewCurrentFile = _builderReviewFileRows.FirstOrDefault(row =>
            string.Equals(row.RelativePath, navigation.CurrentFilePath, StringComparison.OrdinalIgnoreCase));
    }

    private void ResetBuilderReviewWorkspaceState()
    {
        _builderReviewWorkspaceSummary = "No builder review workspace recorded.";
        _builderReviewCountsSummary = "No builder review counts recorded.";
        _builderReviewNavigationSummary = "No builder review navigation state recorded.";
        _builderReviewQueueSummary = "No builder review queue recorded.";
        _builderReviewHighRiskSummary = "No high-risk review flags recorded.";
        _builderReviewBatchActionSummary = "No builder batch review actions recorded.";
        _builderReviewFinalizeState = "not_recorded";
        _builderReviewWorkspaceArtifactPath = string.Empty;
        _builderReviewNavigationArtifactPath = string.Empty;
        _builderReviewEfficiencyArtifactPath = string.Empty;
        _builderReviewHistoryArtifactPath = string.Empty;
        _builderReviewQueueArtifactPath = string.Empty;
        _builderReviewQueueNavigationArtifactPath = string.Empty;
        _builderReviewHighRiskArtifactPath = string.Empty;
        _builderReviewBatchActionsArtifactPath = string.Empty;
        _builderReviewCurrentFilePath = string.Empty;
        _builderReviewHighestPriorityFilePath = string.Empty;
        _builderReviewNextPendingFilePath = string.Empty;
        _builderReviewNextRejectedFilePath = string.Empty;
        _builderReviewNextRevisionFilePath = string.Empty;
        _builderReviewNextHighRiskFilePath = string.Empty;
        _builderReviewProgressPercentage = 0d;
        _builderReviewCurrentFile = null;
        _builderReviewFileRows = Array.Empty<BuilderReviewFileRow>();
        _builderReviewGroups.Clear();
        NotifyBuilderReviewWorkspaceChanged();
    }

    private void NotifyBuilderReviewWorkspaceChanged()
    {
        OnPropertyChanged(nameof(SelectedBuilderReviewFilter));
        OnPropertyChanged(nameof(SelectedBuilderReviewGrouping));
        OnPropertyChanged(nameof(BuilderReviewWorkspaceSummary));
        OnPropertyChanged(nameof(HasBuilderReviewWorkspaceSummary));
        OnPropertyChanged(nameof(BuilderReviewCountsSummary));
        OnPropertyChanged(nameof(HasBuilderReviewCountsSummary));
        OnPropertyChanged(nameof(BuilderReviewNavigationSummary));
        OnPropertyChanged(nameof(HasBuilderReviewNavigationSummary));
        OnPropertyChanged(nameof(BuilderReviewQueueSummary));
        OnPropertyChanged(nameof(HasBuilderReviewQueueSummary));
        OnPropertyChanged(nameof(BuilderReviewHighRiskSummary));
        OnPropertyChanged(nameof(HasBuilderReviewHighRiskSummary));
        OnPropertyChanged(nameof(BuilderReviewBatchActionSummary));
        OnPropertyChanged(nameof(HasBuilderReviewBatchActionSummary));
        OnPropertyChanged(nameof(BuilderReviewFinalizeState));
        OnPropertyChanged(nameof(BuilderReviewFinalizeBadge));
        OnPropertyChanged(nameof(HasBuilderReviewGroups));
        OnPropertyChanged(nameof(BuilderReviewWorkspaceArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewWorkspaceArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewNavigationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewNavigationArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewEfficiencyArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewEfficiencyArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewHistoryArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewHistoryArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewQueueArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewQueueArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewQueueNavigationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewQueueNavigationArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewHighRiskArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewHighRiskArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewBatchActionsArtifactPath));
        OnPropertyChanged(nameof(HasBuilderReviewBatchActionsArtifactPath));
        OnPropertyChanged(nameof(BuilderReviewProgressPercentage));
        OnPropertyChanged(nameof(BuilderReviewProgressSummary));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileHeader));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileApprovalBadge));
        OnPropertyChanged(nameof(BuilderReviewCurrentFilePriorityBadge));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileQueueBadge));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileContextSummary));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileDiffSummary));
        OnPropertyChanged(nameof(HasBuilderReviewCurrentFileDiffSummary));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileDiffExcerpt));
        OnPropertyChanged(nameof(HasBuilderReviewCurrentFile));
        OnPropertyChanged(nameof(HasBuilderReviewCurrentFileRejectionReason));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileRejectionReason));
        OnPropertyChanged(nameof(HasBuilderReviewCurrentFileRelatedTestPath));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileRelatedTestPath));
        OnPropertyChanged(nameof(HasBuilderReviewCurrentFileHighRisk));
        OnPropertyChanged(nameof(BuilderReviewCurrentFileHighRiskBadge));
        OnPropertyChanged(nameof(BuilderReviewCurrentGroupLabel));
        OpenBuilderReviewWorkspaceArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderReviewQueueArtifactCommand.RaiseCanExecuteChanged();
        CopyBuilderCurrentReviewSummaryCommand.RaiseCanExecuteChanged();
        SelectBuilderReviewFileCommand.NotifyCanExecuteChanged();
        SelectFirstBuilderReviewFileInGroupCommand.NotifyCanExecuteChanged();
        SelectNextBuilderReviewPendingFileCommand.RaiseCanExecuteChanged();
        SelectNextBuilderReviewRejectedFileCommand.RaiseCanExecuteChanged();
        SelectNextBuilderReviewRevisionFileCommand.RaiseCanExecuteChanged();
        SelectNextBuilderReviewHighRiskFileCommand.RaiseCanExecuteChanged();
        SelectHighestPriorityBuilderReviewFileCommand.RaiseCanExecuteChanged();
        SelectPreviousBuilderReviewFileCommand.RaiseCanExecuteChanged();
        SelectFirstBuilderReviewRejectedFileCommand.RaiseCanExecuteChanged();
        SelectFirstBuilderReviewPendingFileCommand.RaiseCanExecuteChanged();
        SelectFirstBuilderReviewGroupFileCommand.RaiseCanExecuteChanged();
        ApprovePendingBuilderReviewGroupCommand.RaiseCanExecuteChanged();
        ApprovePendingBuilderReviewDirectoryCommand.RaiseCanExecuteChanged();
        ApprovePendingBuilderReviewFilterCommand.RaiseCanExecuteChanged();
        RejectBuilderReviewGroupCommand.RaiseCanExecuteChanged();
        RequestBuilderReviewGroupRevisionCommand.RaiseCanExecuteChanged();
    }

    private bool CanSelectNextBuilderReviewPendingFile()
        => !string.IsNullOrWhiteSpace(_builderReviewNextPendingFilePath);

    private bool CanSelectNextBuilderReviewRejectedFile()
        => !string.IsNullOrWhiteSpace(_builderReviewNextRejectedFilePath);

    private bool CanSelectNextBuilderReviewRevisionFile()
        => !string.IsNullOrWhiteSpace(_builderReviewNextRevisionFilePath);

    private bool CanSelectNextBuilderReviewHighRiskFile()
        => !string.IsNullOrWhiteSpace(_builderReviewNextHighRiskFilePath);

    private bool CanSelectHighestPriorityBuilderReviewFile()
        => !string.IsNullOrWhiteSpace(_builderReviewHighestPriorityFilePath);

    private bool CanSelectPreviousBuilderReviewFile()
        => _builderReviewFileRows.Length > 1 && _builderReviewCurrentFile is not null;

    private bool CanSelectFirstBuilderReviewRejectedFile()
        => _builderReviewFileRows.Any(row => row.IsRejected);

    private bool CanSelectFirstBuilderReviewPendingFile()
        => _builderReviewFileRows.Any(row => row.IsPendingReview);

    private bool CanSelectFirstBuilderReviewGroupFile()
        => _builderReviewCurrentFile is not null &&
           _builderReviewFileRows.Any(row => string.Equals(row.GroupKey, _builderReviewCurrentFile.GroupKey, StringComparison.OrdinalIgnoreCase));

    private bool CanApplyBuilderReviewBatchAction()
        => _builderReviewCurrentFile is not null && !string.IsNullOrWhiteSpace(GetBuilderWorkspaceRepoRoot());

    private Task OpenBuilderReviewWorkspaceArtifactAsync()
        => OpenPathIfExistsAsync(_builderReviewWorkspaceArtifactPath);

    private Task OpenBuilderReviewQueueArtifactAsync()
        => OpenPathIfExistsAsync(_builderReviewQueueArtifactPath);

    private Task CopyBuilderCurrentReviewSummaryAsync()
    {
        if (_builderReviewCurrentFile is null)
            return Task.CompletedTask;

        var builder = new StringBuilder();
        builder.AppendLine(_builderReviewCurrentFile.FileHeader);
        builder.AppendLine($"Group: {_builderReviewCurrentFile.GroupLabel}");
        builder.AppendLine($"Approval: {_builderReviewCurrentFile.ApprovalBadge}");
        builder.AppendLine($"Priority: {_builderReviewCurrentFile.PriorityBadge}");
        builder.AppendLine(_builderReviewCurrentFile.QueueBadge);
        builder.AppendLine($"Change: {_builderReviewCurrentFile.ChangeSummary}");
        builder.AppendLine($"Context: {_builderReviewCurrentFile.ContextSummary}");
        if (_builderReviewCurrentFile.IsHighRisk)
            builder.AppendLine(_builderReviewCurrentFile.HighRiskBadge);
        if (_builderReviewCurrentFile.HasRelatedTestFile)
            builder.AppendLine($"Related test: {_builderReviewCurrentFile.RelatedTestFilePath}");
        if (_builderReviewCurrentFile.HasRejectionReason)
            builder.AppendLine($"Rejection reason: {_builderReviewCurrentFile.RejectionReason}");
        builder.AppendLine("Bounded diff excerpt:");
        builder.AppendLine(_builderReviewCurrentFile.BoundedDiffExcerpt);
        return _workspaceShell.CopyTextAsync(builder.ToString().Trim());
    }

    private Task SelectBuilderReviewFileAsync(BuilderReviewFileRow? row)
        => SelectBuilderReviewFilePathAsync(row?.RelativePath);

    private Task SelectFirstBuilderReviewFileInGroupAsync(BuilderReviewGroupRow? row)
    {
        if (row is null)
            return Task.CompletedTask;

        return SelectBuilderReviewFileAsync(row.Files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault());
    }

    private Task SelectNextBuilderReviewPendingFileAsync()
        => SelectBuilderReviewFilePathAsync(_builderReviewNextPendingFilePath, revealAcrossFilters: true);

    private Task SelectNextBuilderReviewRejectedFileAsync()
        => SelectBuilderReviewFilePathAsync(_builderReviewNextRejectedFilePath, revealAcrossFilters: true);

    private Task SelectNextBuilderReviewRevisionFileAsync()
        => SelectBuilderReviewFilePathAsync(_builderReviewNextRevisionFilePath, revealAcrossFilters: true);

    private Task SelectNextBuilderReviewHighRiskFileAsync()
        => SelectBuilderReviewFilePathAsync(_builderReviewNextHighRiskFilePath, revealAcrossFilters: true);

    private Task SelectHighestPriorityBuilderReviewFileAsync()
        => SelectBuilderReviewFilePathAsync(_builderReviewHighestPriorityFilePath, revealAcrossFilters: true);

    private Task SelectPreviousBuilderReviewFileAsync()
    {
        if (_builderReviewCurrentFile is null || _builderReviewFileRows.Length < 2)
            return Task.CompletedTask;

        var currentIndex = Array.FindIndex(_builderReviewFileRows, row => string.Equals(row.RelativePath, _builderReviewCurrentFile.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (currentIndex <= 0)
            return SelectBuilderReviewFileAsync(_builderReviewFileRows[^1]);

        return SelectBuilderReviewFileAsync(_builderReviewFileRows[currentIndex - 1]);
    }

    private Task SelectFirstBuilderReviewRejectedFileAsync()
        => SelectBuilderReviewFileAsync(_builderReviewFileRows.FirstOrDefault(row => row.IsRejected));

    private Task SelectFirstBuilderReviewPendingFileAsync()
        => SelectBuilderReviewFileAsync(_builderReviewFileRows.FirstOrDefault(row => row.IsPendingReview));

    private Task SelectFirstBuilderReviewGroupFileAsync()
    {
        if (_builderReviewCurrentFile is null)
            return Task.CompletedTask;

        return SelectBuilderReviewFileAsync(_builderReviewFileRows
            .Where(row => string.Equals(row.GroupKey, _builderReviewCurrentFile.GroupKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault());
    }

    private Task ApprovePendingBuilderReviewGroupAsync()
        => ApplyBuilderReviewBatchActionAsync("approve_pending_in_group", "group", _builderReviewCurrentFile?.GroupKey ?? string.Empty);

    private Task ApprovePendingBuilderReviewDirectoryAsync()
        => ApplyBuilderReviewBatchActionAsync("approve_pending_in_directory", "directory", _builderReviewCurrentFile?.DirectoryPath ?? string.Empty);

    private Task ApprovePendingBuilderReviewFilterAsync()
        => ApplyBuilderReviewBatchActionAsync("approve_pending_in_filter", "filter", _selectedBuilderReviewFilter);

    private Task RejectBuilderReviewGroupAsync()
        => ApplyBuilderReviewBatchActionAsync("reject_all_in_group", "group", _builderReviewCurrentFile?.GroupKey ?? string.Empty);

    private Task RequestBuilderReviewGroupRevisionAsync()
        => ApplyBuilderReviewBatchActionAsync("mark_group_needs_revision", "group", _builderReviewCurrentFile?.GroupKey ?? string.Empty);

    private Task SelectBuilderReviewFilePathAsync(string? relativePath, bool revealAcrossFilters = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Task.CompletedTask;

        if (revealAcrossFilters &&
            !_builderReviewFileRows.Any(row => string.Equals(row.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedBuilderReviewFilter = "all";
        }

        _builderReviewCurrentFilePath = relativePath;
        LoadBuilderReviewWorkspaceArtifacts();
        return Task.CompletedTask;
    }

    private Task ApplyBuilderReviewBatchActionAsync(string actionType, string scopeType, string scopeValue)
    {
        var repoRoot = GetBuilderWorkspaceRepoRoot();
        if (string.IsNullOrWhiteSpace(repoRoot) || _builderReviewCurrentFile is null)
            return Task.CompletedTask;

        var context = BuilderReviewWorkspaceService.ApplyBatchReviewAction(
            repoRoot,
            new BuilderBatchReviewActionRequest(
                actionType,
                scopeType,
                scopeValue,
                _selectedBuilderReviewFilter,
                _selectedBuilderReviewGrouping,
                _builderReviewCurrentFile.RelativePath));
        if (context is null)
            return Task.CompletedTask;

        ApplyBuilderReviewWorkspaceContext(context);
        RecordBuilderReviewDecision(
            actionType,
            context,
            context.BatchReviewActions.Entries.FirstOrDefault()?.ObservedUtc ?? context.Workspace.ObservedUtc);
        NotifyBuilderReviewWorkspaceChanged();
        LoadBuilderCrossRepoArtifacts();
        return Task.CompletedTask;
    }

    private static string BuildBuilderReviewCountsSummary(
        BuilderReviewWorkspaceCounts counts,
        BuilderReviewEfficiencySummaryRecord efficiency,
        BuilderReviewQueueRecord queue)
        => $"Files={counts.TotalChangedFiles}. Pending={counts.PendingFiles}. Approved={counts.ApprovedFiles}. Rejected={counts.RejectedFiles}. Needs revision={counts.NeedsRevisionFiles}. High-risk={queue.HighRiskFiles.Count}. Reviewed={efficiency.FilesReviewedThisSession}. Remaining={counts.PendingFiles + counts.RejectedFiles + counts.NeedsRevisionFiles}. Completion={efficiency.ApprovalCompletionPercentage:0.##}%.";

    private static string BuildBuilderReviewNavigationSummary(
        BuilderReviewNavigationStateRecord navigation,
        BuilderReviewQueueNavigationRecord queueNavigation)
        => $"Current filter={navigation.CurrentFilter}. Current group={navigation.CurrentGroup}. Highest priority={FirstNonEmptyDisplay(queueNavigation.NextPriorityFile)}. Next pending={FirstNonEmptyDisplay(queueNavigation.NextPendingFile)}. Next rejected={FirstNonEmptyDisplay(queueNavigation.NextRejectedFile)}. Next revision={FirstNonEmptyDisplay(queueNavigation.NextRevisionFile)}. Next high risk={FirstNonEmptyDisplay(queueNavigation.NextHighRiskFile)}.";

    private static string FirstNonEmptyDisplay(string value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value;
}

public sealed record BuilderReviewOptionRow(string Value, string Label);

public sealed record BuilderReviewGroupRow(
    string GroupKey,
    string GroupLabel,
    int TotalFiles,
    int PendingFiles,
    int ApprovedFiles,
    int RejectedFiles,
    int NeedsRevisionFiles,
    int HighRiskFiles,
    bool IsCurrentGroup,
    IReadOnlyList<BuilderReviewFileRow> Files)
{
    public string Summary => $"Total={TotalFiles}. Pending={PendingFiles}. Approved={ApprovedFiles}. Rejected={RejectedFiles}. Needs revision={NeedsRevisionFiles}. High risk={HighRiskFiles}.";
}

public sealed record BuilderReviewFileRow(
    string RelativePath,
    string DirectoryPath,
    string FileHeader,
    string ChangeKind,
    string ChangeSummary,
    string DiffSummary,
    string BoundedDiffExcerpt,
    string ApprovalState,
    string RejectionReason,
    string RelatedTestFilePath,
    string ContextSummary,
    string HighRiskCategory,
    bool RequiresExplicitApproval,
    int PriorityRank,
    string PriorityLabel,
    string GroupKey,
    string GroupLabel,
    int QueuePosition,
    bool IsCurrentFile)
{
    public string ApprovalBadge => ApprovalState switch
    {
        "approved" => "Approved",
        "rejected" => "Rejected",
        "needs_revision" => "Needs revision",
        _ => "Pending review"
    };
    public string PriorityBadge => PriorityLabel;
    public string QueueBadge => QueuePosition > 0 ? $"Queue #{QueuePosition}" : "Queue not recorded";
    public bool IsHighRisk => RequiresExplicitApproval;
    public string HighRiskBadge => IsHighRisk
        ? $"High risk: {FormatValue(HighRiskCategory)}"
        : string.Empty;

    public bool HasRejectionReason => !string.IsNullOrWhiteSpace(RejectionReason);
    public bool HasRelatedTestFile => !string.IsNullOrWhiteSpace(RelatedTestFilePath);
    public bool IsPendingReview => !string.Equals(ApprovalState, "approved", StringComparison.Ordinal) &&
                                   !string.Equals(ApprovalState, "rejected", StringComparison.Ordinal) &&
                                   !string.Equals(ApprovalState, "needs_revision", StringComparison.Ordinal);
    public bool IsRejected => string.Equals(ApprovalState, "rejected", StringComparison.Ordinal);
    public bool IsNeedsRevision => string.Equals(ApprovalState, "needs_revision", StringComparison.Ordinal);

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}
