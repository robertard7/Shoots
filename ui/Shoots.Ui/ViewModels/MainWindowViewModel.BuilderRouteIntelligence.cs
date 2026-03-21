using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderRouteRecommendationEntryRecord> _builderRouteRecommendations = new();
    private readonly ObservableCollection<BuilderRouteRiskWarningEntryRecord> _builderRouteRiskWarnings = new();
    private readonly ObservableCollection<string> _builderOrchestrationWarnings = new();
    private string _builderRouteRecommendationSummary = "No builder route recommendations recorded.";
    private string _builderRouteHistoricalRatesSummary = "No historical route outcome rates recorded.";
    private string _builderRouteRiskWarningSummary = "No builder route risk warnings recorded.";
    private string _builderOrchestrationRecommendationSummary = "No builder orchestration recommendations recorded.";
    private string _builderRecommendedOrchestrationSequence = "No recommended orchestration sequence recorded.";
    private string _builderHistoricalOrchestrationOrdering = "No historical workspace ordering recorded.";
    private string _builderRouteRecommendationArtifactPath = string.Empty;
    private string _builderRouteRiskWarningArtifactPath = string.Empty;
    private string _builderOrchestrationRecommendationArtifactPath = string.Empty;

    public ReadOnlyObservableCollection<BuilderRouteRecommendationEntryRecord> BuilderRouteRecommendations { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRouteRiskWarningEntryRecord> BuilderRouteRiskWarnings { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderOrchestrationWarnings { get; private set; } = null!;

    public bool HasBuilderRouteRecommendations => _builderRouteRecommendations.Count > 0;
    public bool HasBuilderRouteRiskWarnings => _builderRouteRiskWarnings.Count > 0;
    public bool HasBuilderOrchestrationWarnings => _builderOrchestrationWarnings.Count > 0;
    public string BuilderRouteRecommendationSummary => _builderRouteRecommendationSummary;
    public bool HasBuilderRouteRecommendationSummary => !string.IsNullOrWhiteSpace(_builderRouteRecommendationSummary) &&
                                                        !string.Equals(_builderRouteRecommendationSummary, "No builder route recommendations recorded.", StringComparison.Ordinal);
    public string BuilderRouteHistoricalRatesSummary => _builderRouteHistoricalRatesSummary;
    public bool HasBuilderRouteHistoricalRatesSummary => !string.IsNullOrWhiteSpace(_builderRouteHistoricalRatesSummary) &&
                                                         !string.Equals(_builderRouteHistoricalRatesSummary, "No historical route outcome rates recorded.", StringComparison.Ordinal);
    public string BuilderRouteRiskWarningSummary => _builderRouteRiskWarningSummary;
    public bool HasBuilderRouteRiskWarningSummary => !string.IsNullOrWhiteSpace(_builderRouteRiskWarningSummary) &&
                                                     !string.Equals(_builderRouteRiskWarningSummary, "No builder route risk warnings recorded.", StringComparison.Ordinal);
    public string BuilderOrchestrationRecommendationSummary => _builderOrchestrationRecommendationSummary;
    public bool HasBuilderOrchestrationRecommendationSummary => !string.IsNullOrWhiteSpace(_builderOrchestrationRecommendationSummary) &&
                                                                !string.Equals(_builderOrchestrationRecommendationSummary, "No builder orchestration recommendations recorded.", StringComparison.Ordinal);
    public string BuilderRecommendedOrchestrationSequence => _builderRecommendedOrchestrationSequence;
    public bool HasBuilderRecommendedOrchestrationSequence => !string.IsNullOrWhiteSpace(_builderRecommendedOrchestrationSequence) &&
                                                              !string.Equals(_builderRecommendedOrchestrationSequence, "No recommended orchestration sequence recorded.", StringComparison.Ordinal);
    public string BuilderHistoricalOrchestrationOrdering => _builderHistoricalOrchestrationOrdering;
    public bool HasBuilderHistoricalOrchestrationOrdering => !string.IsNullOrWhiteSpace(_builderHistoricalOrchestrationOrdering) &&
                                                             !string.Equals(_builderHistoricalOrchestrationOrdering, "No historical workspace ordering recorded.", StringComparison.Ordinal);
    public string BuilderRouteRecommendationArtifactPath => _builderRouteRecommendationArtifactPath;
    public bool HasBuilderRouteRecommendationArtifactPath => !string.IsNullOrWhiteSpace(_builderRouteRecommendationArtifactPath) && File.Exists(_builderRouteRecommendationArtifactPath);
    public string BuilderRouteRiskWarningArtifactPath => _builderRouteRiskWarningArtifactPath;
    public bool HasBuilderRouteRiskWarningArtifactPath => !string.IsNullOrWhiteSpace(_builderRouteRiskWarningArtifactPath) && File.Exists(_builderRouteRiskWarningArtifactPath);
    public string BuilderOrchestrationRecommendationArtifactPath => _builderOrchestrationRecommendationArtifactPath;
    public bool HasBuilderOrchestrationRecommendationArtifactPath => !string.IsNullOrWhiteSpace(_builderOrchestrationRecommendationArtifactPath) && File.Exists(_builderOrchestrationRecommendationArtifactPath);

    public AsyncRelayCommand OpenBuilderRouteRecommendationArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderRouteRiskWarningArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderOrchestrationRecommendationArtifactCommand { get; private set; } = null!;

    private void InitializeBuilderRouteIntelligenceSurface()
    {
        BuilderRouteRecommendations = new ReadOnlyObservableCollection<BuilderRouteRecommendationEntryRecord>(_builderRouteRecommendations);
        BuilderRouteRiskWarnings = new ReadOnlyObservableCollection<BuilderRouteRiskWarningEntryRecord>(_builderRouteRiskWarnings);
        BuilderOrchestrationWarnings = new ReadOnlyObservableCollection<string>(_builderOrchestrationWarnings);
        OpenBuilderRouteRecommendationArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRouteRecommendationArtifactPath), () => HasBuilderRouteRecommendationArtifactPath);
        OpenBuilderRouteRiskWarningArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRouteRiskWarningArtifactPath), () => HasBuilderRouteRiskWarningArtifactPath);
        OpenBuilderOrchestrationRecommendationArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderOrchestrationRecommendationArtifactPath), () => HasBuilderOrchestrationRecommendationArtifactPath);
    }

    private void LoadBuilderRouteIntelligenceArtifacts(BuilderCrossRepoOrchestrationContext? orchestration = null)
    {
        BuilderRouteIntelligenceContext? context = null;
        if (orchestration is not null && _builderWorkspaceOptions.Count > 0)
        {
            var descriptors = _builderWorkspaceOptions
                .Select(option => BuilderWorkspaceService.CreateDescriptor(option.RepoRoot, option.RepoName))
                .ToArray();
            context = BuilderRouteIntelligenceService.RefreshRouteIntelligenceArtifacts(
                descriptors,
                orchestration,
                _selectedBuilderWorkspaceId,
                orchestration.Plan.RequestId);
        }
        else
        {
            var repoRoot = GetBuilderWorkspaceRepoRoot();
            var recommendations = BuilderRouteIntelligenceService.LoadRouteRecommendations(repoRoot);
            var warnings = BuilderRouteIntelligenceService.LoadRouteRiskWarnings(repoRoot);
            var orchestrationRecommendations = BuilderRouteIntelligenceService.LoadOrchestrationRecommendations(repoRoot);
            if (recommendations is not null || warnings is not null || orchestrationRecommendations is not null)
            {
                context = new BuilderRouteIntelligenceContext(
                    recommendations ?? new BuilderRouteRecommendationsRecord(string.Empty, string.Empty, Array.Empty<BuilderRouteRecommendationEntryRecord>(), 0d, 0d, Array.Empty<string>(), "No builder route recommendations recorded.", BuilderRouteIntelligenceService.RouteRecommendationsPathForRepo(repoRoot), DateTimeOffset.MinValue),
                    warnings ?? new BuilderRouteRiskWarningsRecord(string.Empty, string.Empty, Array.Empty<BuilderRouteRiskWarningEntryRecord>(), "No builder route risk warnings recorded.", BuilderRouteIntelligenceService.RouteRiskWarningsPathForRepo(repoRoot), DateTimeOffset.MinValue),
                    orchestrationRecommendations ?? new BuilderOrchestrationRecommendationsRecord(string.Empty, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "No builder orchestration recommendations recorded.", BuilderRouteIntelligenceService.OrchestrationRecommendationsPathForRepo(repoRoot), DateTimeOffset.MinValue));
            }
        }

        if (context is null)
        {
            ResetBuilderRouteIntelligenceState();
            return;
        }

        _builderRouteRecommendationSummary = context.RouteRecommendations.ReasoningSummary;
        _builderRouteHistoricalRatesSummary = $"Historical success rate: {context.RouteRecommendations.HistoricalSuccessRate:0.##}%. Historical failure rate: {context.RouteRecommendations.HistoricalFailureRate:0.##}%.";
        _builderRouteRiskWarningSummary = context.RiskWarnings.Summary;
        _builderOrchestrationRecommendationSummary = context.OrchestrationRecommendations.Summary;
        _builderRecommendedOrchestrationSequence = context.OrchestrationRecommendations.RecommendedSequenceSummary;
        _builderHistoricalOrchestrationOrdering = context.OrchestrationRecommendations.HistoricalOrderingSummary;
        _builderRouteRecommendationArtifactPath = context.RouteRecommendations.ArtifactPath;
        _builderRouteRiskWarningArtifactPath = context.RiskWarnings.ArtifactPath;
        _builderOrchestrationRecommendationArtifactPath = context.OrchestrationRecommendations.ArtifactPath;

        _builderRouteRecommendations.Clear();
        foreach (var recommendation in context.RouteRecommendations.RecommendedRoutes)
        {
            _builderRouteRecommendations.Add(recommendation);
        }

        _builderRouteRiskWarnings.Clear();
        foreach (var warning in context.RiskWarnings.Entries)
        {
            _builderRouteRiskWarnings.Add(warning);
        }

        _builderOrchestrationWarnings.Clear();
        foreach (var warning in context.OrchestrationRecommendations.OrderingWarnings)
        {
            _builderOrchestrationWarnings.Add(warning);
        }

        NotifyBuilderRouteIntelligenceStateChanged();
    }

    private void ResetBuilderRouteIntelligenceState()
    {
        _builderRouteRecommendationSummary = "No builder route recommendations recorded.";
        _builderRouteHistoricalRatesSummary = "No historical route outcome rates recorded.";
        _builderRouteRiskWarningSummary = "No builder route risk warnings recorded.";
        _builderOrchestrationRecommendationSummary = "No builder orchestration recommendations recorded.";
        _builderRecommendedOrchestrationSequence = "No recommended orchestration sequence recorded.";
        _builderHistoricalOrchestrationOrdering = "No historical workspace ordering recorded.";
        _builderRouteRecommendationArtifactPath = string.Empty;
        _builderRouteRiskWarningArtifactPath = string.Empty;
        _builderOrchestrationRecommendationArtifactPath = string.Empty;
        _builderRouteRecommendations.Clear();
        _builderRouteRiskWarnings.Clear();
        _builderOrchestrationWarnings.Clear();
        NotifyBuilderRouteIntelligenceStateChanged();
    }

    private void NotifyBuilderRouteIntelligenceStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderRouteRecommendations));
        OnPropertyChanged(nameof(HasBuilderRouteRiskWarnings));
        OnPropertyChanged(nameof(HasBuilderOrchestrationWarnings));
        OnPropertyChanged(nameof(BuilderRouteRecommendationSummary));
        OnPropertyChanged(nameof(HasBuilderRouteRecommendationSummary));
        OnPropertyChanged(nameof(BuilderRouteHistoricalRatesSummary));
        OnPropertyChanged(nameof(HasBuilderRouteHistoricalRatesSummary));
        OnPropertyChanged(nameof(BuilderRouteRiskWarningSummary));
        OnPropertyChanged(nameof(HasBuilderRouteRiskWarningSummary));
        OnPropertyChanged(nameof(BuilderOrchestrationRecommendationSummary));
        OnPropertyChanged(nameof(HasBuilderOrchestrationRecommendationSummary));
        OnPropertyChanged(nameof(BuilderRecommendedOrchestrationSequence));
        OnPropertyChanged(nameof(HasBuilderRecommendedOrchestrationSequence));
        OnPropertyChanged(nameof(BuilderHistoricalOrchestrationOrdering));
        OnPropertyChanged(nameof(HasBuilderHistoricalOrchestrationOrdering));
        OnPropertyChanged(nameof(BuilderRouteRecommendationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRouteRecommendationArtifactPath));
        OnPropertyChanged(nameof(BuilderRouteRiskWarningArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRouteRiskWarningArtifactPath));
        OnPropertyChanged(nameof(BuilderOrchestrationRecommendationArtifactPath));
        OnPropertyChanged(nameof(HasBuilderOrchestrationRecommendationArtifactPath));
        OpenBuilderRouteRecommendationArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteRiskWarningArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderOrchestrationRecommendationArtifactCommand.RaiseCanExecuteChanged();
    }
}
