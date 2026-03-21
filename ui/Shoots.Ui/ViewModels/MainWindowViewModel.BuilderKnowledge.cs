using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderKnowledgePatternInsightRecord> _builderKnowledgePatterns = new();
    private readonly ObservableCollection<BuilderKnowledgeDependencyRecord> _builderKnowledgeDependencies = new();
    private readonly ObservableCollection<BuilderKnowledgeRouteInsightRecord> _builderKnowledgeSuccessfulRoutes = new();
    private readonly ObservableCollection<BuilderKnowledgeRouteInsightRecord> _builderKnowledgeFailureRoutes = new();
    private string _builderKnowledgeGraphSummary = "No builder knowledge graph recorded.";
    private string _builderKnowledgeSuccessSummary = "No successful builder patterns recorded.";
    private string _builderKnowledgeFailureSummary = "No builder failure patterns recorded.";
    private string _builderKnowledgeDependencySummary = "No workspace dependency knowledge recorded.";
    private string _builderKnowledgeGraphArtifactPath = string.Empty;
    private string _builderKnowledgePatternsArtifactPath = string.Empty;
    private string _builderKnowledgeFailureArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderKnowledgePatternInsightRecord> BuilderKnowledgePatterns { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderKnowledgeDependencyRecord> BuilderKnowledgeDependencies { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderKnowledgeRouteInsightRecord> BuilderKnowledgeSuccessfulRoutes { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderKnowledgeRouteInsightRecord> BuilderKnowledgeFailureRoutes { get; private set; } = null!;

    public bool HasBuilderKnowledgePatterns => _builderKnowledgePatterns.Count > 0;
    public bool HasBuilderKnowledgeDependencies => _builderKnowledgeDependencies.Count > 0;
    public bool HasBuilderKnowledgeSuccessfulRoutes => _builderKnowledgeSuccessfulRoutes.Count > 0;
    public bool HasBuilderKnowledgeFailureRoutes => _builderKnowledgeFailureRoutes.Count > 0;
    public string BuilderKnowledgeGraphSummary => _builderKnowledgeGraphSummary;
    public bool HasBuilderKnowledgeGraphSummary => !string.IsNullOrWhiteSpace(_builderKnowledgeGraphSummary) &&
                                                   !string.Equals(_builderKnowledgeGraphSummary, "No builder knowledge graph recorded.", StringComparison.Ordinal);
    public string BuilderKnowledgeSuccessSummary => _builderKnowledgeSuccessSummary;
    public bool HasBuilderKnowledgeSuccessSummary => !string.IsNullOrWhiteSpace(_builderKnowledgeSuccessSummary) &&
                                                     !string.Equals(_builderKnowledgeSuccessSummary, "No successful builder patterns recorded.", StringComparison.Ordinal);
    public string BuilderKnowledgeFailureSummary => _builderKnowledgeFailureSummary;
    public bool HasBuilderKnowledgeFailureSummary => !string.IsNullOrWhiteSpace(_builderKnowledgeFailureSummary) &&
                                                     !string.Equals(_builderKnowledgeFailureSummary, "No builder failure patterns recorded.", StringComparison.Ordinal);
    public string BuilderKnowledgeDependencySummary => _builderKnowledgeDependencySummary;
    public bool HasBuilderKnowledgeDependencySummary => !string.IsNullOrWhiteSpace(_builderKnowledgeDependencySummary) &&
                                                        !string.Equals(_builderKnowledgeDependencySummary, "No workspace dependency knowledge recorded.", StringComparison.Ordinal);
    public string BuilderKnowledgeGraphArtifactPath => _builderKnowledgeGraphArtifactPath;
    public bool HasBuilderKnowledgeGraphArtifactPath => !string.IsNullOrWhiteSpace(_builderKnowledgeGraphArtifactPath) && File.Exists(_builderKnowledgeGraphArtifactPath);
    public string BuilderKnowledgePatternsArtifactPath => _builderKnowledgePatternsArtifactPath;
    public bool HasBuilderKnowledgePatternsArtifactPath => !string.IsNullOrWhiteSpace(_builderKnowledgePatternsArtifactPath) && File.Exists(_builderKnowledgePatternsArtifactPath);
    public string BuilderKnowledgeFailureArtifactPath => _builderKnowledgeFailureArtifactPath;
    public bool HasBuilderKnowledgeFailureArtifactPath => !string.IsNullOrWhiteSpace(_builderKnowledgeFailureArtifactPath) && File.Exists(_builderKnowledgeFailureArtifactPath);

    public AsyncRelayCommand OpenBuilderKnowledgeGraphArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderKnowledgePatternsArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderKnowledgeFailureArtifactCommand { get; private set; } = null!;

    private void InitializeBuilderKnowledgeSurface()
    {
        BuilderKnowledgePatterns = new ReadOnlyObservableCollection<BuilderKnowledgePatternInsightRecord>(_builderKnowledgePatterns);
        BuilderKnowledgeDependencies = new ReadOnlyObservableCollection<BuilderKnowledgeDependencyRecord>(_builderKnowledgeDependencies);
        BuilderKnowledgeSuccessfulRoutes = new ReadOnlyObservableCollection<BuilderKnowledgeRouteInsightRecord>(_builderKnowledgeSuccessfulRoutes);
        BuilderKnowledgeFailureRoutes = new ReadOnlyObservableCollection<BuilderKnowledgeRouteInsightRecord>(_builderKnowledgeFailureRoutes);
        OpenBuilderKnowledgeGraphArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderKnowledgeGraphArtifactPath), () => HasBuilderKnowledgeGraphArtifactPath);
        OpenBuilderKnowledgePatternsArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderKnowledgePatternsArtifactPath), () => HasBuilderKnowledgePatternsArtifactPath);
        OpenBuilderKnowledgeFailureArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderKnowledgeFailureArtifactPath), () => HasBuilderKnowledgeFailureArtifactPath);
    }

    private void LoadBuilderKnowledgeArtifacts(BuilderCrossRepoOrchestrationContext? orchestration = null)
    {
        BuilderKnowledgeGraphContext? context = null;
        if (orchestration is not null && _builderWorkspaceOptions.Count > 0)
        {
            var descriptors = _builderWorkspaceOptions
                .Select(option => BuilderWorkspaceService.CreateDescriptor(option.RepoRoot, option.RepoName))
                .ToArray();
            context = BuilderKnowledgeGraphService.RefreshKnowledgeArtifacts(
                descriptors,
                orchestration,
                _selectedBuilderWorkspaceId);
        }
        else
        {
            var repoRoot = GetBuilderWorkspaceRepoRoot();
            var graph = BuilderKnowledgeGraphService.LoadKnowledgeGraph(repoRoot);
            var patterns = BuilderKnowledgeGraphService.LoadExecutionPatterns(repoRoot);
            var failures = BuilderKnowledgeGraphService.LoadFailurePatterns(repoRoot);
            if (graph is not null || patterns is not null || failures is not null)
            {
                context = new BuilderKnowledgeGraphContext(
                    graph ?? new BuilderKnowledgeGraphRecord(0, Array.Empty<BuilderKnowledgeGraphEntryRecord>(), "No builder knowledge graph recorded.", BuilderKnowledgeGraphService.KnowledgeGraphPathForRepo(repoRoot), DateTimeOffset.MinValue),
                    patterns ?? new BuilderExecutionPatternsRecord(0, Array.Empty<BuilderExecutionPatternRecord>(), "No successful builder patterns recorded.", BuilderKnowledgeGraphService.ExecutionPatternsPathForRepo(repoRoot), DateTimeOffset.MinValue),
                    failures ?? new BuilderFailurePatternsRecord(0, Array.Empty<BuilderFailurePatternRecord>(), "No builder failure patterns recorded.", BuilderKnowledgeGraphService.FailurePatternsPathForRepo(repoRoot), DateTimeOffset.MinValue),
                    BuilderKnowledgeGraphService.QueryKnownWorkspaceDependencies(repoRoot),
                    BuilderKnowledgeGraphService.QueryCommonOrchestrationPatterns(repoRoot),
                    BuilderKnowledgeGraphService.QueryPriorSuccessfulRoutes(repoRoot),
                    BuilderKnowledgeGraphService.QueryKnownFailureRoutes(repoRoot));
            }
        }

        if (context is null)
        {
            ResetBuilderKnowledgeState();
            return;
        }

        _builderKnowledgeGraphSummary = context.KnowledgeGraph.Summary;
        _builderKnowledgeDependencySummary = context.WorkspaceDependencies.Count == 0
            ? "No workspace dependency knowledge recorded."
            : $"Workspace dependency map contains {context.WorkspaceDependencies.Count} known relationship(s).";
        _builderKnowledgeSuccessSummary = context.PriorSuccessfulRoutes.Count == 0
            ? "No successful builder patterns recorded."
            : $"Successful route trends: {string.Join(" | ", context.PriorSuccessfulRoutes.Take(2).Select(route => $"{route.RouteUsed} ({route.Occurrences})"))}.";
        _builderKnowledgeFailureSummary = context.KnownFailureRoutes.Count == 0
            ? "No builder failure patterns recorded."
            : $"Failure route trends: {string.Join(" | ", context.KnownFailureRoutes.Take(2).Select(route => $"{route.RouteUsed} ({route.Occurrences})"))}.";
        _builderKnowledgeGraphArtifactPath = context.KnowledgeGraph.ArtifactPath;
        _builderKnowledgePatternsArtifactPath = context.ExecutionPatterns.ArtifactPath;
        _builderKnowledgeFailureArtifactPath = context.FailurePatterns.ArtifactPath;

        _builderKnowledgePatterns.Clear();
        foreach (var pattern in context.CommonPatterns)
        {
            _builderKnowledgePatterns.Add(pattern);
        }

        _builderKnowledgeDependencies.Clear();
        foreach (var dependency in context.WorkspaceDependencies)
        {
            _builderKnowledgeDependencies.Add(dependency);
        }

        _builderKnowledgeSuccessfulRoutes.Clear();
        foreach (var route in context.PriorSuccessfulRoutes)
        {
            _builderKnowledgeSuccessfulRoutes.Add(route);
        }

        _builderKnowledgeFailureRoutes.Clear();
        foreach (var route in context.KnownFailureRoutes)
        {
            _builderKnowledgeFailureRoutes.Add(route);
        }

        NotifyBuilderKnowledgeStateChanged();
    }

    private void ResetBuilderKnowledgeState()
    {
        _builderKnowledgeGraphSummary = "No builder knowledge graph recorded.";
        _builderKnowledgeSuccessSummary = "No successful builder patterns recorded.";
        _builderKnowledgeFailureSummary = "No builder failure patterns recorded.";
        _builderKnowledgeDependencySummary = "No workspace dependency knowledge recorded.";
        _builderKnowledgeGraphArtifactPath = string.Empty;
        _builderKnowledgePatternsArtifactPath = string.Empty;
        _builderKnowledgeFailureArtifactPath = string.Empty;
        _builderKnowledgePatterns.Clear();
        _builderKnowledgeDependencies.Clear();
        _builderKnowledgeSuccessfulRoutes.Clear();
        _builderKnowledgeFailureRoutes.Clear();
        NotifyBuilderKnowledgeStateChanged();
    }

    private void NotifyBuilderKnowledgeStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderKnowledgePatterns));
        OnPropertyChanged(nameof(HasBuilderKnowledgeDependencies));
        OnPropertyChanged(nameof(HasBuilderKnowledgeSuccessfulRoutes));
        OnPropertyChanged(nameof(HasBuilderKnowledgeFailureRoutes));
        OnPropertyChanged(nameof(BuilderKnowledgeGraphSummary));
        OnPropertyChanged(nameof(HasBuilderKnowledgeGraphSummary));
        OnPropertyChanged(nameof(BuilderKnowledgeSuccessSummary));
        OnPropertyChanged(nameof(HasBuilderKnowledgeSuccessSummary));
        OnPropertyChanged(nameof(BuilderKnowledgeFailureSummary));
        OnPropertyChanged(nameof(HasBuilderKnowledgeFailureSummary));
        OnPropertyChanged(nameof(BuilderKnowledgeDependencySummary));
        OnPropertyChanged(nameof(HasBuilderKnowledgeDependencySummary));
        OnPropertyChanged(nameof(BuilderKnowledgeGraphArtifactPath));
        OnPropertyChanged(nameof(HasBuilderKnowledgeGraphArtifactPath));
        OnPropertyChanged(nameof(BuilderKnowledgePatternsArtifactPath));
        OnPropertyChanged(nameof(HasBuilderKnowledgePatternsArtifactPath));
        OnPropertyChanged(nameof(BuilderKnowledgeFailureArtifactPath));
        OnPropertyChanged(nameof(HasBuilderKnowledgeFailureArtifactPath));
        OpenBuilderKnowledgeGraphArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderKnowledgePatternsArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderKnowledgeFailureArtifactCommand.RaiseCanExecuteChanged();
    }
}
