using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Builder;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<BuilderRecoveryPlaybookRow> _builderRecoveryPlaybooks = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryRepoFilterOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryFailureClassFilterOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryRouteFilterOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoverySeverityFilterOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryScopeFilterOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryBlockingStateFilterOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryContextModeOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryIntentOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryConstraintProfileOptions = new();
    private readonly ObservableCollection<BuilderRecoveryOptionRow> _builderRecoveryConstraintRouteOptions = new();
    private readonly ObservableCollection<BuilderRecoveryPlaybookStepRecord> _builderRecoverySelectedSteps = new();
    private readonly ObservableCollection<BuilderRecoveryArtifactLinkRow> _builderRecoverySelectedArtifactLinks = new();
    private readonly ObservableCollection<string> _builderRecoverySelectedRankingBreakdown = new();
    private readonly ObservableCollection<string> _builderRecoverySelectedSignalContributions = new();
    private readonly ObservableCollection<string> _builderRecoverySelectedContextFlags = new();
    private readonly ObservableCollection<string> _builderRecoveryCoordinationRelations = new();
    private readonly ObservableCollection<string> _builderRecoveryCoordinationStagingSuggestions = new();
    private readonly ObservableCollection<string> _builderRecoveryCoordinationNotes = new();
    private readonly ObservableCollection<string> _builderRecoveryActiveConstraints = new();
    private BuilderRecoveryPlaybookRow[] _builderRecoveryAllPlaybooks = Array.Empty<BuilderRecoveryPlaybookRow>();
    private BuilderRecoveryPlaybookRow? _selectedBuilderRecoveryPlaybook;
    private string _builderRecoverySummary = "No builder recovery playbooks recorded.";
    private string _builderRecoveryAdvisoryBanner = "Recovery playbooks are advisory only. Routing, review, approval, and finalize still require explicit operator action.";
    private string _builderRecoveryCoordinationSummary = "No cross-repo recovery guidance recorded.";
    private string _builderRecoveryCoordinationOrder = "No coordinated recovery order recorded.";
    private string _builderRecoveryBlockingReposSummary = "No blocking repos recorded.";
    private string _builderRecoveryAffectedReposSummary = "No affected repos recorded.";
    private string _builderRecoveryArtifactPath = string.Empty;
    private string _builderRecoveryRankingSummary = "No evidence-weighted playbook ranking recorded.";
    private string _builderRecoveryRankingArtifactPath = string.Empty;
    private string _builderRecoveryContextFilterSummary = "No contextual playbook filter recorded.";
    private string _builderRecoveryContextSnapshotSummary = "No contextual workspace snapshot recorded.";
    private string _builderRecoveryContextFilterArtifactPath = string.Empty;
    private string _builderRecoveryIntentSummary = "No explicit operator intent recorded.";
    private string _builderRecoveryIntentArtifactPath = string.Empty;
    private string _builderRecoveryConstraintSummary = "No operator constraint profile recorded.";
    private string _builderRecoveryConstraintArtifactPath = string.Empty;
    private string _builderRecoveryConstraintVisibilitySummary = "No recovery options are currently hidden by constraints.";
    private string _builderRecoveryConstraintNewProfileName = string.Empty;
    private string _selectedBuilderRecoveryRepoFilter = "all";
    private string _selectedBuilderRecoveryFailureClassFilter = "all";
    private string _selectedBuilderRecoveryRouteFilter = "all";
    private string _selectedBuilderRecoverySeverityFilter = "all";
    private string _selectedBuilderRecoveryScopeFilter = "all";
    private string _selectedBuilderRecoveryBlockingStateFilter = "all";
    private string _selectedBuilderRecoveryContextMode = BuilderPlaybookContextFilterService.ShowAllModeId;
    private string _selectedBuilderRecoveryIntent = string.Empty;
    private string _selectedBuilderRecoveryConstraintProfile = string.Empty;
    private string _selectedBuilderRecoveryConstraintRoute = string.Empty;
    private bool _isHydratingBuilderRecoveryIntentSelection;
    private bool _isHydratingBuilderRecoveryConstraintSelection;
    private bool _showBuilderRecoveryViolatingOptions;
    private bool _builderRecoveryConstraintBlockHighRiskFilesEnabled;
    private bool _builderRecoveryConstraintBlockSelectedRouteEnabled;
    private bool _builderRecoveryConstraintBlockCrossRepoActionsEnabled;
    private bool _builderRecoveryConstraintLimitToSingleRepoEnabled;
    private bool _builderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled;
    private bool _builderRecoveryConstraintBlockPartialOrchestrationEnabled;

    public ReadOnlyObservableCollection<BuilderRecoveryPlaybookRow> BuilderRecoveryPlaybooks { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryRepoFilterOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryFailureClassFilterOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryRouteFilterOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoverySeverityFilterOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryScopeFilterOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryBlockingStateFilterOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryContextModeOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryIntentOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryConstraintProfileOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryOptionRow> BuilderRecoveryConstraintRouteOptions { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryPlaybookStepRecord> BuilderRecoverySelectedSteps { get; private set; } = null!;
    public ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow> BuilderRecoverySelectedArtifactLinks { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoverySelectedRankingBreakdown { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoverySelectedSignalContributions { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoverySelectedContextFlags { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoveryCoordinationRelations { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoveryCoordinationStagingSuggestions { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoveryCoordinationNotes { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> BuilderRecoveryActiveConstraints { get; private set; } = null!;

    public bool HasBuilderRecoveryPlaybooks => _builderRecoveryPlaybooks.Count > 0;
    public string BuilderRecoverySummary => _builderRecoverySummary;
    public bool HasBuilderRecoverySummary => !string.IsNullOrWhiteSpace(_builderRecoverySummary) &&
                                            !string.Equals(_builderRecoverySummary, "No builder recovery playbooks recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryAdvisoryBanner => _builderRecoveryAdvisoryBanner;
    public string BuilderRecoveryCoordinationSummary => _builderRecoveryCoordinationSummary;
    public bool HasBuilderRecoveryCoordinationSummary => !string.IsNullOrWhiteSpace(_builderRecoveryCoordinationSummary) &&
                                                         !string.Equals(_builderRecoveryCoordinationSummary, "No cross-repo recovery guidance recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryCoordinationOrder => _builderRecoveryCoordinationOrder;
    public bool HasBuilderRecoveryCoordinationOrder => !string.IsNullOrWhiteSpace(_builderRecoveryCoordinationOrder) &&
                                                       !string.Equals(_builderRecoveryCoordinationOrder, "No coordinated recovery order recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryBlockingReposSummary => _builderRecoveryBlockingReposSummary;
    public bool HasBuilderRecoveryBlockingReposSummary => !string.IsNullOrWhiteSpace(_builderRecoveryBlockingReposSummary) &&
                                                          !string.Equals(_builderRecoveryBlockingReposSummary, "No blocking repos recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryAffectedReposSummary => _builderRecoveryAffectedReposSummary;
    public bool HasBuilderRecoveryAffectedReposSummary => !string.IsNullOrWhiteSpace(_builderRecoveryAffectedReposSummary) &&
                                                          !string.Equals(_builderRecoveryAffectedReposSummary, "No affected repos recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryArtifactPath => _builderRecoveryArtifactPath;
    public bool HasBuilderRecoveryArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoveryArtifactPath) && File.Exists(_builderRecoveryArtifactPath);
    public string BuilderRecoveryRankingSummary => _builderRecoveryRankingSummary;
    public bool HasBuilderRecoveryRankingSummary => !string.IsNullOrWhiteSpace(_builderRecoveryRankingSummary) &&
                                                    !string.Equals(_builderRecoveryRankingSummary, "No evidence-weighted playbook ranking recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryRankingArtifactPath => _builderRecoveryRankingArtifactPath;
    public bool HasBuilderRecoveryRankingArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoveryRankingArtifactPath) && File.Exists(_builderRecoveryRankingArtifactPath);
    public string BuilderRecoveryContextFilterSummary => _builderRecoveryContextFilterSummary;
    public bool HasBuilderRecoveryContextFilterSummary => !string.IsNullOrWhiteSpace(_builderRecoveryContextFilterSummary) &&
                                                          !string.Equals(_builderRecoveryContextFilterSummary, "No contextual playbook filter recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryContextSnapshotSummary => _builderRecoveryContextSnapshotSummary;
    public bool HasBuilderRecoveryContextSnapshotSummary => !string.IsNullOrWhiteSpace(_builderRecoveryContextSnapshotSummary) &&
                                                            !string.Equals(_builderRecoveryContextSnapshotSummary, "No contextual workspace snapshot recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryContextFilterArtifactPath => _builderRecoveryContextFilterArtifactPath;
    public bool HasBuilderRecoveryContextFilterArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoveryContextFilterArtifactPath) && File.Exists(_builderRecoveryContextFilterArtifactPath);
    public string BuilderRecoveryIntentSummary => _builderRecoveryIntentSummary;
    public bool HasBuilderRecoveryIntentSummary => !string.IsNullOrWhiteSpace(_builderRecoveryIntentSummary) &&
                                                   !string.Equals(_builderRecoveryIntentSummary, "No explicit operator intent recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryIntentArtifactPath => _builderRecoveryIntentArtifactPath;
    public bool HasBuilderRecoveryIntentArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoveryIntentArtifactPath) && File.Exists(_builderRecoveryIntentArtifactPath);
    public string BuilderRecoveryConstraintSummary => _builderRecoveryConstraintSummary;
    public bool HasBuilderRecoveryConstraintSummary => !string.IsNullOrWhiteSpace(_builderRecoveryConstraintSummary) &&
                                                       !string.Equals(_builderRecoveryConstraintSummary, "No operator constraint profile recorded.", StringComparison.Ordinal);
    public string BuilderRecoveryConstraintArtifactPath => _builderRecoveryConstraintArtifactPath;
    public bool HasBuilderRecoveryConstraintArtifactPath => !string.IsNullOrWhiteSpace(_builderRecoveryConstraintArtifactPath) && File.Exists(_builderRecoveryConstraintArtifactPath);
    public string BuilderRecoveryConstraintVisibilitySummary => _builderRecoveryConstraintVisibilitySummary;
    public bool HasBuilderRecoveryConstraintVisibilitySummary => !string.IsNullOrWhiteSpace(_builderRecoveryConstraintVisibilitySummary);
    public bool HasBuilderRecoverySelectedPlaybook => _selectedBuilderRecoveryPlaybook is not null;
    public string BuilderRecoverySelectedTitle => _selectedBuilderRecoveryPlaybook?.Header ?? "No recovery playbook selected.";
    public string BuilderRecoverySelectedSummary => _selectedBuilderRecoveryPlaybook?.Summary ?? "No recovery playbook selected.";
    public string BuilderRecoverySelectedContextSummary => _selectedBuilderRecoveryPlaybook?.ContextSummary ?? "No recovery context selected.";
    public string BuilderRecoverySelectedRankingSummary => _selectedBuilderRecoveryPlaybook?.RankingSummary ?? "No ranking evidence recorded for the selected playbook.";
    public bool HasBuilderRecoverySelectedRankingSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                            !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.RankingSummary);
    public string BuilderRecoverySelectedRankingIndicator => _selectedBuilderRecoveryPlaybook?.RankingIndicatorLabel ?? string.Empty;
    public bool HasBuilderRecoverySelectedRankingIndicator => _selectedBuilderRecoveryPlaybook is not null &&
                                                              !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.RankingIndicatorLabel);
    public string BuilderRecoverySelectedContextualSummary => _selectedBuilderRecoveryPlaybook?.ContextualSummary ?? "No contextual relevance recorded for the selected playbook.";
    public bool HasBuilderRecoverySelectedContextualSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                               !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.ContextualSummary);
    public string BuilderRecoverySelectedContextReason => _selectedBuilderRecoveryPlaybook?.ContextFilterReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedContextReason => _selectedBuilderRecoveryPlaybook is not null &&
                                                           !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.ContextFilterReason);
    public string BuilderRecoverySelectedConstraintSummary => _selectedBuilderRecoveryPlaybook?.ConstraintReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedConstraintSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                               !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.ConstraintReason);
    public string BuilderRecoverySelectedRiskEscalationSummary => _selectedBuilderRecoveryPlaybook?.RiskEscalationSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedRiskEscalationSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                                   !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.RiskEscalationSummary);
    public string BuilderRecoverySelectedRiskEscalationReason => _selectedBuilderRecoveryPlaybook?.RiskEscalationReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedRiskEscalationReason => _selectedBuilderRecoveryPlaybook is not null &&
                                                                  !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.RiskEscalationReason);
    public string BuilderRecoverySelectedTrustSummary => _selectedBuilderRecoveryPlaybook?.TrustSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedTrustSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                          !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.TrustSummary);
    public string BuilderRecoverySelectedTrustReason => _selectedBuilderRecoveryPlaybook?.TrustReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedTrustReason => _selectedBuilderRecoveryPlaybook is not null &&
                                                         !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.TrustReason);
    public string BuilderRecoverySelectedPredictiveRiskSummary => _selectedBuilderRecoveryPlaybook?.PredictedRiskSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedPredictiveRiskSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                                   !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.PredictedRiskSummary);
    public string BuilderRecoverySelectedPredictiveRiskReason => _selectedBuilderRecoveryPlaybook?.PredictedRiskReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedPredictiveRiskReason => _selectedBuilderRecoveryPlaybook is not null &&
                                                                  !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.PredictedRiskReason);
    public string BuilderRecoverySelectedSignalBalanceSummary => _selectedBuilderRecoveryPlaybook?.SignalBalanceSummary ?? "No signal-balance summary recorded for the selected playbook.";
    public bool HasBuilderRecoverySelectedSignalBalanceSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                                  !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.SignalBalanceSummary);
    public string BuilderRecoverySelectedIntentSummary => _selectedBuilderRecoveryPlaybook?.IntentSummary ?? "No intent-aligned ranking recorded for the selected playbook.";
    public bool HasBuilderRecoverySelectedIntentSummary => _selectedBuilderRecoveryPlaybook is not null &&
                                                           !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.IntentSummary);
    public string BuilderRecoverySelectedIntentReason => _selectedBuilderRecoveryPlaybook?.IntentReason ?? string.Empty;
    public bool HasBuilderRecoverySelectedIntentReason => _selectedBuilderRecoveryPlaybook is not null &&
                                                          !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryPlaybook.IntentReason);
    public string BuilderRecoverySelectedEvidenceBasis => _selectedBuilderRecoveryPlaybook?.Playbook.EvidenceBasis ?? string.Empty;
    public bool HasBuilderRecoverySelectedEvidenceBasis => !string.IsNullOrWhiteSpace(BuilderRecoverySelectedEvidenceBasis);
    public string BuilderRecoverySelectedGateSummary => _selectedBuilderRecoveryPlaybook?.Playbook.GateSummary ?? string.Empty;
    public bool HasBuilderRecoverySelectedSteps => _builderRecoverySelectedSteps.Count > 0;
    public bool HasBuilderRecoverySelectedArtifactLinks => _builderRecoverySelectedArtifactLinks.Count > 0;
    public bool HasBuilderRecoverySelectedRankingBreakdown => _builderRecoverySelectedRankingBreakdown.Count > 0;
    public bool HasBuilderRecoverySelectedSignalContributions => _builderRecoverySelectedSignalContributions.Count > 0;
    public bool HasBuilderRecoverySelectedContextFlags => _builderRecoverySelectedContextFlags.Count > 0;
    public bool HasBuilderRecoveryCoordinationRelations => _builderRecoveryCoordinationRelations.Count > 0;
    public bool HasBuilderRecoveryCoordinationStagingSuggestions => _builderRecoveryCoordinationStagingSuggestions.Count > 0;
    public bool HasBuilderRecoveryCoordinationNotes => _builderRecoveryCoordinationNotes.Count > 0;
    public bool HasBuilderRecoveryActiveConstraints => _builderRecoveryActiveConstraints.Count > 0;
    public string BuilderRecoveryFilterSummary => _builderRecoveryAllPlaybooks.Length == 0
        ? "No recovery playbooks loaded."
        : $"Showing {_builderRecoveryPlaybooks.Count} of {_builderRecoveryAllPlaybooks.Length} recovery playbook(s) in {FormatBuilderRecoveryContextMode(_selectedBuilderRecoveryContextMode)} mode for {BuilderOperatorIntentService.GetIntentLabel(_selectedBuilderRecoveryIntent)}. Show violating options: {_showBuilderRecoveryViolatingOptions}.";

    public string SelectedBuilderRecoveryRepoFilter
    {
        get => _selectedBuilderRecoveryRepoFilter;
        set
        {
            if (string.Equals(_selectedBuilderRecoveryRepoFilter, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBuilderRecoveryRepoFilter = value;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryRepoFilter));
            ApplyBuilderRecoveryFilters();
        }
    }

    public string SelectedBuilderRecoveryFailureClassFilter
    {
        get => _selectedBuilderRecoveryFailureClassFilter;
        set
        {
            if (string.Equals(_selectedBuilderRecoveryFailureClassFilter, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBuilderRecoveryFailureClassFilter = value;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryFailureClassFilter));
            ApplyBuilderRecoveryFilters();
        }
    }

    public string SelectedBuilderRecoveryRouteFilter
    {
        get => _selectedBuilderRecoveryRouteFilter;
        set
        {
            if (string.Equals(_selectedBuilderRecoveryRouteFilter, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBuilderRecoveryRouteFilter = value;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryRouteFilter));
            ApplyBuilderRecoveryFilters();
        }
    }

    public string SelectedBuilderRecoverySeverityFilter
    {
        get => _selectedBuilderRecoverySeverityFilter;
        set
        {
            if (string.Equals(_selectedBuilderRecoverySeverityFilter, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBuilderRecoverySeverityFilter = value;
            OnPropertyChanged(nameof(SelectedBuilderRecoverySeverityFilter));
            ApplyBuilderRecoveryFilters();
        }
    }

    public string SelectedBuilderRecoveryScopeFilter
    {
        get => _selectedBuilderRecoveryScopeFilter;
        set
        {
            if (string.Equals(_selectedBuilderRecoveryScopeFilter, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBuilderRecoveryScopeFilter = value;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryScopeFilter));
            ApplyBuilderRecoveryFilters();
        }
    }

    public string SelectedBuilderRecoveryBlockingStateFilter
    {
        get => _selectedBuilderRecoveryBlockingStateFilter;
        set
        {
            if (string.Equals(_selectedBuilderRecoveryBlockingStateFilter, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBuilderRecoveryBlockingStateFilter = value;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryBlockingStateFilter));
            ApplyBuilderRecoveryFilters();
        }
    }

    public string SelectedBuilderRecoveryContextMode
    {
        get => _selectedBuilderRecoveryContextMode;
        set
        {
            if (string.Equals(_selectedBuilderRecoveryContextMode, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBuilderRecoveryContextMode = value;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryContextMode));
            ApplyBuilderRecoveryFilters();
        }
    }

    public string SelectedBuilderRecoveryIntent
    {
        get => _selectedBuilderRecoveryIntent;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedBuilderRecoveryIntent, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderRecoveryIntent = normalized;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryIntent));
            if (!_isHydratingBuilderRecoveryIntentSelection)
            {
                ApplyBuilderRecoveryIntentSelection();
            }
        }
    }

    public string SelectedBuilderRecoveryConstraintProfile
    {
        get => _selectedBuilderRecoveryConstraintProfile;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedBuilderRecoveryConstraintProfile, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderRecoveryConstraintProfile = normalized;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryConstraintProfile));
            if (!_isHydratingBuilderRecoveryConstraintSelection)
            {
                ApplyBuilderRecoveryConstraintProfileSelection();
            }
        }
    }

    public string SelectedBuilderRecoveryConstraintRoute
    {
        get => _selectedBuilderRecoveryConstraintRoute;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_selectedBuilderRecoveryConstraintRoute, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBuilderRecoveryConstraintRoute = normalized;
            OnPropertyChanged(nameof(SelectedBuilderRecoveryConstraintRoute));
            if (!_isHydratingBuilderRecoveryConstraintSelection && _builderRecoveryConstraintBlockSelectedRouteEnabled)
            {
                SaveActiveBuilderRecoveryConstraintProfile();
            }
        }
    }

    public string BuilderRecoveryConstraintNewProfileName
    {
        get => _builderRecoveryConstraintNewProfileName;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_builderRecoveryConstraintNewProfileName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _builderRecoveryConstraintNewProfileName = normalized;
            OnPropertyChanged(nameof(BuilderRecoveryConstraintNewProfileName));
            CreateBuilderRecoveryConstraintProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowBuilderRecoveryViolatingOptions
    {
        get => _showBuilderRecoveryViolatingOptions;
        set
        {
            if (_showBuilderRecoveryViolatingOptions == value)
            {
                return;
            }

            _showBuilderRecoveryViolatingOptions = value;
            OnPropertyChanged(nameof(ShowBuilderRecoveryViolatingOptions));
            ApplyBuilderRecoveryFilters();
        }
    }

    public bool BuilderRecoveryConstraintBlockHighRiskFilesEnabled
    {
        get => _builderRecoveryConstraintBlockHighRiskFilesEnabled;
        set => SetBuilderRecoveryConstraintFlag(ref _builderRecoveryConstraintBlockHighRiskFilesEnabled, value, nameof(BuilderRecoveryConstraintBlockHighRiskFilesEnabled));
    }

    public bool BuilderRecoveryConstraintBlockSelectedRouteEnabled
    {
        get => _builderRecoveryConstraintBlockSelectedRouteEnabled;
        set => SetBuilderRecoveryConstraintFlag(ref _builderRecoveryConstraintBlockSelectedRouteEnabled, value, nameof(BuilderRecoveryConstraintBlockSelectedRouteEnabled));
    }

    public bool BuilderRecoveryConstraintBlockCrossRepoActionsEnabled
    {
        get => _builderRecoveryConstraintBlockCrossRepoActionsEnabled;
        set => SetBuilderRecoveryConstraintFlag(ref _builderRecoveryConstraintBlockCrossRepoActionsEnabled, value, nameof(BuilderRecoveryConstraintBlockCrossRepoActionsEnabled));
    }

    public bool BuilderRecoveryConstraintLimitToSingleRepoEnabled
    {
        get => _builderRecoveryConstraintLimitToSingleRepoEnabled;
        set => SetBuilderRecoveryConstraintFlag(ref _builderRecoveryConstraintLimitToSingleRepoEnabled, value, nameof(BuilderRecoveryConstraintLimitToSingleRepoEnabled));
    }

    public bool BuilderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled
    {
        get => _builderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled;
        set => SetBuilderRecoveryConstraintFlag(ref _builderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled, value, nameof(BuilderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled));
    }

    public bool BuilderRecoveryConstraintBlockPartialOrchestrationEnabled
    {
        get => _builderRecoveryConstraintBlockPartialOrchestrationEnabled;
        set => SetBuilderRecoveryConstraintFlag(ref _builderRecoveryConstraintBlockPartialOrchestrationEnabled, value, nameof(BuilderRecoveryConstraintBlockPartialOrchestrationEnabled));
    }

    public AsyncRelayCommand OpenBuilderRecoveryArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderRecoveryRankingArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderRecoveryContextFilterArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderRecoveryIntentArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenBuilderRecoveryConstraintArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand CreateBuilderRecoveryConstraintProfileCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryPlaybookRow> SelectBuilderRecoveryPlaybookCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow> OpenBuilderRecoveryArtifactLinkCommand { get; private set; } = null!;

    private void InitializeBuilderRecoverySurface()
    {
        BuilderRecoveryPlaybooks = new ReadOnlyObservableCollection<BuilderRecoveryPlaybookRow>(_builderRecoveryPlaybooks);
        BuilderRecoveryRepoFilterOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryRepoFilterOptions);
        BuilderRecoveryFailureClassFilterOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryFailureClassFilterOptions);
        BuilderRecoveryRouteFilterOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryRouteFilterOptions);
        BuilderRecoverySeverityFilterOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoverySeverityFilterOptions);
        BuilderRecoveryScopeFilterOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryScopeFilterOptions);
        BuilderRecoveryBlockingStateFilterOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryBlockingStateFilterOptions);
        BuilderRecoveryContextModeOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryContextModeOptions);
        BuilderRecoveryIntentOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryIntentOptions);
        BuilderRecoveryConstraintProfileOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryConstraintProfileOptions);
        BuilderRecoveryConstraintRouteOptions = new ReadOnlyObservableCollection<BuilderRecoveryOptionRow>(_builderRecoveryConstraintRouteOptions);
        BuilderRecoverySelectedSteps = new ReadOnlyObservableCollection<BuilderRecoveryPlaybookStepRecord>(_builderRecoverySelectedSteps);
        BuilderRecoverySelectedArtifactLinks = new ReadOnlyObservableCollection<BuilderRecoveryArtifactLinkRow>(_builderRecoverySelectedArtifactLinks);
        BuilderRecoverySelectedRankingBreakdown = new ReadOnlyObservableCollection<string>(_builderRecoverySelectedRankingBreakdown);
        BuilderRecoverySelectedSignalContributions = new ReadOnlyObservableCollection<string>(_builderRecoverySelectedSignalContributions);
        BuilderRecoverySelectedContextFlags = new ReadOnlyObservableCollection<string>(_builderRecoverySelectedContextFlags);
        BuilderRecoveryCoordinationRelations = new ReadOnlyObservableCollection<string>(_builderRecoveryCoordinationRelations);
        BuilderRecoveryCoordinationStagingSuggestions = new ReadOnlyObservableCollection<string>(_builderRecoveryCoordinationStagingSuggestions);
        BuilderRecoveryCoordinationNotes = new ReadOnlyObservableCollection<string>(_builderRecoveryCoordinationNotes);
        BuilderRecoveryActiveConstraints = new ReadOnlyObservableCollection<string>(_builderRecoveryActiveConstraints);
        OpenBuilderRecoveryArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoveryArtifactPath), () => HasBuilderRecoveryArtifactPath);
        OpenBuilderRecoveryRankingArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoveryRankingArtifactPath), () => HasBuilderRecoveryRankingArtifactPath);
        OpenBuilderRecoveryContextFilterArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoveryContextFilterArtifactPath), () => HasBuilderRecoveryContextFilterArtifactPath);
        OpenBuilderRecoveryIntentArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoveryIntentArtifactPath), () => HasBuilderRecoveryIntentArtifactPath);
        OpenBuilderRecoveryConstraintArtifactCommand = new AsyncRelayCommand(() => OpenPathIfExistsAsync(_builderRecoveryConstraintArtifactPath), () => HasBuilderRecoveryConstraintArtifactPath);
        CreateBuilderRecoveryConstraintProfileCommand = new AsyncRelayCommand(CreateBuilderRecoveryConstraintProfileAsync, () => !string.IsNullOrWhiteSpace(_builderRecoveryConstraintNewProfileName));
        SelectBuilderRecoveryPlaybookCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryPlaybookRow>(SelectBuilderRecoveryPlaybookAsync, row => row is not null);
        OpenBuilderRecoveryArtifactLinkCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<BuilderRecoveryArtifactLinkRow>(OpenBuilderRecoveryArtifactLinkAsync, row => row is not null && File.Exists(row.Path));
        ResetBuilderRecoveryFilterOptions();
    }

    private void LoadBuilderRecoveryArtifacts(BuilderCrossRepoOrchestrationContext? orchestration = null)
    {
        BuilderRecoveryPlaybooksRecord? artifact = null;
        if (orchestration is not null && _builderWorkspaceOptions.Count > 0)
        {
            var descriptors = _builderWorkspaceOptions
                .Select(option => BuilderWorkspaceService.CreateDescriptor(option.RepoRoot, option.RepoName))
                .ToArray();
            artifact = BuilderRecoveryPlaybookService.RefreshRecoveryPlaybooks(
                descriptors,
                orchestration,
                _selectedBuilderWorkspaceId,
                orchestration.Plan.RequestId);
        }
        else
        {
            var repoRoot = GetBuilderWorkspaceRepoRoot();
            artifact = BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(repoRoot);
        }

        if (artifact is null)
        {
            ResetBuilderRecoveryState();
            return;
        }

        _builderRecoverySummary = artifact.Summary;
        _builderRecoveryCoordinationSummary = artifact.CrossRepoCoordination.Summary;
        _builderRecoveryCoordinationOrder = artifact.CrossRepoCoordination.RecoveryOrderSummary;
        _builderRecoveryBlockingReposSummary = artifact.CrossRepoCoordination.BlockingRepoIds.Count == 0
            ? "No blocking repos recorded."
            : $"Blocking repos: {string.Join(", ", artifact.CrossRepoCoordination.BlockingRepoIds)}.";
        _builderRecoveryAffectedReposSummary = artifact.CrossRepoCoordination.AffectedRepoIds.Count == 0
            ? "No affected repos recorded."
            : $"Affected repos: {string.Join(", ", artifact.CrossRepoCoordination.AffectedRepoIds)}.";
        _builderRecoveryArtifactPath = artifact.ArtifactPath;
        _builderRecoveryCoordinationRelations.Clear();
        foreach (var relation in artifact.CrossRepoCoordination.UpstreamDownstreamRelations)
        {
            _builderRecoveryCoordinationRelations.Add(relation);
        }

        _builderRecoveryCoordinationStagingSuggestions.Clear();
        foreach (var suggestion in artifact.CrossRepoCoordination.StagingSuggestions)
        {
            _builderRecoveryCoordinationStagingSuggestions.Add(suggestion);
        }

        _builderRecoveryCoordinationNotes.Clear();
        foreach (var note in artifact.CrossRepoCoordination.RepoIndependenceNotes)
        {
            _builderRecoveryCoordinationNotes.Add(note);
        }

        LoadBuilderRecoveryConstraintArtifact(artifact.Playbooks.SelectMany(playbook => playbook.AppliesToRoutes).ToArray());
        LoadBuilderRecoveryIntentArtifact();
        LoadBuilderRecoverySimulationArtifacts(artifact, orchestration);
        var rankingArtifact = LoadBuilderRecoveryRankingArtifacts(artifact);
        var contextFilterArtifact = LoadBuilderRecoveryContextFilterArtifacts(artifact, rankingArtifact);
        var simulationArtifact = BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot());
        var accuracyArtifact = BuilderSimulationAccuracyService.LoadSimulationAccuracy(GetBuilderWorkspaceRepoRoot());
        LoadBuilderRecoveryComparisonArtifacts(artifact, simulationArtifact, rankingArtifact, accuracyArtifact, contextFilterArtifact);
        var comparisonArtifact = BuilderRecoveryComparisonService.LoadRecoveryComparisons(GetBuilderWorkspaceRepoRoot());
        LoadBuilderDecisionJustificationArtifacts(artifact, simulationArtifact, rankingArtifact, contextFilterArtifact, comparisonArtifact, accuracyArtifact);
        LoadBuilderExecutionReadinessArtifacts(artifact, simulationArtifact, rankingArtifact, contextFilterArtifact, comparisonArtifact, accuracyArtifact, BuilderDecisionJustificationService.LoadDecisionJustifications(GetBuilderWorkspaceRepoRoot()));
        var rankingIndex = (rankingArtifact?.Rankings ?? Array.Empty<BuilderPlaybookRankingRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var contextFilterIndex = (contextFilterArtifact?.RelevanceScores ?? Array.Empty<BuilderPlaybookContextFilterEntryRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _builderRecoveryAllPlaybooks = OrderBuilderRecoveryRows(
                artifact.Playbooks
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

    private BuilderPlaybookRankingsRecord? LoadBuilderRecoveryRankingArtifacts(BuilderRecoveryPlaybooksRecord? playbookArtifact = null)
    {
        playbookArtifact ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(GetBuilderWorkspaceRepoRoot());
        if (playbookArtifact is null)
        {
            _builderRecoveryRankingSummary = "No evidence-weighted playbook ranking recorded.";
            _builderRecoveryRankingArtifactPath = string.Empty;
            return null;
        }

        LoadBuilderRecoveryIntentArtifact();
        var artifact = BuilderPlaybookRankingService.RefreshPlaybookRankings(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact);
        _builderRecoveryRankingSummary = artifact?.Summary ?? "No evidence-weighted playbook ranking recorded.";
        _builderRecoveryRankingArtifactPath = artifact?.ArtifactPath ?? string.Empty;
        return artifact;
    }

    private BuilderPlaybookContextFiltersRecord? LoadBuilderRecoveryContextFilterArtifacts(
        BuilderRecoveryPlaybooksRecord? playbookArtifact = null,
        BuilderPlaybookRankingsRecord? rankingArtifact = null)
    {
        playbookArtifact ??= BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(GetBuilderWorkspaceRepoRoot());
        if (playbookArtifact is null)
        {
            _builderRecoveryContextFilterSummary = "No contextual playbook filter recorded.";
            _builderRecoveryContextSnapshotSummary = "No contextual workspace snapshot recorded.";
            _builderRecoveryContextFilterArtifactPath = string.Empty;
            return null;
        }

        rankingArtifact ??= BuilderPlaybookRankingService.LoadPlaybookRankings(GetBuilderWorkspaceRepoRoot());
        var artifact = BuilderPlaybookContextFilterService.RefreshContextFilters(
            GetBuilderWorkspaceRepoRoot(),
            playbookArtifact,
            rankingArtifact);
        _builderRecoveryContextFilterSummary = artifact?.Summary ?? "No contextual playbook filter recorded.";
        _builderRecoveryContextSnapshotSummary = artifact?.ContextSnapshot.Summary ?? "No contextual workspace snapshot recorded.";
        _builderRecoveryContextFilterArtifactPath = artifact?.ArtifactPath ?? string.Empty;
        return artifact;
    }

    private static IReadOnlyList<BuilderRecoveryPlaybookRow> OrderBuilderRecoveryRows(IEnumerable<BuilderRecoveryPlaybookRow> rows)
        => rows
            .OrderBy(row => row.RankingPositionSort)
            .ThenByDescending(row => row.RankingScoreSort)
            .ThenBy(row => row.ViolatesConstraints ? 1 : 0)
            .ThenBy(row => row.SeverityRank)
            .ThenBy(row => row.FailureClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.RepoScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PrimaryRoute, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void ResetBuilderRecoveryState()
    {
        _builderRecoverySummary = "No builder recovery playbooks recorded.";
        _builderRecoveryCoordinationSummary = "No cross-repo recovery guidance recorded.";
        _builderRecoveryCoordinationOrder = "No coordinated recovery order recorded.";
        _builderRecoveryBlockingReposSummary = "No blocking repos recorded.";
        _builderRecoveryAffectedReposSummary = "No affected repos recorded.";
        _builderRecoveryArtifactPath = string.Empty;
        _builderRecoveryRankingSummary = "No evidence-weighted playbook ranking recorded.";
        _builderRecoveryRankingArtifactPath = string.Empty;
        _builderRecoveryContextFilterSummary = "No contextual playbook filter recorded.";
        _builderRecoveryContextSnapshotSummary = "No contextual workspace snapshot recorded.";
        _builderRecoveryContextFilterArtifactPath = string.Empty;
        _builderRecoveryIntentSummary = "No explicit operator intent recorded.";
        _builderRecoveryIntentArtifactPath = string.Empty;
        _builderRecoveryConstraintSummary = "No operator constraint profile recorded.";
        _builderRecoveryConstraintArtifactPath = string.Empty;
        _builderRecoveryConstraintVisibilitySummary = "No recovery options are currently hidden by constraints.";
        _builderRecoveryConstraintNewProfileName = string.Empty;
        _builderRecoveryAllPlaybooks = Array.Empty<BuilderRecoveryPlaybookRow>();
        _builderRecoveryPlaybooks.Clear();
        _builderRecoverySelectedSteps.Clear();
        _builderRecoverySelectedArtifactLinks.Clear();
        _builderRecoverySelectedRankingBreakdown.Clear();
        _builderRecoverySelectedSignalContributions.Clear();
        _builderRecoverySelectedContextFlags.Clear();
        _builderRecoveryCoordinationRelations.Clear();
        _builderRecoveryCoordinationStagingSuggestions.Clear();
        _builderRecoveryCoordinationNotes.Clear();
        _builderRecoveryActiveConstraints.Clear();
        _selectedBuilderRecoveryPlaybook = null;
        _selectedBuilderRecoveryIntent = string.Empty;
        ResetBuilderRecoverySimulationState();
        ResetBuilderRecoveryComparisonState();
        ResetBuilderExecutionReadinessState();
        ResetBuilderPreventativeGuardrailState();
        ResetBuilderAutoSuggestionState();
        ResetBuilderDecisionJustificationState();
        ResetBuilderRecoveryFilterOptions();
        NotifyBuilderRecoveryStateChanged();
    }

    private void ApplyBuilderRecoveryFilters()
    {
        var filtered = OrderBuilderRecoveryRows(_builderRecoveryAllPlaybooks.Where(MatchesBuilderRecoveryFilters));

        _builderRecoveryPlaybooks.Clear();
        foreach (var row in filtered)
        {
            _builderRecoveryPlaybooks.Add(row);
        }

        UpdateBuilderRecoveryConstraintVisibilitySummary();
        var selectedId = _selectedBuilderRecoveryPlaybook?.PlaybookId;
        var nextSelected = filtered.FirstOrDefault(row => string.Equals(row.PlaybookId, selectedId, StringComparison.OrdinalIgnoreCase))
                           ?? filtered.FirstOrDefault();
        ApplySelectedBuilderRecoveryPlaybook(nextSelected);
        NotifyBuilderRecoveryStateChanged();
    }

    private bool MatchesBuilderRecoveryFilters(BuilderRecoveryPlaybookRow row)
    {
        if (!_showBuilderRecoveryViolatingOptions && row.ViolatesConstraints)
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderRecoveryRepoFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.RepoScope, _selectedBuilderRecoveryRepoFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderRecoveryFailureClassFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.FailureClass, _selectedBuilderRecoveryFailureClassFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderRecoveryRouteFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !row.Routes.Contains(_selectedBuilderRecoveryRouteFilter, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderRecoverySeverityFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.Severity, _selectedBuilderRecoverySeverityFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(_selectedBuilderRecoveryScopeFilter, "cross_repo_only", StringComparison.OrdinalIgnoreCase) && !row.IsCrossRepo)
        {
            return false;
        }

        if (string.Equals(_selectedBuilderRecoveryScopeFilter, "workspace_only", StringComparison.OrdinalIgnoreCase) && row.IsCrossRepo)
        {
            return false;
        }

        if (!string.Equals(_selectedBuilderRecoveryBlockingStateFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.CurrentBlockingState, _selectedBuilderRecoveryBlockingStateFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(_selectedBuilderRecoveryContextMode, BuilderPlaybookContextFilterService.ShowRelevantModeId, StringComparison.OrdinalIgnoreCase) &&
            !row.IsRelevantContext)
        {
            return false;
        }

        if (string.Equals(_selectedBuilderRecoveryContextMode, BuilderPlaybookContextFilterService.ShowHighPriorityOnlyModeId, StringComparison.OrdinalIgnoreCase) &&
            !row.IsHighPriorityContext)
        {
            return false;
        }

        return true;
    }

    private void ApplySelectedBuilderRecoveryPlaybook(BuilderRecoveryPlaybookRow? row)
    {
        _selectedBuilderRecoveryPlaybook = row;
        _builderRecoverySelectedSteps.Clear();
        _builderRecoverySelectedArtifactLinks.Clear();
        _builderRecoverySelectedRankingBreakdown.Clear();
        _builderRecoverySelectedSignalContributions.Clear();
        _builderRecoverySelectedContextFlags.Clear();
        if (row is not null)
        {
            foreach (var step in row.Playbook.RecommendedSteps)
            {
                _builderRecoverySelectedSteps.Add(step);
            }

            foreach (var link in row.ArtifactLinks)
            {
                _builderRecoverySelectedArtifactLinks.Add(link);
            }

            foreach (var detail in row.RankingBreakdown)
            {
                _builderRecoverySelectedRankingBreakdown.Add(detail);
            }

            foreach (var detail in row.SignalContributionSummaries)
            {
                _builderRecoverySelectedSignalContributions.Add(detail);
            }

            foreach (var flag in row.ContextFlags)
            {
                _builderRecoverySelectedContextFlags.Add(flag);
            }
        }

        ApplyBuilderRecoverySimulationSelection();
        ApplyBuilderRecoveryComparisonSelection();
        ApplyBuilderDecisionJustificationSelection();
        SyncBuilderPredictiveDriftSelection();
        NotifyBuilderPredictiveDriftStateChanged();
        if (!_isApplyingPreventativeGuardrailOverlay &&
            !_isApplyingBuilderAutoSuggestionOverlay &&
            !_isApplyingBuilderTrustOverlay &&
            !_isApplyingBuilderPredictiveDriftOverlay)
        {
            LoadBuilderExecutionReadinessArtifacts();
        }
    }

    private void RefreshBuilderRecoveryRankingState()
    {
        if (_builderRecoveryAllPlaybooks.Length == 0)
        {
            return;
        }

        var playbookArtifact = BuilderRecoveryPlaybookService.LoadRecoveryPlaybooks(GetBuilderWorkspaceRepoRoot());
        LoadBuilderRecoveryIntentArtifact();
        LoadBuilderRecoveryConstraintArtifact(playbookArtifact?.Playbooks.SelectMany(playbook => playbook.AppliesToRoutes).ToArray());
        var rankingArtifact = LoadBuilderRecoveryRankingArtifacts(playbookArtifact);
        var contextFilterArtifact = LoadBuilderRecoveryContextFilterArtifacts(playbookArtifact, rankingArtifact);
        LoadBuilderRecoverySimulationArtifacts(playbookArtifact);
        LoadBuilderRecoveryComparisonArtifacts(playbookArtifact, BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot()), rankingArtifact, BuilderSimulationAccuracyService.LoadSimulationAccuracy(GetBuilderWorkspaceRepoRoot()), contextFilterArtifact);
        LoadBuilderDecisionJustificationArtifacts(playbookArtifact, BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot()), rankingArtifact, contextFilterArtifact, BuilderRecoveryComparisonService.LoadRecoveryComparisons(GetBuilderWorkspaceRepoRoot()), BuilderSimulationAccuracyService.LoadSimulationAccuracy(GetBuilderWorkspaceRepoRoot()));
        LoadBuilderExecutionReadinessArtifacts(playbookArtifact, BuilderRecoverySimulationService.LoadRecoverySimulations(GetBuilderWorkspaceRepoRoot()), rankingArtifact, contextFilterArtifact, BuilderRecoveryComparisonService.LoadRecoveryComparisons(GetBuilderWorkspaceRepoRoot()), BuilderSimulationAccuracyService.LoadSimulationAccuracy(GetBuilderWorkspaceRepoRoot()), BuilderDecisionJustificationService.LoadDecisionJustifications(GetBuilderWorkspaceRepoRoot()));
        if (playbookArtifact is null || rankingArtifact is null)
        {
            return;
        }

        var rankingIndex = rankingArtifact.Rankings
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var contextFilterIndex = (contextFilterArtifact?.RelevanceScores ?? Array.Empty<BuilderPlaybookContextFilterEntryRecord>())
            .GroupBy(entry => entry.PlaybookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _builderRecoveryAllPlaybooks = OrderBuilderRecoveryRows(
                playbookArtifact.Playbooks.Select(playbook => new BuilderRecoveryPlaybookRow(
                    playbook,
                    rankingIndex.TryGetValue(playbook.PlaybookId, out var ranking) ? ranking : null,
                    contextFilterIndex.TryGetValue(playbook.PlaybookId, out var contextFilter) ? contextFilter : null,
                    BuildBuilderPreventativeGuardrailPresentation(
                        playbookId: playbook.PlaybookId,
                        route: playbook.AppliesToRoutes.FirstOrDefault() ?? string.Empty),
                    BuildBuilderAutoSuggestionPresentation(playbookId: playbook.PlaybookId),
                    BuildBuilderTrustPresentation(playbookId: playbook.PlaybookId),
                    BuildBuilderPredictiveDriftPresentation(playbookId: playbook.PlaybookId))))
            .ToArray();
        PopulateBuilderRecoveryFilterOptions(_builderRecoveryAllPlaybooks);
        ApplyBuilderRecoveryFilters();
    }

    private void LoadBuilderRecoveryIntentArtifact()
    {
        var artifact = BuilderOperatorIntentService.LoadOperatorIntent(GetBuilderWorkspaceRepoRoot());
        _builderRecoveryIntentSummary = artifact?.Summary ?? "No explicit operator intent recorded.";
        _builderRecoveryIntentArtifactPath = artifact?.ArtifactPath ?? string.Empty;

        _isHydratingBuilderRecoveryIntentSelection = true;
        try
        {
            PopulateBuilderRecoveryIntentOptions(artifact is null);
            _selectedBuilderRecoveryIntent = artifact?.Intent ?? string.Empty;
        }
        finally
        {
            _isHydratingBuilderRecoveryIntentSelection = false;
        }
    }

    private void LoadBuilderRecoveryConstraintArtifact(IEnumerable<string>? routes = null)
    {
        var artifact = BuilderOperatorConstraintService.LoadOperatorConstraints(GetBuilderWorkspaceRepoRoot());
        var activeProfile = BuilderOperatorConstraintService.ResolveActiveProfile(artifact);
        _builderRecoveryConstraintSummary = artifact?.Summary ?? "No operator constraint profile recorded.";
        _builderRecoveryConstraintArtifactPath = artifact?.ArtifactPath ?? string.Empty;

        _builderRecoveryActiveConstraints.Clear();
        foreach (var constraint in activeProfile?.Constraints.Select(constraint => constraint.Summary) ?? Array.Empty<string>())
        {
            _builderRecoveryActiveConstraints.Add(constraint);
        }

        var routeValues = routes?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        _isHydratingBuilderRecoveryConstraintSelection = true;
        try
        {
            PopulateBuilderRecoveryConstraintProfileOptions(artifact);
            PopulateBuilderRecoveryConstraintRouteOptions(routeValues, activeProfile);
            _selectedBuilderRecoveryConstraintProfile = activeProfile?.ProfileId ?? string.Empty;
            var blockedRoute = activeProfile?.Constraints.FirstOrDefault(constraint =>
                string.Equals(constraint.ConstraintType, BuilderOperatorConstraintService.BlockSpecificRouteConstraint, StringComparison.OrdinalIgnoreCase));
            _selectedBuilderRecoveryConstraintRoute = blockedRoute?.ConstraintValue
                ?? _builderRecoveryConstraintRouteOptions.FirstOrDefault()?.Value
                ?? string.Empty;
            _builderRecoveryConstraintBlockHighRiskFilesEnabled = HasConstraint(activeProfile, BuilderOperatorConstraintService.BlockHighRiskFilesConstraint);
            _builderRecoveryConstraintBlockSelectedRouteEnabled = blockedRoute is not null;
            _builderRecoveryConstraintBlockCrossRepoActionsEnabled = HasConstraint(activeProfile, BuilderOperatorConstraintService.BlockCrossRepoActionsConstraint);
            _builderRecoveryConstraintLimitToSingleRepoEnabled = HasConstraint(activeProfile, BuilderOperatorConstraintService.LimitToSingleRepoConstraint);
            _builderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled = HasConstraint(activeProfile, BuilderOperatorConstraintService.BlockFinalizeUntilReviewCleanConstraint);
            _builderRecoveryConstraintBlockPartialOrchestrationEnabled = HasConstraint(activeProfile, BuilderOperatorConstraintService.BlockPartialOrchestrationConstraint);
        }
        finally
        {
            _isHydratingBuilderRecoveryConstraintSelection = false;
        }

        UpdateBuilderRecoveryConstraintVisibilitySummary();
    }

    private void ApplyBuilderRecoveryIntentSelection()
    {
        if (string.IsNullOrWhiteSpace(_selectedBuilderRecoveryIntent))
        {
            NotifyBuilderRecoveryStateChanged();
            NotifyBuilderRecoverySimulationStateChanged();
            return;
        }

        if (!BuilderOperatorIntentService.IsSupportedIntent(_selectedBuilderRecoveryIntent))
        {
            return;
        }

        BuilderOperatorIntentService.SetOperatorIntent(GetBuilderWorkspaceRepoRoot(), _selectedBuilderRecoveryIntent);
        LoadBuilderRecoveryIntentArtifact();
        RefreshBuilderRecoveryRankingState();
        NotifyBuilderRecoveryStateChanged();
        NotifyBuilderRecoverySimulationStateChanged();
    }

    private void ApplyBuilderRecoveryConstraintProfileSelection()
    {
        var artifact = BuilderOperatorConstraintService.LoadOperatorConstraints(GetBuilderWorkspaceRepoRoot());
        if (artifact is null || string.IsNullOrWhiteSpace(_selectedBuilderRecoveryConstraintProfile))
        {
            NotifyBuilderRecoveryStateChanged();
            NotifyBuilderRecoverySimulationStateChanged();
            return;
        }

        BuilderOperatorConstraintService.SetActiveProfile(
            GetBuilderWorkspaceRepoRoot(),
            _selectedBuilderRecoveryConstraintProfile);
        LoadBuilderRecoveryConstraintArtifact(_builderRecoveryAllPlaybooks.SelectMany(row => row.Routes).ToArray());
        RefreshBuilderRecoveryRankingState();
        NotifyBuilderRecoveryStateChanged();
        NotifyBuilderRecoverySimulationStateChanged();
    }

    private void SaveActiveBuilderRecoveryConstraintProfile()
    {
        if (_isHydratingBuilderRecoveryConstraintSelection)
        {
            return;
        }

        var existing = BuilderOperatorConstraintService.LoadOperatorConstraints(GetBuilderWorkspaceRepoRoot());
        var activeProfile = BuilderOperatorConstraintService.ResolveActiveProfile(existing);
        var profileName = !string.IsNullOrWhiteSpace(activeProfile?.ProfileName)
            ? activeProfile.ProfileName
            : "Active Constraints";
        var constraints = BuildSelectedBuilderRecoveryConstraints();
        var artifact = BuilderOperatorConstraintService.CreateOrUpdateProfile(
            GetBuilderWorkspaceRepoRoot(),
            profileName,
            constraints,
            makeActive: true);
        LoadBuilderRecoveryConstraintArtifact(_builderRecoveryAllPlaybooks.SelectMany(row => row.Routes).ToArray());
        _selectedBuilderRecoveryConstraintProfile = artifact.ActiveProfileId;
        RefreshBuilderRecoveryRankingState();
        NotifyBuilderRecoveryStateChanged();
        NotifyBuilderRecoverySimulationStateChanged();
    }

    private IReadOnlyList<BuilderOperatorConstraintRecord> BuildSelectedBuilderRecoveryConstraints()
    {
        var constraints = new List<BuilderOperatorConstraintRecord>();
        if (_builderRecoveryConstraintBlockHighRiskFilesEnabled)
        {
            constraints.Add(BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockHighRiskFilesConstraint));
        }

        if (_builderRecoveryConstraintBlockSelectedRouteEnabled && !string.IsNullOrWhiteSpace(_selectedBuilderRecoveryConstraintRoute))
        {
            constraints.Add(BuilderOperatorConstraintService.CreateConstraint(
                BuilderOperatorConstraintService.BlockSpecificRouteConstraint,
                _selectedBuilderRecoveryConstraintRoute,
                "route"));
        }

        if (_builderRecoveryConstraintBlockCrossRepoActionsEnabled)
        {
            constraints.Add(BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockCrossRepoActionsConstraint));
        }

        if (_builderRecoveryConstraintLimitToSingleRepoEnabled)
        {
            constraints.Add(BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.LimitToSingleRepoConstraint, scope: "repo"));
        }

        if (_builderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled)
        {
            constraints.Add(BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockFinalizeUntilReviewCleanConstraint));
        }

        if (_builderRecoveryConstraintBlockPartialOrchestrationEnabled)
        {
            constraints.Add(BuilderOperatorConstraintService.CreateConstraint(BuilderOperatorConstraintService.BlockPartialOrchestrationConstraint));
        }

        return constraints;
    }

    private void SetBuilderRecoveryConstraintFlag(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (!_isHydratingBuilderRecoveryConstraintSelection)
        {
            SaveActiveBuilderRecoveryConstraintProfile();
        }
    }

    private Task CreateBuilderRecoveryConstraintProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(_builderRecoveryConstraintNewProfileName))
        {
            return Task.CompletedTask;
        }

        BuilderOperatorConstraintService.CreateOrUpdateProfile(
            GetBuilderWorkspaceRepoRoot(),
            _builderRecoveryConstraintNewProfileName,
            BuildSelectedBuilderRecoveryConstraints(),
            makeActive: true);
        _builderRecoveryConstraintNewProfileName = string.Empty;
        LoadBuilderRecoveryConstraintArtifact(_builderRecoveryAllPlaybooks.SelectMany(row => row.Routes).ToArray());
        RefreshBuilderRecoveryRankingState();
        NotifyBuilderRecoveryStateChanged();
        NotifyBuilderRecoverySimulationStateChanged();
        return Task.CompletedTask;
    }

    private void PopulateBuilderRecoveryFilterOptions(IReadOnlyList<BuilderRecoveryPlaybookRow> rows)
    {
        var constraintArtifact = BuilderOperatorConstraintService.LoadOperatorConstraints(GetBuilderWorkspaceRepoRoot());
        var activeConstraintProfile = BuilderOperatorConstraintService.ResolveActiveProfile(constraintArtifact);
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryRepoFilterOptions, "All repos", rows.Select(row => new BuilderRecoveryOptionRow(row.RepoScope, row.RepoScope)));
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryFailureClassFilterOptions, "All failure classes", rows.Select(row => new BuilderRecoveryOptionRow(row.FailureClass, row.FailureClassLabel)));
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryRouteFilterOptions, "All routes", rows.SelectMany(row => row.Routes.Select(route => new BuilderRecoveryOptionRow(route, route))));
        PopulateBuilderRecoveryOptionCollection(_builderRecoverySeverityFilterOptions, "All severities", rows.Select(row => new BuilderRecoveryOptionRow(row.Severity, row.SeverityLabel)));
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryBlockingStateFilterOptions, "All blocking states", rows.Select(row => new BuilderRecoveryOptionRow(row.CurrentBlockingState, row.CurrentBlockingStateLabel)));

        _builderRecoveryScopeFilterOptions.Clear();
        _builderRecoveryScopeFilterOptions.Add(new BuilderRecoveryOptionRow("all", "All scopes"));
        _builderRecoveryScopeFilterOptions.Add(new BuilderRecoveryOptionRow("workspace_only", "Workspace only"));
        _builderRecoveryScopeFilterOptions.Add(new BuilderRecoveryOptionRow("cross_repo_only", "Cross-repo only"));
        PopulateBuilderRecoveryContextModeOptions();
        PopulateBuilderRecoveryIntentOptions(string.IsNullOrWhiteSpace(_selectedBuilderRecoveryIntent));
        PopulateBuilderRecoveryConstraintProfileOptions(constraintArtifact);
        PopulateBuilderRecoveryConstraintRouteOptions(rows.SelectMany(row => row.Routes).ToArray(), activeConstraintProfile);

        _selectedBuilderRecoveryRepoFilter = EnsureBuilderRecoveryFilterValue(_selectedBuilderRecoveryRepoFilter, _builderRecoveryRepoFilterOptions);
        _selectedBuilderRecoveryFailureClassFilter = EnsureBuilderRecoveryFilterValue(_selectedBuilderRecoveryFailureClassFilter, _builderRecoveryFailureClassFilterOptions);
        _selectedBuilderRecoveryRouteFilter = EnsureBuilderRecoveryFilterValue(_selectedBuilderRecoveryRouteFilter, _builderRecoveryRouteFilterOptions);
        _selectedBuilderRecoverySeverityFilter = EnsureBuilderRecoveryFilterValue(_selectedBuilderRecoverySeverityFilter, _builderRecoverySeverityFilterOptions);
        _selectedBuilderRecoveryScopeFilter = EnsureBuilderRecoveryFilterValue(_selectedBuilderRecoveryScopeFilter, _builderRecoveryScopeFilterOptions);
        _selectedBuilderRecoveryBlockingStateFilter = EnsureBuilderRecoveryFilterValue(_selectedBuilderRecoveryBlockingStateFilter, _builderRecoveryBlockingStateFilterOptions);
        _selectedBuilderRecoveryContextMode = EnsureBuilderRecoveryFilterValue(_selectedBuilderRecoveryContextMode, _builderRecoveryContextModeOptions);
        _selectedBuilderRecoveryIntent = EnsureBuilderRecoveryIntentValue(_selectedBuilderRecoveryIntent, _builderRecoveryIntentOptions);
        _selectedBuilderRecoveryConstraintProfile = EnsureBuilderRecoveryConstraintValue(_selectedBuilderRecoveryConstraintProfile, _builderRecoveryConstraintProfileOptions);
        _selectedBuilderRecoveryConstraintRoute = EnsureBuilderRecoveryConstraintValue(_selectedBuilderRecoveryConstraintRoute, _builderRecoveryConstraintRouteOptions);
    }

    private void PopulateBuilderRecoveryOptionCollection(
        ObservableCollection<BuilderRecoveryOptionRow> collection,
        string allLabel,
        IEnumerable<BuilderRecoveryOptionRow> options)
    {
        collection.Clear();
        collection.Add(new BuilderRecoveryOptionRow("all", allLabel));
        foreach (var option in options
                     .Where(option => option is not null && !string.IsNullOrWhiteSpace(option.Value))
                     .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(option => option.Value, StringComparer.OrdinalIgnoreCase))
        {
            collection.Add(option);
        }
    }

    private void PopulateBuilderRecoveryContextModeOptions()
    {
        _builderRecoveryContextModeOptions.Clear();
        _builderRecoveryContextModeOptions.Add(new BuilderRecoveryOptionRow(BuilderPlaybookContextFilterService.ShowAllModeId, "Show All"));
        _builderRecoveryContextModeOptions.Add(new BuilderRecoveryOptionRow(BuilderPlaybookContextFilterService.ShowRelevantModeId, "Show Relevant"));
        _builderRecoveryContextModeOptions.Add(new BuilderRecoveryOptionRow(BuilderPlaybookContextFilterService.ShowHighPriorityOnlyModeId, "Show High Priority Only"));
    }

    private void PopulateBuilderRecoveryIntentOptions(bool includeEmptyOption)
    {
        _builderRecoveryIntentOptions.Clear();
        if (includeEmptyOption)
        {
            _builderRecoveryIntentOptions.Add(new BuilderRecoveryOptionRow(string.Empty, "Select Intent"));
        }

        foreach (var intent in BuilderOperatorIntentService.GetSupportedIntents())
        {
            _builderRecoveryIntentOptions.Add(new BuilderRecoveryOptionRow(intent, BuilderOperatorIntentService.GetIntentLabel(intent)));
        }
    }

    private void PopulateBuilderRecoveryConstraintProfileOptions(BuilderOperatorConstraintsRecord? artifact)
    {
        _builderRecoveryConstraintProfileOptions.Clear();
        _builderRecoveryConstraintProfileOptions.Add(new BuilderRecoveryOptionRow(string.Empty, "No active profile"));
        foreach (var profile in (artifact?.Profiles ?? Array.Empty<BuilderOperatorConstraintProfileRecord>())
                     .OrderBy(entry => entry.ProfileName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.ProfileId, StringComparer.OrdinalIgnoreCase))
        {
            _builderRecoveryConstraintProfileOptions.Add(new BuilderRecoveryOptionRow(profile.ProfileId, profile.ProfileName));
        }
    }

    private void PopulateBuilderRecoveryConstraintRouteOptions(IReadOnlyList<string> routes, BuilderOperatorConstraintProfileRecord? activeProfile)
    {
        _builderRecoveryConstraintRouteOptions.Clear();
        _builderRecoveryConstraintRouteOptions.Add(new BuilderRecoveryOptionRow(string.Empty, "Select Route"));
        var blockedRoute = activeProfile?.Constraints.FirstOrDefault(constraint =>
            string.Equals(constraint.ConstraintType, BuilderOperatorConstraintService.BlockSpecificRouteConstraint, StringComparison.OrdinalIgnoreCase))?.ConstraintValue;
        foreach (var route in routes
                     .Concat(string.IsNullOrWhiteSpace(blockedRoute) ? Array.Empty<string>() : new[] { blockedRoute })
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            _builderRecoveryConstraintRouteOptions.Add(new BuilderRecoveryOptionRow(route, route));
        }
    }

    private void ResetBuilderRecoveryFilterOptions()
    {
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryRepoFilterOptions, "All repos", Array.Empty<BuilderRecoveryOptionRow>());
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryFailureClassFilterOptions, "All failure classes", Array.Empty<BuilderRecoveryOptionRow>());
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryRouteFilterOptions, "All routes", Array.Empty<BuilderRecoveryOptionRow>());
        PopulateBuilderRecoveryOptionCollection(_builderRecoverySeverityFilterOptions, "All severities", Array.Empty<BuilderRecoveryOptionRow>());
        PopulateBuilderRecoveryOptionCollection(_builderRecoveryBlockingStateFilterOptions, "All blocking states", Array.Empty<BuilderRecoveryOptionRow>());
        _builderRecoveryScopeFilterOptions.Clear();
        _builderRecoveryScopeFilterOptions.Add(new BuilderRecoveryOptionRow("all", "All scopes"));
        _builderRecoveryScopeFilterOptions.Add(new BuilderRecoveryOptionRow("workspace_only", "Workspace only"));
        _builderRecoveryScopeFilterOptions.Add(new BuilderRecoveryOptionRow("cross_repo_only", "Cross-repo only"));
        PopulateBuilderRecoveryContextModeOptions();
        PopulateBuilderRecoveryIntentOptions(includeEmptyOption: true);
        PopulateBuilderRecoveryConstraintProfileOptions(null);
        PopulateBuilderRecoveryConstraintRouteOptions(Array.Empty<string>(), null);
        _selectedBuilderRecoveryRepoFilter = "all";
        _selectedBuilderRecoveryFailureClassFilter = "all";
        _selectedBuilderRecoveryRouteFilter = "all";
        _selectedBuilderRecoverySeverityFilter = "all";
        _selectedBuilderRecoveryScopeFilter = "all";
        _selectedBuilderRecoveryBlockingStateFilter = "all";
        _selectedBuilderRecoveryContextMode = BuilderPlaybookContextFilterService.ShowAllModeId;
        _selectedBuilderRecoveryIntent = string.Empty;
        _selectedBuilderRecoveryConstraintProfile = string.Empty;
        _selectedBuilderRecoveryConstraintRoute = string.Empty;
        _showBuilderRecoveryViolatingOptions = false;
        _builderRecoveryConstraintBlockHighRiskFilesEnabled = false;
        _builderRecoveryConstraintBlockSelectedRouteEnabled = false;
        _builderRecoveryConstraintBlockCrossRepoActionsEnabled = false;
        _builderRecoveryConstraintLimitToSingleRepoEnabled = false;
        _builderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled = false;
        _builderRecoveryConstraintBlockPartialOrchestrationEnabled = false;
    }

    private static string EnsureBuilderRecoveryFilterValue(string value, ObservableCollection<BuilderRecoveryOptionRow> options)
        => options.Any(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
            ? value
            : "all";

    private static string EnsureBuilderRecoveryIntentValue(string value, ObservableCollection<BuilderRecoveryOptionRow> options)
        => options.Any(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
            ? value
            : options.FirstOrDefault()?.Value ?? string.Empty;

    private static string EnsureBuilderRecoveryConstraintValue(string value, ObservableCollection<BuilderRecoveryOptionRow> options)
        => options.Any(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
            ? value
            : options.FirstOrDefault()?.Value ?? string.Empty;

    private static bool HasConstraint(BuilderOperatorConstraintProfileRecord? profile, string constraintType)
        => profile?.Constraints.Any(constraint =>
               string.Equals(constraint.ConstraintType, constraintType, StringComparison.OrdinalIgnoreCase)) ?? false;

    private static string FormatBuilderRecoveryContextMode(string value)
        => value switch
        {
            BuilderPlaybookContextFilterService.ShowRelevantModeId => "Show Relevant",
            BuilderPlaybookContextFilterService.ShowHighPriorityOnlyModeId => "Show High Priority Only",
            _ => "Show All"
        };

    private Task SelectBuilderRecoveryPlaybookAsync(BuilderRecoveryPlaybookRow? row)
    {
        _builderDecisionJustificationPreferredTargetType = "playbook";
        ApplySelectedBuilderRecoveryPlaybook(row);
        SyncBuilderPreventativeGuardrailSelection();
        NotifyBuilderPredictiveDriftStateChanged();
        NotifyBuilderRecoveryStateChanged();
        NotifyBuilderDecisionJustificationStateChanged();
        return Task.CompletedTask;
    }

    private Task OpenBuilderRecoveryArtifactLinkAsync(BuilderRecoveryArtifactLinkRow? row)
        => row is null ? Task.CompletedTask : OpenPathIfExistsAsync(row.Path);

    private void NotifyBuilderRecoveryStateChanged()
    {
        OnPropertyChanged(nameof(HasBuilderRecoveryPlaybooks));
        OnPropertyChanged(nameof(BuilderRecoverySummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySummary));
        OnPropertyChanged(nameof(BuilderRecoveryAdvisoryBanner));
        OnPropertyChanged(nameof(BuilderRecoveryCoordinationSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryCoordinationSummary));
        OnPropertyChanged(nameof(BuilderRecoveryCoordinationOrder));
        OnPropertyChanged(nameof(HasBuilderRecoveryCoordinationOrder));
        OnPropertyChanged(nameof(BuilderRecoveryBlockingReposSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryBlockingReposSummary));
        OnPropertyChanged(nameof(BuilderRecoveryAffectedReposSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryAffectedReposSummary));
        OnPropertyChanged(nameof(BuilderRecoveryArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoveryArtifactPath));
        OnPropertyChanged(nameof(BuilderRecoveryRankingSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryRankingSummary));
        OnPropertyChanged(nameof(BuilderRecoveryRankingArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoveryRankingArtifactPath));
        OnPropertyChanged(nameof(BuilderRecoveryContextFilterSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryContextFilterSummary));
        OnPropertyChanged(nameof(BuilderRecoveryContextSnapshotSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryContextSnapshotSummary));
        OnPropertyChanged(nameof(BuilderRecoveryContextFilterArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoveryContextFilterArtifactPath));
        OnPropertyChanged(nameof(BuilderRecoveryIntentSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryIntentSummary));
        OnPropertyChanged(nameof(BuilderRecoveryIntentArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoveryIntentArtifactPath));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintSummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryConstraintSummary));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintArtifactPath));
        OnPropertyChanged(nameof(HasBuilderRecoveryConstraintArtifactPath));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintVisibilitySummary));
        OnPropertyChanged(nameof(HasBuilderRecoveryConstraintVisibilitySummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedTitle));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedContextSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedRankingSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedRankingSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedRankingIndicator));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedRankingIndicator));
        OnPropertyChanged(nameof(BuilderRecoverySelectedContextualSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedContextualSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedContextReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedContextReason));
        OnPropertyChanged(nameof(BuilderRecoverySelectedConstraintSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedConstraintSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedRiskEscalationSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedRiskEscalationSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedRiskEscalationReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedRiskEscalationReason));
        OnPropertyChanged(nameof(BuilderRecoverySelectedTrustSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedTrustSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedTrustReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedTrustReason));
        OnPropertyChanged(nameof(BuilderRecoverySelectedPredictiveRiskSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedPredictiveRiskSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedPredictiveRiskReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedPredictiveRiskReason));
        OnPropertyChanged(nameof(BuilderRecoverySelectedSignalBalanceSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSignalBalanceSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedIntentSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedIntentSummary));
        OnPropertyChanged(nameof(BuilderRecoverySelectedIntentReason));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedIntentReason));
        OnPropertyChanged(nameof(BuilderRecoverySelectedEvidenceBasis));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedEvidenceBasis));
        OnPropertyChanged(nameof(BuilderRecoverySelectedGateSummary));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedPlaybook));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSteps));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedArtifactLinks));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedRankingBreakdown));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedSignalContributions));
        OnPropertyChanged(nameof(HasBuilderRecoverySelectedContextFlags));
        OnPropertyChanged(nameof(HasBuilderRecoveryCoordinationRelations));
        OnPropertyChanged(nameof(HasBuilderRecoveryCoordinationStagingSuggestions));
        OnPropertyChanged(nameof(HasBuilderRecoveryCoordinationNotes));
        OnPropertyChanged(nameof(HasBuilderRecoveryActiveConstraints));
        OnPropertyChanged(nameof(BuilderRecoveryFilterSummary));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryRepoFilter));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryFailureClassFilter));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryRouteFilter));
        OnPropertyChanged(nameof(SelectedBuilderRecoverySeverityFilter));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryScopeFilter));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryBlockingStateFilter));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryContextMode));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryIntent));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryConstraintProfile));
        OnPropertyChanged(nameof(SelectedBuilderRecoveryConstraintRoute));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintNewProfileName));
        OnPropertyChanged(nameof(ShowBuilderRecoveryViolatingOptions));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintBlockHighRiskFilesEnabled));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintBlockSelectedRouteEnabled));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintBlockCrossRepoActionsEnabled));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintLimitToSingleRepoEnabled));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintBlockFinalizeUntilReviewCleanEnabled));
        OnPropertyChanged(nameof(BuilderRecoveryConstraintBlockPartialOrchestrationEnabled));
        OpenBuilderRecoveryArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderRecoveryRankingArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderRecoveryContextFilterArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderRecoveryIntentArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderRecoveryConstraintArtifactCommand.RaiseCanExecuteChanged();
        CreateBuilderRecoveryConstraintProfileCommand.RaiseCanExecuteChanged();
    }

    private void UpdateBuilderRecoveryConstraintVisibilitySummary()
    {
        var activeProfileLabel = _builderRecoveryConstraintProfileOptions.FirstOrDefault(option =>
            string.Equals(option.Value, _selectedBuilderRecoveryConstraintProfile, StringComparison.OrdinalIgnoreCase))?.Label ?? "No active profile";
        var violatingPlaybooks = _builderRecoveryAllPlaybooks.Count(row => row.ViolatesConstraints);
        var visibleViolatingPlaybooks = _builderRecoveryPlaybooks.Count(row => row.ViolatesConstraints);
        var violatingSimulations = _builderRecoveryAllSimulations.Count(row => row.IsBlockedByConstraints);
        var visibleViolatingSimulations = _builderRecoverySimulations.Count(row => row.IsBlockedByConstraints);

        _builderRecoveryConstraintVisibilitySummary = string.IsNullOrWhiteSpace(_selectedBuilderRecoveryConstraintProfile)
            ? "No operator constraint profile is currently filtering recovery guidance."
            : violatingPlaybooks == 0 && violatingSimulations == 0
                ? $"{activeProfileLabel} does not block the currently loaded playbooks or simulations."
                : _showBuilderRecoveryViolatingOptions
                    ? $"{activeProfileLabel} flags {violatingPlaybooks} playbook(s) and {violatingSimulations} simulation(s) as constraint violations. They remain visible because Show Violating Options is enabled ({visibleViolatingPlaybooks} playbook(s), {visibleViolatingSimulations} simulation(s))."
                    : $"{activeProfileLabel} hides {violatingPlaybooks - visibleViolatingPlaybooks} playbook(s) and {violatingSimulations - visibleViolatingSimulations} simulation(s) by default. Enable Show Violating Options to inspect them without changing state.";
    }
}

public sealed record BuilderRecoveryOptionRow(string Value, string Label);

public sealed record BuilderRecoveryArtifactLinkRow(string Label, string Path);

public sealed record BuilderRecoveryPlaybookRow(
    BuilderRecoveryPlaybookRecord Playbook,
    BuilderPlaybookRankingRecord? Ranking,
    BuilderPlaybookContextFilterEntryRecord? ContextFilter,
    BuilderPreventativeGuardrailPresentation Guardrail,
    BuilderAutoSuggestionPresentation Suggestion,
    BuilderTrustPresentation Trust,
    BuilderPredictiveDriftPresentation PredictiveDrift)
{
    public string PlaybookId => Playbook.PlaybookId;
    public string Header => $"{Playbook.Title} [{SeverityLabel}]";
    public string Summary => Playbook.Summary;
    public string ContextSummary => $"Failure: {FailureClassLabel}. Repo: {RepoScope}. Route: {PrimaryRoute}. Runs: {RunSummary}. Blocking state: {CurrentBlockingStateLabel}. Scope: {(IsCrossRepo ? "cross repo" : "workspace")}.";
    public string RankingBadge => Ranking is null ? "Unranked" : $"#{Ranking.RankingPosition} | {Ranking.RankingScore:0.##}";
    public string RankingSummary => Ranking?.Summary ?? "No evidence-weighted ranking recorded yet.";
    public string RankingIndicatorLabel => Ranking?.ConfidenceIndicator switch
    {
        "high_confidence" => "Historically strong outcome evidence.",
        "low_confidence" => "Low-confidence option with mismatch history.",
        "unstable_confidence" => "Mixed or low-sample evidence.",
        _ => string.Empty
    };
    public IReadOnlyList<string> RankingBreakdown => Ranking?.Breakdown ?? Array.Empty<string>();
    public string IntentAlignmentBadge => Ranking is null || string.IsNullOrWhiteSpace(Ranking.SelectedIntent)
        ? "Intent: not selected"
        : $"{BuilderOperatorIntentService.GetIntentLabel(Ranking.SelectedIntent)} | {Ranking.IntentAlignmentScore:0.##}";
    public string IntentSummary => Ranking is null || string.IsNullOrWhiteSpace(Ranking.SelectedIntent)
        ? "No explicit operator intent is shaping this ranking yet."
        : $"Intent-adjusted score {Ranking.IntentAdjustedScore:0.##} for {BuilderOperatorIntentService.GetIntentLabel(Ranking.SelectedIntent)}. Best for: {BestIntentSummary}.";
    public string IntentReason => Ranking?.IntentReason ?? "No explicit operator intent is currently recorded.";
    public string BestIntentSummary => Ranking?.BestForIntents.Count > 0
        ? string.Join(", ", Ranking.BestForIntents.Select(BuilderOperatorIntentService.GetIntentLabel))
        : "No preferred intent recorded.";
    public string RelevanceBadge => ContextFilter is null ? "Context: show all" : $"{PriorityBandLabel} | {ContextFilter.RelevanceScore:0.##}";
    public string PriorityBand => ContextFilter?.PriorityBand ?? "low";
    public string PriorityBandLabel => PriorityBand switch
    {
        "high" => "High priority",
        "medium" => "Relevant now",
        _ => "Show All only"
    };
    public string ContextualSummary => ContextFilter is null
        ? "No contextual relevance recorded yet."
        : $"Context relevance {ContextFilter.RelevanceScore:0.##} ({PriorityBandLabel}). Base relevance {ContextFilter.BaseRelevanceScore:0.##}. Intent alignment {ContextFilter.IntentAlignmentScore:0.##}. Visibility: {FormatState(ContextFilter.VisibilityState)}. Constraint status: {ConstraintBadge}.";
    public string ContextFilterReason => ContextFilter?.FilterReason ?? "This playbook remains available through Show All even without a strong current-context match.";
    public bool ViolatesConstraints => ContextFilter?.ViolatesConstraints ?? false;
    public string ConstraintBadge => ViolatesConstraints
        ? $"Blocked by {ContextFilter?.ViolatedConstraints.Count ?? 0} constraint(s)"
        : "Constraint compatible";
    public string ViolatedConstraintSummary => ContextFilter?.ViolatedConstraints.Count > 0
        ? string.Join(" ", ContextFilter.ViolatedConstraints)
        : "No operator constraint blocks this playbook.";
    public string ConstraintReason => ContextFilter?.ConstraintReason ?? "No operator constraint blocks this playbook.";
    public bool HasRiskEscalation => Guardrail.HasEscalation;
    public bool HasCriticalRiskEscalation => Guardrail.HasCriticalEscalation;
    public string RiskEscalationBadge => Guardrail.Badge;
    public string RiskEscalationSummary => Guardrail.Summary;
    public string RiskEscalationReason => Guardrail.Reason;
    public string RiskEscalationTriggerSummary => Guardrail.TriggerSummary;
    public bool HasSuggestedRecommendation => Suggestion.HasSuggestion;
    public bool IsPrimarySuggestedRecommendation => Suggestion.IsPrimarySuggestion;
    public string SuggestedRecommendationBadge => Suggestion.Badge;
    public string SuggestedRecommendationSummary => Suggestion.Summary;
    public string SuggestedRecommendationReason => Suggestion.Reason;
    public string SignalBalanceSummary => Suggestion.Summary;
    public IReadOnlyList<string> SignalContributionSummaries => Suggestion.SignalContributionSummaries;
    public bool HasTrustProfile => Trust.HasProfile;
    public string TrustBadge => Trust.Badge;
    public string TrustSummary => Trust.Summary;
    public string TrustReason => Trust.Reason;
    public bool HasPredictedRisk => PredictiveDrift.HasPrediction;
    public bool HasCriticalPredictedRisk => PredictiveDrift.HasCriticalRisk;
    public string PredictedRiskBadge => PredictiveDrift.Badge;
    public string PredictedRiskSummary => PredictiveDrift.Summary;
    public string PredictedRiskReason => PredictiveDrift.Reason;
    public IReadOnlyList<string> ContextFlags => (ContextFilter?.ActiveContextFlags ?? Array.Empty<string>())
        .Select(FormatState)
        .ToArray();
    public string ContextFlagSummary => ContextFlags.Count == 0 ? "No active contextual match flags." : string.Join(", ", ContextFlags);
    public bool IsRelevantContext => ContextFilter is not null &&
                                     !string.Equals(ContextFilter.VisibilityState, "visible_in_show_all_only", StringComparison.OrdinalIgnoreCase);
    public bool IsHighPriorityContext => string.Equals(PriorityBand, "high", StringComparison.OrdinalIgnoreCase);
    public string RepoScope => Playbook.RepoScope;
    public string FailureClass => Playbook.FailureClass;
    public string FailureClassLabel => FormatState(Playbook.FailureClass);
    public string Severity => Playbook.Severity;
    public string SeverityLabel => FormatState(Playbook.Severity);
    public int SeverityRank => Playbook.Severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        _ => 3
    };
    public int RankingPositionSort => Ranking?.RankingPosition ?? int.MaxValue;
    public double RankingScoreSort => Ranking?.RankingScore ?? double.MinValue;
    public bool IsCrossRepo => Playbook.CrossRepoScope;
    public string PrimaryRoute => Playbook.AppliesToRoutes.FirstOrDefault() ?? "not_recorded";
    public IReadOnlyList<string> Routes => Playbook.AppliesToRoutes;
    public string RunSummary => Playbook.AppliesToRunIds.Count == 0 ? "not recorded" : string.Join(", ", Playbook.AppliesToRunIds);
    public string CurrentBlockingState => Playbook.CurrentBlockingState;
    public string CurrentBlockingStateLabel => FormatState(Playbook.CurrentBlockingState);
    public IReadOnlyList<BuilderRecoveryArtifactLinkRow> ArtifactLinks => Playbook.ArtifactLinks
        .Select(path => new BuilderRecoveryArtifactLinkRow(Path.GetFileName(path), path))
        .ToArray();

    private static string FormatState(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('_', ' ');
}
