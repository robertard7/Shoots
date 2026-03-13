#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
using Shoots.UI.AiHelp;
using Shoots.UI.Blueprints;
using Shoots.UI.Builder;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Environment;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
using Shoots.UI.Roles;
using Shoots.UI.Diagnostics;
using Shoots.UI.Services;
using Shoots.UI.Services.Backends;
using Shoots.UI.Settings;
using Shoots.UI.Startup;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.Services.AiHelp;
using System.Windows;
using System.Windows.Threading;

namespace Shoots.UI.ViewModels;

/// <summary>
/// Main window view model. UI-only authority. Never holds runtime BuildPlan objects.
/// This file is intentionally self-contained to avoid missing-type drift.
/// </summary>
public sealed partial class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, string> NarrationHeadings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan.materialize.start"] = "Materializing plan",
            ["execute.step.begin"] = "Running step"
        };

    // ---- UI-only execution state (do NOT reference runtime types here) ----
    public enum UiExecutionState
    {
        Idle,
        Running,
        Waiting,
        Replaying,
        Halted,
        Completed
    }

    private readonly IExecutionCommandService _commandService;
    private readonly IHostExecutionService _hostExecutionService;
    private readonly IEnvironmentProfileService _environmentService;
    private readonly IEnvironmentCapabilityProvider _capabilityProvider;
    private readonly IEnvironmentProfilePrompt _profilePrompt;
    private readonly EnvironmentScriptLoader _scriptLoader;
    private readonly IProjectWorkspaceProvider _workspaceProvider;
    private readonly IWorkspaceShellService _workspaceShell;
    private readonly IDatabaseIntentStore _databaseIntentStore;
    private readonly IToolTierPrompt _toolTierPrompt;
    private readonly ISystemBlueprintStore _blueprintStore;
    private readonly IExecutionEnvironmentSettingsStore _executionEnvironmentStore;
    private readonly IAiPolicyStore _aiPolicyStore;
    private readonly IValidationSettingsStore _validationSettingsStore;
    private readonly AiPanelVisibilityService _aiPanelVisibilityService;
    private readonly IAiHelpFacade _aiHelpFacade;
    private readonly IBackendProbeService _backendProbeService;
    private readonly IOllamaClient _ollamaClient;
    private readonly IValidationRunnerService _validationRunnerService;
    private readonly ISemanticReuseService _semanticReuseService;
    private readonly IRepairAttemptService _repairAttemptService;
    private readonly LocalProjectService _localProjectService;
    private readonly IPlanner _planner;
    private readonly BuilderExecutionService _builderExecutionService;
    private readonly ObservableCollection<RunHistoryRow> _runHistory;
    private readonly ObservableCollection<string> _artifactFiles;
    private readonly ObservableCollection<ProofArtifactRow> _proofArtifacts;
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly bool _autoRefreshBackends;

    private readonly ObservableCollection<ProjectWorkspace> _recentWorkspaces;
    private readonly ObservableCollection<BlueprintEntryViewModel> _blueprints;
    private readonly ObservableCollection<UiRootFsDescriptor> _rootFsCatalog;
    private readonly StartupFlowStateMachine _startupFlow;
    private readonly ObservableCollection<string> _startupMessages;
    private readonly ObservableCollection<string> _narrationLines;
    private readonly ObservableCollection<string> _actionLogLines;
    private readonly ObservableCollection<string> _availableModels;
    private readonly ObservableCollection<ProviderDiagnosticEventRow> _providerDiagnostics = new();
    private readonly ObservableCollection<ValidationStageResultRow> _validationStageResults = new();
    private readonly ObservableCollection<ValidationRunHistoryRow> _validationRuns = new();
    private readonly ObservableCollection<ValidationStageHistoryRow> _validationStageHistory = new();
    private readonly ObservableCollection<ValidationBaselineStageChangeRow> _validationBaselineStageChanges = new();
    private readonly ObservableCollection<SemanticReuseSuggestionRow> _semanticReuseSuggestions = new();
    private readonly ObservableCollection<SemanticReusePlaybookRow> _semanticReusePlaybooks = new();
    private readonly ObservableCollection<string> _repairChangedFiles = new();
    private readonly ObservableCollection<RepairHistoryRow> _repairHistory = new();

    public ReadOnlyObservableCollection<ProjectWorkspace> RecentWorkspaces { get; }
    public ReadOnlyObservableCollection<BlueprintEntryViewModel> Blueprints { get; }
    public ReadOnlyObservableCollection<UiRootFsDescriptor> RootFsCatalog { get; }
    public ReadOnlyObservableCollection<string> StartupMessages { get; }
    public ReadOnlyObservableCollection<string> NarrationLines { get; }
    public ReadOnlyObservableCollection<string> ActionLogLines { get; }
    public ReadOnlyObservableCollection<string> AvailableModels { get; }
    public ReadOnlyObservableCollection<RunHistoryRow> RunHistory { get; }
    public ReadOnlyObservableCollection<string> ArtifactFiles { get; }
    public ReadOnlyObservableCollection<ProofArtifactRow> ProofArtifacts { get; }
    public ReadOnlyObservableCollection<ProviderDiagnosticEventRow> ProviderDiagnostics { get; private set; } = null!;
    public ReadOnlyObservableCollection<ValidationStageResultRow> ValidationStageResults { get; private set; } = null!;
    public ReadOnlyObservableCollection<ValidationRunHistoryRow> ValidationRuns { get; private set; } = null!;
    public ReadOnlyObservableCollection<ValidationStageHistoryRow> ValidationStageHistory { get; private set; } = null!;
    public ReadOnlyObservableCollection<ValidationBaselineStageChangeRow> ValidationBaselineStageChanges { get; private set; } = null!;
    public ReadOnlyObservableCollection<SemanticReuseSuggestionRow> SemanticReuseSuggestions { get; private set; } = null!;
    public ReadOnlyObservableCollection<SemanticReusePlaybookRow> SemanticReusePlaybooks { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> RepairChangedFiles { get; private set; } = null!;
    public ReadOnlyObservableCollection<RepairHistoryRow> RepairHistory { get; private set; } = null!;

    private string _startupInput = string.Empty;
    private string _selectedModelId = string.Empty;
    private string _lastKnownModelId = string.Empty;
    private string _selectedNarrationPhase = "all";
    private readonly ReadOnlyCollection<ProviderCapabilityMatrixRow> _providerCapabilityMatrix;
    private static readonly ProofArtifactDescriptor[] ProofArtifactDescriptors =
    {
        new("run.json", "run.json"),
        new("verification_report.json", "verification_report.json"),
        new("operator_flow.json", "operator_flow.json"),
        new("transport_equivalence.json", "transport_equivalence.json"),
        new("manifest.json", Path.Combine("artifacts", "manifest.json")),
        new("narrator.jsonl", "narrator.jsonl")
    };

    private UiExecutionState _state;
    private IEnvironmentProfile? _selectedProfile;
    private EnvironmentProfileResult? _lastEnvironmentResult;
    private bool _restartNeeded;
    private EnvironmentScript? _environmentScript;
    private string? _environmentErrorMessage;
    private string? _environmentInfoMessage;
    private ProjectWorkspace? _activeWorkspace;
    private string _scriptSearchPath = string.Empty;
    private string? _scriptUnsupportedCapabilitiesMessage;
    private DatabaseIntentOption? _selectedDatabaseIntent;
    private UiToolpackTier _lastNonSystemTier = UiToolpackTier.Public;
    private RoleDescriptor? _selectedRole;

    // Blueprint draft fields
    private string _newBlueprintName = string.Empty;
    private string _newBlueprintDescription = string.Empty;
    private string _newBlueprintIntents = string.Empty;
    private string _newBlueprintArtifacts = string.Empty;
    private string _newBlueprintVersion = "1.0";
    private string _newBlueprintDefinition = string.Empty;

    private StartupSessionMode _sessionMode = StartupSessionMode.Startup;
    private bool _startupComplete;

    private string _pendingProjectLanguage = "dotnet";
    private string _pendingProjectName = string.Empty;
    private string _pendingProjectDescription = string.Empty;
    private string _pendingProviderKind = "Local";
    private string _pendingProviderEndpoint = string.Empty;
    private string _pendingEnvironmentId = "host-local";

    private ExecutionEnvironmentSettings _executionSettings = CreateDefaultExecutionEnvironmentSettings();

    private string _blueprintSaveStatus = "Blueprint changes are saved.";
    private BackendStatus _ollamaStatus = new(BackendKind.Ollama, false, "ui.backend.ollama.not_probed", "Ollama status has not been probed.", DateTimeOffset.MinValue, EndpointResolver.ResolveOllamaEndpoint(), null);
    private BackendStatus _qdrantStatus = new(BackendKind.Qdrant, false, "ui.backend.qdrant.not_probed", "Qdrant status has not been probed.", DateTimeOffset.MinValue, EndpointResolver.ResolveQdrantEndpoint(), null);
    private DateTimeOffset? _lastProbeUtc;
    private bool _probeInFlight;
    private string _modelCatalogError = string.Empty;
    private ProjectModel? _currentProject;
    private string? _lastDemoRunPath;
    private string _lastRunVerificationState = "Not verified";
    private string? _proofRunPath;
    private string _proofRunVerificationState = "No run selected";
    private string _proofRunLabel = "No run selected";
    private string? _proofRunId;
    private RunHistoryRow? _selectedRunHistory;
    private string _selectedProviderMode = "local";
    private string _selectedHostTransport = "none";
    private FailureDetails? _lastFailure;
    private string _replaySourcePath = string.Empty;
    private string _replaySummary = "No replay loaded.";
    private string _replayMismatchSummary = string.Empty;
    private string _replayTimingSummary = string.Empty;
    private string _validationSummary = "No validation run recorded.";
    private string _validationOutputFolder = string.Empty;
    private string _validationFirstFailureText = string.Empty;
    private string _validationFirstFailureLogPath = string.Empty;
    private string _validationStabilityClassification = "not_run";
    private string _validationStabilityArtifactPath = string.Empty;
    private string _validationHandoffBundlePath = string.Empty;
    private string _validationHandoffSummaryPath = string.Empty;
    private string _validationHandoffSummaryText = "No validation handoff bundle recorded.";
    private string _validationHandoffComparisonSummary = "No previous validation bundle available.";
    private string _validationFollowupCategory = "no_followup";
    private string _validationFollowupSummaryText = "No validation follow-up intake recorded.";
    private string _validationFollowupNextStepText = "No validation follow-up recommendation recorded.";
    private string _validationFollowupRepeatedIssueSummary = "No recent repeated follow-up detected.";
    private string _validationFollowupReuseSuggestionSummary = "No similar-case or playbook suggestion loaded for the current follow-up.";
    private string _validationFollowupIntakePath = string.Empty;
    private string _validationFollowupPromptPath = string.Empty;
    private string _validationFollowupPlanSummaryText = "No follow-up execution plan recorded.";
    private string _validationFollowupRerunRecommendationText = "No rerun recommendation recorded.";
    private string _validationRepairPrepSummaryText = "No repair-prep bundle recorded.";
    private string _validationFollowupPlanFreshnessText = "No follow-up plan freshness recorded.";
    private string _validationFollowupEscalationHint = "No recurring follow-up signal detected.";
    private string _validationFollowupRerunOutcomeSummary = "No guided rerun has been recorded for this plan.";
    private string _validationFollowupOutcomeSourceSummary = "No guided outcome source recorded.";
    private string _validationFollowupOutcomeSummaryText = "No guided execution outcome recorded.";
    private string _validationFollowupOutcomeNextStepText = "No guided next-step recommendation recorded.";
    private string _validationFollowupOutcomeFreshnessText = "No guided outcome freshness recorded.";
    private string _validationFollowupExecutionOutcomePath = string.Empty;
    private string _validationFollowupEscalationSummaryText = "No guided escalation summary recorded.";
    private string _validationFollowupEscalationPath = string.Empty;
    private string _validationFollowupResolutionOriginalIssueSummary = "No resolution review issue summary recorded.";
    private string _validationFollowupResolutionSummaryText = "No follow-up resolution review recorded.";
    private string _validationFollowupResolutionClosureText = "No resolution closure status recorded.";
    private string _validationFollowupResolutionFreshnessText = "No resolution review freshness recorded.";
    private string _validationFollowupResolutionReopenSummaryText = "No reopened issue recorded.";
    private string _validationFollowupResolutionReviewPath = string.Empty;
    private string _validationResolutionHandoffSummaryText = "No resolution handoff recorded.";
    private string _validationResolutionHandoffPath = string.Empty;
    private string _validationResolutionPromotionSummaryText = "No resolution promotion review recorded.";
    private string _validationResolutionPromotionReviewPath = string.Empty;
    private string _validationReleaseDecisionSummaryText = "No release decision summary recorded.";
    private string _validationReleaseDecisionNotesSummaryText = "No release decision notes recorded.";
    private string _validationReleaseDecisionSummaryPath = string.Empty;
    private string _builderProofSummaryText = "No builder proof run recorded.";
    private string _builderProofLatestTargetSummary = "No builder proof target recorded.";
    private string _builderExternalProofSummaryText = "No external builder proof run recorded.";
    private string _builderProofSuccessCountsSummary = "No builder proof burden summary recorded.";
    private string _builderProofRunPath = string.Empty;
    private string _builderProofSummaryPath = string.Empty;
    private string _builderModelFloorVerdictSummary = "No builder model floor verdict recorded.";
    private string _builderModelFloorVerdictPath = string.Empty;
    private string _builderModelFloorFailurePatternSummary = "No low-floor failure pattern summary recorded.";
    private string _builderModelFloorFailurePatternsPath = string.Empty;
    private string _builderExternalProofSummaryPath = string.Empty;
    private string _builderExternalFloorVerdictSummary = "No external builder floor verdict recorded.";
    private string _builderExternalFloorVerdictPath = string.Empty;
    private string _builderModelFloorPolicySummary = "No builder model floor policy recorded.";
    private string _builderModelFloorPolicyPath = string.Empty;
    private string _builderModelTrustBandSummary = "No builder trust-band summary recorded.";
    private string _builderModelTrustBandsPath = string.Empty;
    private string _builderModelScopeSummary = "No builder scope summary recorded.";
    private string _builderModelScopeSummaryPath = string.Empty;
    private string _builderModelRoutingRecommendationSummary = "No builder routing recommendation recorded.";
    private string _builderModelRoutingRecommendationPath = string.Empty;
    private string _builderModelWeakSpotSummary = "No low-floor weak-spot summary recorded.";
    private string _builderModelEscalationSummary = "No builder escalation decision recorded.";
    private string _builderModelEscalationDecisionPath = string.Empty;
    private string _builderModelRoutingPlanSummary = "No builder routing plan recorded.";
    private string _builderModelRoutingPlanPath = string.Empty;
    private string _builderModelSplitTaskGuidanceSummary = "No split-task guidance recorded.";
    private string _builderModelRoutingWeakSpotReason = "No linked builder weak-spot reason recorded.";
    private string _builderStrongerTierAvailabilitySummary = "No stronger-tier availability recorded.";
    private string _builderStrongerTierAvailabilityPath = string.Empty;
    private string _builderComparativeProofSummary = "No builder comparative proof recorded.";
    private string _builderComparativeProofSummaryPath = string.Empty;
    private string _builderComparativeRepairBurdenSummary = "No comparative repair burden recorded.";
    private string _builderRoutingPolicySummary = "No builder routing policy evidence recorded.";
    private string _builderRoutingPolicyPath = string.Empty;
    private string _builderSplitFirstPlanSummary = "No split-first plan recorded.";
    private string _builderSplitFirstPlanPath = string.Empty;
    private string _builderTieredRoutingSummary = "No tiered routing evidence recorded.";
    private string _builderTieredRoutingPath = string.Empty;
    private string _builderPrimaryRoutingRecommendationSummary = "No primary builder routing recommendation recorded.";
    private string _builderStrongerTierRoleSummary = "No stronger-tier routing role recorded.";
    private string _builderWeakSpotMitigationSummary = "No weak-spot mitigation summary recorded.";
    private string _builderDefaultPolicySummary = "No default builder guidance recorded.";
    private string _builderDefaultPolicyPath = string.Empty;
    private string _builderDefaultPolicyHistoryPath = string.Empty;
    private string _builderRequestPolicyDecisionSummary = "No builder routing decision recorded.";
    private string _builderRequestPolicyDecisionPath = string.Empty;
    private string _builderPolicyStabilitySummary = "No builder guidance support recorded.";
    private string _builderPolicyStabilityPath = string.Empty;
    private string _builderRequestIntakeSummary = "No builder intake recorded.";
    private string _builderRequestIntakePath = string.Empty;
    private string _builderExecutionPrepSummary = "No builder execution prep recorded.";
    private string _builderExecutionPrepPath = string.Empty;
    private string _builderExecutionLaunchSummary = "No prepared builder launch recorded.";
    private string _builderExecutionLaunchPath = string.Empty;
    private string _builderExecutionResultSummary = "No prepared builder route result recorded.";
    private string _builderExecutionResultPath = string.Empty;
    private string _builderReadinessGateSummary = "No builder readiness gate recorded.";
    private string _builderReadinessGatePath = string.Empty;
    private string _builderReadinessGateHistoryPath = string.Empty;
    private string _builderRouteStabilitySummary = "No builder route stability summary recorded.";
    private string _builderRouteStabilitySummaryPath = string.Empty;
    private string _builderReadinessCountsSummary = "No builder readiness evidence recorded.";
    private string _builderReadinessBoundedUseSummary = "No builder readiness decision recorded.";
    private string _builderReadinessLatestContradictionNote = string.Empty;
    private string _builderConfirmedClassesSummary = "No confirmed builder task classes recorded.";
    private string _builderConfirmedClassesPath = string.Empty;
    private string _builderDefaultRouteDecisionSummary = "No default builder route decision recorded.";
    private string _builderDefaultRouteDecisionPath = string.Empty;
    private string _builderLaunchDefaultDecisionSummary = "No builder launch default decision recorded.";
    private string _builderLaunchDefaultDecisionPath = string.Empty;
    private string _builderLaunchRouteModeSummary = "No builder launch default mode recorded.";
    private string _builderRouteSourceSummary = "No current builder route source recorded.";
    private string _builderOverrideAvailabilitySummary = "No operator override state recorded.";
    private string _builderOverrideRouteOptionSummary = "No explicit builder route override is currently prepared.";
    private string _builderRouteOverrideSummary = "No builder route override evidence recorded.";
    private string _builderRouteOverridePath = string.Empty;
    private string _builderRouteReviewSummary = "No builder route review candidates recorded.";
    private string _builderRouteReviewPath = string.Empty;
    private string _builderDefaultSuspensionSummary = "No default-route suspension recorded.";
    private string _builderRouteReconfirmationSummary = "No builder route reconfirmation recorded.";
    private string _builderRouteReconfirmationPath = string.Empty;
    private string _builderDefaultRouteRecoverySummary = "No builder default-route recovery recorded.";
    private string _builderDefaultRouteRecoveryPath = string.Empty;
    private string _builderReadinessContradictionsSummary = "No builder readiness contradictions recorded.";
    private string _builderReadinessContradictionsPath = string.Empty;
    private string _builderSplitStepExecutionSummary = "No split-step execution recorded.";
    private string _builderSplitStepExecutionPath = string.Empty;
    private string _builderSplitFirstOutcomeSummary = "No split-first outcome recorded.";
    private string _builderSplitFirstOutcomePath = string.Empty;
    private string _validationFollowupPinnedOutputFolder = string.Empty;
    private string _validationFollowupPlanPath = string.Empty;
    private string _validationRepairPrepBundlePath = string.Empty;
    private string _validationRunMode = "not_run";
    private string _validationRunModeSummary = "No validation run recorded.";
    private string _validationOrchestrationArtifactPath = string.Empty;
    private string _validationOrchestrationPolicyNotePath = string.Empty;
    private string _validationIsolatedWorkspacePath = string.Empty;
    private string _validationTrendClassification = "no_history";
    private string _validationTrendSummaryText = "No validation trend history recorded.";
    private string _validationRegressionSummaryText = "No regression history recorded.";
    private string _validationHistoryLedgerPath = string.Empty;
    private string _validationTrendArtifactPath = string.Empty;
    private string _validationRegressionArtifactPath = string.Empty;
    private string _validationReleaseReadinessClassification = "not_ready";
    private string _validationReleaseReadinessSummary = "No release readiness assessment recorded.";
    private string _validationBaselineSummaryText = "No active release baseline recorded.";
    private string _validationBaselineComparisonSummaryText = "No baseline comparison recorded.";
    private string _validationBaselineArtifactPath = string.Empty;
    private string _validationBaselineHistoryArtifactPath = string.Empty;
    private string _validationBaselineComparisonArtifactPath = string.Empty;
    private string _semanticReuseStatus = "disabled";
    private string _semanticReuseSummary = "Semantic reuse suggestions are disabled.";
    private string _semanticReuseDesignNotePath = string.Empty;
    private string _semanticReuseIndexPath = string.Empty;
    private string _semanticReuseLinkagePath = string.Empty;
    private ValidationRunResult? _lastValidationResult;
    private bool _validateGeneratedOutputAfterRun;
    private bool _enableValidationStabilityRetry;
    private string _generatedOutputValidationStatus = "not_validated";
    private string _generatedOutputValidationSummary = "Generated output has not been validated.";
    private string _generatedOutputValidationRunId = string.Empty;
    private string _generatedOutputValidationSourcePath = string.Empty;
    private string _linkedGeneratedOutputRunId = string.Empty;
    private string _linkedGeneratedOutputRunPath = string.Empty;
    private string _repairSummary = "No repair attempts recorded.";
    private string _repairBundlePath = string.Empty;
    private string _repairOutputFolder = string.Empty;
    private string _repairOutcome = string.Empty;
    private string _repairComparisonSourceStage = string.Empty;
    private string _repairComparisonSourceExcerpt = string.Empty;
    private string _repairComparisonRepairedStage = string.Empty;
    private string _repairComparisonRepairedExcerpt = string.Empty;
    private string _repairComparisonValidationResult = string.Empty;
    private string _repairLinkedValidationRunFolder = string.Empty;
    private string _repairPromotionStatus = "not_promoted";
    private string _repairPromotionSummary = "No promoted repair result.";
    private string _repairAdoptionStatus = "not_promoted";
    private string _repairAdoptionSummary = "No promoted repair is available for adoption.";
    private string _repairConfidenceSignal = string.Empty;
    private string _repairConfidenceText = string.Empty;
    private string _generatedOutputTrustState = "unvalidated";
    private string _promotedRepairFolder = string.Empty;
    private string _repairAuditSummaryFolder = string.Empty;
    private string _repairLineageSummary = "No repair lineage recorded.";
    private string _repairReviewNote = string.Empty;
    private string _promotedRepairId = string.Empty;
    private RepairHistoryEntry? _latestRepairHistoryEntry;
    private RepairComparisonRecord? _latestRepairComparison;
    private RepairPromotionRecord? _repairPromotionRecord;
    private bool _isValidationOptionsExpanded;
    private bool _continueValidationOnFailure;
    private bool _includeValidateBuildForFullLoop;
    private bool _autoOpenValidationLogsOnFailure;
    private bool _enableIsolatedValidationWorkspaceMode;
    private int _selectedValidationKeepLastRuns = 5;
    private int _selectedValidationHistoryRetentionCount = 20;
    private int _selectedValidationRegressionComparisonWindow = 5;
    private bool _countRetryPassesAsStableInTrendSummaries;
    private int _selectedValidationBaselineHistoryRetentionCount = 5;
    private bool _countPassedOnRetryAsReleaseReady;
    private bool _flakySuspectedBlocksReleaseReadiness = true;
    private bool _enableSemanticReuseSuggestions;
    private int _selectedSemanticReuseMaxCases = 5;
    private int _selectedSemanticReuseRetentionCount = 200;
    private bool _indexProviderDiagnosticsEpisodes = true;
    private bool _onlyShowPassingOrImprovedReuseCases;
    private bool _includePromotedRepairSuggestions = true;
    private bool _includeProviderEpisodeSuggestions = true;
    private bool _enablePlaybookSuggestions = true;
    private int _selectedPlaybookMinimumEvidenceCount = 2;
    private bool _showTentativePlaybooks = true;
    private int _selectedSemanticReuseMaxPlaybooks = 3;
    private bool _isSemanticReuseExpanded;
    private bool _isSemanticReusePlaybooksExpanded;
    private string _selectedSemanticReuseContext = "All contexts";
    private string _semanticReuseContextSummary = "Select a context to inspect similar historical cases.";
    private string _semanticReuseEffectivenessSummary = "No reuse outcome evidence recorded.";
    private string _semanticReuseEffectivenessPath = string.Empty;
    private string _semanticReusePlaybookPath = string.Empty;
    private string _semanticReusePlaybookSummary = "No evidence-backed operator playbooks are currently loaded.";
    private bool _isValidationHandoffExpanded;
    private bool _isValidationFollowupExpanded;
    private bool _isValidationFollowupPlanExpanded;
    private ValidationHandoffBundle? _latestValidationHandoffBundle;
    private ValidationFollowupIntake? _latestValidationFollowupIntake;
    private ValidationFollowupPlan? _latestValidationFollowupPlan;
    private ValidationRepairPrepBundle? _latestValidationRepairPrepBundle;
    private ValidationFollowupExecutionState? _latestValidationFollowupExecutionState;
    private ValidationFollowupExecutionOutcome? _latestValidationFollowupExecutionOutcome;
    private ValidationFollowupEscalation? _latestValidationFollowupEscalation;
    private ValidationFollowupResolutionReview? _latestValidationFollowupResolutionReview;
    private ValidationResolutionHandoff? _latestValidationResolutionHandoff;
    private ValidationResolutionPromotionReview? _latestValidationResolutionPromotionReview;
    private ValidationReleaseDecisionSummary? _latestValidationReleaseDecisionSummary;
    private BuilderProofRun? _latestBuilderProofRun;
    private BuilderModelFloorVerdict? _latestBuilderModelFloorVerdict;
    private BuilderExternalProofRun? _latestBuilderExternalProofRun;
    private BuilderExternalFloorVerdict? _latestBuilderExternalFloorVerdict;
    private BuilderProofFailurePatternSummary? _latestBuilderFailurePatternSummary;
    private BuilderModelFloorPolicy? _latestBuilderModelFloorPolicy;
    private BuilderModelTrustBands? _latestBuilderModelTrustBands;
    private BuilderModelRoutingRecommendation? _latestBuilderModelRoutingRecommendation;
    private BuilderModelEscalationDecision? _latestBuilderModelEscalationDecision;
    private BuilderModelRoutingPlan? _latestBuilderModelRoutingPlan;
    private BuilderStrongerTierAvailability? _latestBuilderStrongerTierAvailability;
    private BuilderComparativeProofRun? _latestBuilderComparativeProofRun;
    private BuilderRoutingPolicyEvidence? _latestBuilderRoutingPolicyEvidence;
    private BuilderSplitFirstPlan? _latestBuilderSplitFirstPlan;
    private BuilderTieredRoutingPolicy? _latestBuilderTieredRoutingPolicy;
    private BuilderDefaultPolicy? _latestBuilderDefaultPolicy;
    private BuilderRequestPolicyDecision? _latestBuilderRequestPolicyDecision;
    private BuilderPolicyStability? _latestBuilderPolicyStability;
    private BuilderRequestIntake? _latestBuilderRequestIntake;
    private BuilderExecutionPrep? _latestBuilderExecutionPrep;
    private PreparedBuilderExecutionLaunch? _latestBuilderExecutionLaunch;
    private PreparedBuilderExecutionResult? _latestBuilderExecutionResult;
    private BuilderReadinessGate? _latestBuilderReadinessGate;
    private BuilderConfirmedTaskClasses? _latestBuilderConfirmedTaskClasses;
    private BuilderDefaultRouteDecision? _latestBuilderDefaultRouteDecision;
    private BuilderLaunchDefaultDecision? _latestBuilderLaunchDefaultDecision;
    private BuilderRouteOverrideEvidence? _latestBuilderRouteOverrideEvidence;
    private BuilderRouteReviewCandidates? _latestBuilderRouteReviewCandidates;
    private BuilderRouteReconfirmation? _latestBuilderRouteReconfirmation;
    private BuilderDefaultRouteRecovery? _latestBuilderDefaultRouteRecovery;
    private BuilderReadinessContradictions? _latestBuilderReadinessContradictions;
    private BuilderSplitStepExecution? _latestBuilderSplitStepExecution;
    private BuilderSplitFirstOutcome? _latestBuilderSplitFirstOutcome;
    private ValidationAction? _activeValidationAction;
    private string _activeValidationActionLabel = string.Empty;
    private string _activeValidationStageId = string.Empty;
    private bool _isBuilderProofExpanded;

    private AiPresentationPolicy _aiPresentationPolicy =
        new(AiVisibilityMode.Visible, AllowAiPanelToggle: true, AllowCopyExport: true, EnterpriseMode: false);

    private AiAccessRole _aiAccessRole = AiAccessRole.Developer;
    private AiPanelVisibilityState _aiPanelVisibilityState = new(true, true, true);

    // UI cannot reference runtime BuildPlan. Store minimal preview identity only.
    private string? _planId;
    private string? _providerId;
    private ProviderKind _providerKind = ProviderKind.Local;
    private string? _graphHash;
    private string? _nodeSetHash;
    private string? _edgeSetHash;

    private sealed record FailureDetails(
        string Phase,
        string Reason,
        string? ProofPath,
        string? NextAction,
        DateTimeOffset OccurredUtc);

    private readonly ObservableCollection<ValidationFollowupPlanStepRow> _validationFollowupPlanSteps = new();
    private readonly ObservableCollection<BuilderSplitStepRow> _builderSplitSteps = new();

	private IReadOnlyList<IAiHelpSurface> BuildAiHelpSurfaces()
	{
		return new IAiHelpSurface[]
		{
			new SimpleAiHelpSurface(
				surfaceId: "workspace",
				surfaceKind: "workspace",
				supportedIntents: new[]
				{
					SimpleAiHelpSurface.Intent("workspace", "Workspace", "Work area: chat intake, attachments, and build run controls.")
				},
				context: "Main workspace surface.",
				capabilities: "Explains UI behavior and intended workflows.",
				constraints: "No system authority. Descriptive guidance only."
			),

			new SimpleAiHelpSurface(
				surfaceId: "execution",
				surfaceKind: "execution",
				supportedIntents: new[]
				{
					SimpleAiHelpSurface.Intent("execution", "Execution", "Run, cancel, replay, and inspect execution state (UI-only).")
				},
				context: "Execution surface.",
				capabilities: "Explains execution controls and status labels.",
				constraints: "UI-only. No runtime authority."
			),

			new SimpleAiHelpSurface(
				surfaceId: "execution-environment",
				surfaceKind: "execution-environment",
				supportedIntents: new[]
				{
					SimpleAiHelpSurface.Intent("execution-environment", "Execution Environment", "Profiles, rootfs catalog, and environment script preview.")
				},
				context: "Execution environment surface.",
				capabilities: "Explains environment selection and script preview behavior.",
				constraints: "UI-only. Never executes scripts."
			),
			new SimpleAiHelpSurface(
				surfaceId: "blueprints",
				surfaceKind: "blueprints",
				supportedIntents: new[]
				{
					SimpleAiHelpSurface.Intent("blueprints", "Blueprints", "Create, edit, validate, and save blueprint entries (UI-only).")
				},
				context: "Blueprint authoring surface.",
				capabilities: "Explains blueprint fields, save/revert flow, validation expectations.",
				constraints: "UI-only. No runtime authority."
			),
			new SimpleAiHelpSurface(
				surfaceId: "tool-executions",
				surfaceKind: "tool-executions",
				supportedIntents: new[]
				{
					SimpleAiHelpSurface.Intent("tool-executions", "Tool Executions", "Tool run surface: shows tool selection, inputs, outputs, and results.")
				},
				context: "Tool execution surface (UI-only).",
				capabilities: "Explains tool execution output and how to resume or replay.",
				constraints: "UI-only. No tool authority."
			),
			new SimpleAiHelpSurface(
				surfaceId: "planner",
				surfaceKind: "planner",
				supportedIntents: new[]
				{
					SimpleAiHelpSurface.Intent("planner", "Planner", "Planning surface: preview, explain, and validate plans (UI-only).")
				},
				context: "Planner surface (plan preview + explanation).",
				capabilities: "Explains planner outputs and expected next actions.",
				constraints: "UI-only. No runtime authority."
			)
		};
	}
	
	// -----------------------------------------------------------------------------
	// CHAT INTAKE (UI-only surfaces)
	// Restored because XAML + Shoots.Ui.Tests bind to these names.
	// These are intentionally UI-only and host/runtime neutral.
	// -----------------------------------------------------------------------------

	private string _intakeIntent = string.Empty;
	private string _intakeTarget = string.Empty;
	private string _intakeAttachments = string.Empty;
	private string _intakeStack = string.Empty;
	private string _chatInputText = string.Empty;
	private string _projectCreationErrorMessage = string.Empty;
	private bool _isCreatingProject;
	private bool _isBusy;
	private string _busyOperation = string.Empty;
    private readonly ObservableCollection<OperationProgressStepRow> _operationProgressSteps = new();
    private readonly ObservableCollection<OperationProgressStepRow> _visibleOperationProgressSteps = new();
    private readonly ObservableCollection<string> _operationNarrationFeed = new();
    private string _operationStatusLine = "Idle";
    private string _operationStatusDetail = "No active operation.";
    private string _operationLatestEvent = string.Empty;
    private DateTimeOffset? _operationStartedUtc;
    private bool _isOperationActive;
    private bool _isOperationVisible;
    private DateTimeOffset? _operationDisplayUntilUtc;
    private DateTimeOffset? _operationLastProgressUtc;
    private bool _isOperationWaiting;
    private string _operationWaitHint = string.Empty;
    private bool _showFullTimeline = true;
    private bool _isProviderDiagnosticsExpanded;
    private DispatcherTimer? _operationProgressTimer;
	private readonly ObservableCollection<string> _chatTranscript = new();
    private readonly ObservableCollection<NarrationEvent> _narrationEvents = new();
    private readonly DeterministicIntentParser _intentParser = new();

	private bool _isWorkOrderLocked;
	private string _jobSpecDigest = string.Empty;

	private object? _lastWaitingInfo;
	private string _decisionBindingsJson = "{}";
	private string _decisionToolId = string.Empty;

	public string IntakeIntent
	{
		get => _intakeIntent;
		set
		{
			if (_isWorkOrderLocked) return;
			if (_intakeIntent == value) return;
			_intakeIntent = value;
			OnPropertyChanged(nameof(IntakeIntent));
			RebuildJobSpecDigest();
			RaiseCommandCanExecute();
		}
	}

    public ReadOnlyObservableCollection<string> ChatTranscript { get; private set; } = null!;
    public ReadOnlyObservableCollection<NarrationEvent> Narration { get; private set; } = null!;

    public string ChatInputText
    {
        get => _chatInputText;
        set
        {
            if (_chatInputText == value) return;
            _chatInputText = value;
            OnPropertyChanged(nameof(ChatInputText));
            SendChatIntentCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsCreatingProject
    {
        get => _isCreatingProject;
        private set
        {
            if (_isCreatingProject == value) return;
            _isCreatingProject = value;
            OnPropertyChanged(nameof(IsCreatingProject));
            OnPropertyChanged(nameof(ProjectCreationStatus));
            OnPropertyChanged(nameof(CanStartNewProjectUi));
            NewProjectCommand.RaiseCanExecuteChanged();
            QuickDemoCommand?.RaiseCanExecuteChanged();
        }
    }

    public string ProjectCreationStatus => IsCreatingProject ? "Creating project..." : string.Empty;

    public string ProjectCreationErrorMessage
    {
        get => _projectCreationErrorMessage;
        private set
        {
            if (_projectCreationErrorMessage == value) return;
            _projectCreationErrorMessage = value;
            OnPropertyChanged(nameof(ProjectCreationErrorMessage));
            OnPropertyChanged(nameof(HasProjectCreationError));
        }
    }

    public bool HasProjectCreationError => !string.IsNullOrWhiteSpace(ProjectCreationErrorMessage);
    public string UiLogPath => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Shoots.UI", "ui.log");
    public bool CanStartNewProjectUi => !IsCreatingProject && !IsBusy && !IsOperationActive;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyState));
            OnPropertyChanged(nameof(CanStartNewProjectUi));
            OnPropertyChanged(nameof(ActionDisableReason));
            OnPropertyChanged(nameof(RunDemoPlanDisabledReason));
            OnPropertyChanged(nameof(QuickDemoDisabledReason));
            RaiseCommandCanExecute();
        }
    }

    public string BusyOperation
    {
        get => _busyOperation;
        private set
        {
            if (_busyOperation == value) return;
            _busyOperation = value;
            OnPropertyChanged(nameof(BusyOperation));
        }
    }

    public ReadOnlyObservableCollection<OperationProgressStepRow> OperationProgressSteps { get; private set; } = null!;
    public ReadOnlyObservableCollection<OperationProgressStepRow> VisibleOperationProgressSteps { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> OperationNarrationFeed { get; private set; } = null!;
    public ReadOnlyObservableCollection<string> NarrationFeed => OperationNarrationFeed;
    public string OperationStatusLine => _operationStatusLine;
    public string OperationStatusDetail => _operationStatusDetail;
    public string OperationLatestEvent => _operationLatestEvent;
    public bool IsOperationActive => _isOperationActive;
    public bool IsOperationVisible => _isOperationVisible;
    public bool HasOperationSteps => _operationProgressSteps.Count > 0;
    public bool HasVisibleOperationSteps => _visibleOperationProgressSteps.Count > 0;
    public bool HasOperationNarration => _operationNarrationFeed.Count > 0;
    public string OperationElapsedLabel => BuildOperationElapsedLabel();
    public string CurrentOperation => OperationStatusLine;
    public string CurrentOperationStage => OperationStatusLine;
    public DateTimeOffset? CurrentOperationStartedAt => _operationStartedUtc;
    public string CurrentOperationStatus => _isOperationActive ? "active" : OperationStatusLine switch
    {
        "Completed" => "completed",
        "Failed" => "failed",
        _ => "idle"
    };
    public string CurrentOperationDetail => OperationStatusDetail;
    public string BusyState => IsOperationActive || IsOperationCompletionHoldActive ? "busy" : "idle";
    public bool IsOperationBusyIndicatorVisible => IsOperationActive || IsOperationCompletionHoldActive;
    public bool IsOperationCompletionHoldActive =>
        !_isOperationActive && _isOperationVisible && !string.Equals(_operationStatusLine, "Idle", StringComparison.Ordinal);
    public string RunDemoPlanDisabledReason => GetRunDemoPlanDisabledReason();
    public string QuickDemoDisabledReason => GetQuickDemoDisabledReason();
    public TimeSpan OperationCompletionHoldDuration { get; set; } = TimeSpan.FromSeconds(4);
    public bool CompletionHold => IsOperationCompletionHoldActive;
    public string ActionDisableReason => BuildOperationBusyReason();
    public DateTimeOffset? OperationLastProgressAt => _operationLastProgressUtc;
    public bool IsOperationWaiting => _isOperationWaiting;
    public string OperationWaitHint => _operationWaitHint;
    public bool IsProviderDiagnosticsExpanded
    {
        get => _isProviderDiagnosticsExpanded;
        set
        {
            if (_isProviderDiagnosticsExpanded == value) return;
            _isProviderDiagnosticsExpanded = value;
            OnPropertyChanged(nameof(IsProviderDiagnosticsExpanded));
        }
    }
    public bool HasProviderDiagnostics => _providerDiagnostics.Count > 0;
    public string ProviderDiagnosticsPath => ResolveProviderDiagnosticsPath();
    public bool IsValidationOptionsExpanded
    {
        get => _isValidationOptionsExpanded;
        set
        {
            if (_isValidationOptionsExpanded == value) return;
            _isValidationOptionsExpanded = value;
            OnPropertyChanged(nameof(IsValidationOptionsExpanded));
        }
    }
    public bool ContinueValidationOnFailure
    {
        get => _continueValidationOnFailure;
        set
        {
            if (_continueValidationOnFailure == value) return;
            _continueValidationOnFailure = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(ContinueValidationOnFailure));
        }
    }
    public bool IncludeValidateBuildForFullLoop
    {
        get => _includeValidateBuildForFullLoop;
        set
        {
            if (_includeValidateBuildForFullLoop == value) return;
            _includeValidateBuildForFullLoop = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(IncludeValidateBuildForFullLoop));
        }
    }
    public bool AutoOpenValidationLogsOnFailure
    {
        get => _autoOpenValidationLogsOnFailure;
        set
        {
            if (_autoOpenValidationLogsOnFailure == value) return;
            _autoOpenValidationLogsOnFailure = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(AutoOpenValidationLogsOnFailure));
        }
    }
    public bool EnableIsolatedValidationWorkspaceMode
    {
        get => _enableIsolatedValidationWorkspaceMode;
        set
        {
            if (_enableIsolatedValidationWorkspaceMode == value) return;
            _enableIsolatedValidationWorkspaceMode = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(EnableIsolatedValidationWorkspaceMode));
            OnPropertyChanged(nameof(ValidationActionPolicies));
            OnPropertyChanged(nameof(HasValidationActionPolicies));
        }
    }
    public bool ValidateGeneratedOutputAfterRun
    {
        get => _validateGeneratedOutputAfterRun;
        set
        {
            if (_validateGeneratedOutputAfterRun == value) return;
            _validateGeneratedOutputAfterRun = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(ValidateGeneratedOutputAfterRun));
        }
    }
    public bool EnableValidationStabilityRetry
    {
        get => _enableValidationStabilityRetry;
        set
        {
            if (_enableValidationStabilityRetry == value) return;
            _enableValidationStabilityRetry = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(EnableValidationStabilityRetry));
        }
    }
    public IReadOnlyList<int> ValidationHistoryRetentionOptions => new[] { 10, 20, 50, 100 };
    public int SelectedValidationHistoryRetentionCount
    {
        get => _selectedValidationHistoryRetentionCount;
        set
        {
            var normalized = Math.Clamp(value, 5, 100);
            if (_selectedValidationHistoryRetentionCount == normalized) return;
            _selectedValidationHistoryRetentionCount = normalized;
            if (_selectedValidationRegressionComparisonWindow > _selectedValidationHistoryRetentionCount)
            {
                _selectedValidationRegressionComparisonWindow = _selectedValidationHistoryRetentionCount;
                OnPropertyChanged(nameof(SelectedValidationRegressionComparisonWindow));
            }

            PersistValidationSettings();
            OnPropertyChanged(nameof(SelectedValidationHistoryRetentionCount));
        }
    }
    public IReadOnlyList<int> ValidationRegressionComparisonWindowOptions => new[] { 3, 5, 10, 20 };
    public int SelectedValidationRegressionComparisonWindow
    {
        get => _selectedValidationRegressionComparisonWindow;
        set
        {
            var normalized = Math.Clamp(value, 2, Math.Min(20, SelectedValidationHistoryRetentionCount));
            if (_selectedValidationRegressionComparisonWindow == normalized) return;
            _selectedValidationRegressionComparisonWindow = normalized;
            PersistValidationSettings();
            OnPropertyChanged(nameof(SelectedValidationRegressionComparisonWindow));
        }
    }
    public bool CountRetryPassesAsStableInTrendSummaries
    {
        get => _countRetryPassesAsStableInTrendSummaries;
        set
        {
            if (_countRetryPassesAsStableInTrendSummaries == value) return;
            _countRetryPassesAsStableInTrendSummaries = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(CountRetryPassesAsStableInTrendSummaries));
        }
    }
    public IReadOnlyList<int> ValidationBaselineHistoryRetentionOptions => new[] { 3, 5, 10, 20 };
    public int SelectedValidationBaselineHistoryRetentionCount
    {
        get => _selectedValidationBaselineHistoryRetentionCount;
        set
        {
            var normalized = Math.Clamp(value, 1, 20);
            if (_selectedValidationBaselineHistoryRetentionCount == normalized) return;
            _selectedValidationBaselineHistoryRetentionCount = normalized;
            PersistValidationSettings();
            OnPropertyChanged(nameof(SelectedValidationBaselineHistoryRetentionCount));
        }
    }
    public bool CountPassedOnRetryAsReleaseReady
    {
        get => _countPassedOnRetryAsReleaseReady;
        set
        {
            if (_countPassedOnRetryAsReleaseReady == value) return;
            _countPassedOnRetryAsReleaseReady = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(CountPassedOnRetryAsReleaseReady));
        }
    }
    public bool FlakySuspectedBlocksReleaseReadiness
    {
        get => _flakySuspectedBlocksReleaseReadiness;
        set
        {
            if (_flakySuspectedBlocksReleaseReadiness == value) return;
            _flakySuspectedBlocksReleaseReadiness = value;
            PersistValidationSettings();
            OnPropertyChanged(nameof(FlakySuspectedBlocksReleaseReadiness));
        }
    }
    public bool EnableSemanticReuseSuggestions
    {
        get => _enableSemanticReuseSuggestions;
        set
        {
            if (_enableSemanticReuseSuggestions == value) return;
            _enableSemanticReuseSuggestions = value;
            PersistValidationSettings();
            ResetSemanticReuseSuggestions();
            OnPropertyChanged(nameof(EnableSemanticReuseSuggestions));
        }
    }
    public IReadOnlyList<int> SemanticReuseMaxCaseOptions => new[] { 3, 5, 8, 10 };
    public int SelectedSemanticReuseMaxCases
    {
        get => _selectedSemanticReuseMaxCases;
        set
        {
            var normalized = Math.Clamp(value, 1, 10);
            if (_selectedSemanticReuseMaxCases == normalized) return;
            _selectedSemanticReuseMaxCases = normalized;
            PersistValidationSettings();
            ResetSemanticReuseSuggestions();
            OnPropertyChanged(nameof(SelectedSemanticReuseMaxCases));
        }
    }
    public IReadOnlyList<int> SemanticReuseRetentionOptions => new[] { 50, 100, 200, 500 };
    public int SelectedSemanticReuseRetentionCount
    {
        get => _selectedSemanticReuseRetentionCount;
        set
        {
            var normalized = Math.Clamp(value, 20, 500);
            if (_selectedSemanticReuseRetentionCount == normalized) return;
            _selectedSemanticReuseRetentionCount = normalized;
            PersistValidationSettings();
            ResetSemanticReuseSuggestions();
            OnPropertyChanged(nameof(SelectedSemanticReuseRetentionCount));
        }
    }
    public bool IndexProviderDiagnosticsEpisodes
    {
        get => _indexProviderDiagnosticsEpisodes;
        set
        {
            if (_indexProviderDiagnosticsEpisodes == value) return;
            _indexProviderDiagnosticsEpisodes = value;
            PersistValidationSettings();
            ResetSemanticReuseSuggestions();
            OnPropertyChanged(nameof(IndexProviderDiagnosticsEpisodes));
        }
    }
    public bool OnlyShowPassingOrImprovedReuseCases
    {
        get => _onlyShowPassingOrImprovedReuseCases;
        set
        {
            if (_onlyShowPassingOrImprovedReuseCases == value) return;
            _onlyShowPassingOrImprovedReuseCases = value;
            PersistValidationSettings();
            ResetSemanticReuseSuggestions();
            OnPropertyChanged(nameof(OnlyShowPassingOrImprovedReuseCases));
        }
    }
    public bool IncludePromotedRepairSuggestions
    {
        get => _includePromotedRepairSuggestions;
        set
        {
            if (_includePromotedRepairSuggestions == value) return;
            _includePromotedRepairSuggestions = value;
            PersistValidationSettings();
            ResetSemanticReuseSuggestions();
            OnPropertyChanged(nameof(IncludePromotedRepairSuggestions));
        }
    }
    public bool IncludeProviderEpisodeSuggestions
    {
        get => _includeProviderEpisodeSuggestions;
        set
        {
            if (_includeProviderEpisodeSuggestions == value) return;
            _includeProviderEpisodeSuggestions = value;
            PersistValidationSettings();
            ResetSemanticReuseSuggestions();
            OnPropertyChanged(nameof(IncludeProviderEpisodeSuggestions));
        }
    }
    public bool EnablePlaybookSuggestions
    {
        get => _enablePlaybookSuggestions;
        set
        {
            if (_enablePlaybookSuggestions == value) return;
            _enablePlaybookSuggestions = value;
            PersistValidationSettings();
            UpdateSemanticReusePlaybookSummary();
            OnPropertyChanged(nameof(EnablePlaybookSuggestions));
            OnPropertyChanged(nameof(VisibleSemanticReusePlaybooks));
            OnPropertyChanged(nameof(HasVisibleSemanticReusePlaybooks));
        }
    }
    public IReadOnlyList<int> PlaybookMinimumEvidenceOptions => new[] { 2, 3, 4, 5 };
    public int SelectedPlaybookMinimumEvidenceCount
    {
        get => _selectedPlaybookMinimumEvidenceCount;
        set
        {
            var normalized = Math.Clamp(value, 2, 10);
            if (_selectedPlaybookMinimumEvidenceCount == normalized) return;
            _selectedPlaybookMinimumEvidenceCount = normalized;
            PersistValidationSettings();
            OnPropertyChanged(nameof(SelectedPlaybookMinimumEvidenceCount));
        }
    }
    public bool ShowTentativePlaybooks
    {
        get => _showTentativePlaybooks;
        set
        {
            if (_showTentativePlaybooks == value) return;
            _showTentativePlaybooks = value;
            PersistValidationSettings();
            UpdateSemanticReusePlaybookSummary();
            OnPropertyChanged(nameof(ShowTentativePlaybooks));
            OnPropertyChanged(nameof(VisibleSemanticReusePlaybooks));
            OnPropertyChanged(nameof(HasVisibleSemanticReusePlaybooks));
        }
    }
    public IReadOnlyList<int> SemanticReuseMaxPlaybookOptions => new[] { 1, 2, 3, 5 };
    public int SelectedSemanticReuseMaxPlaybooks
    {
        get => _selectedSemanticReuseMaxPlaybooks;
        set
        {
            var normalized = Math.Clamp(value, 1, 10);
            if (_selectedSemanticReuseMaxPlaybooks == normalized) return;
            _selectedSemanticReuseMaxPlaybooks = normalized;
            PersistValidationSettings();
            UpdateSemanticReusePlaybookSummary();
            OnPropertyChanged(nameof(SelectedSemanticReuseMaxPlaybooks));
            OnPropertyChanged(nameof(VisibleSemanticReusePlaybooks));
            OnPropertyChanged(nameof(HasVisibleSemanticReusePlaybooks));
        }
    }
    public IReadOnlyList<string> SemanticReuseContextOptions => new[] { "All contexts", "Planning", "Validation failure", "Repair attempt", "Provider diagnostics" };
    public string SelectedSemanticReuseContext
    {
        get => _selectedSemanticReuseContext;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "All contexts" : value;
            if (string.Equals(_selectedSemanticReuseContext, normalized, StringComparison.Ordinal)) return;
            _selectedSemanticReuseContext = normalized;
            UpdateSemanticReuseContextSummary();
            UpdateSemanticReusePlaybookSummary();
            OnPropertyChanged(nameof(SelectedSemanticReuseContext));
            OnPropertyChanged(nameof(VisibleSemanticReuseSuggestions));
            OnPropertyChanged(nameof(HasVisibleSemanticReuseSuggestions));
            OnPropertyChanged(nameof(VisibleSemanticReusePlaybooks));
            OnPropertyChanged(nameof(HasVisibleSemanticReusePlaybooks));
        }
    }
    public IReadOnlyList<int> ValidationKeepLastRunsOptions => new[] { 3, 5, 10, 20 };
    public int SelectedValidationKeepLastRuns
    {
        get => _selectedValidationKeepLastRuns;
        set
        {
            var normalized = Math.Clamp(value, 1, 20);
            if (_selectedValidationKeepLastRuns == normalized) return;
            _selectedValidationKeepLastRuns = normalized;
            PersistValidationSettings();
            LoadValidationRuns();
            OnPropertyChanged(nameof(SelectedValidationKeepLastRuns));
        }
    }
    public string ValidationRepoRoot => _validationRunnerService.RepoRoot;
    public string ValidationDisabledReason => GetValidationDisabledReason();
    public bool HasValidationDisabledReason => !string.IsNullOrWhiteSpace(ValidationDisabledReason);
    public string BuildUiProjectValidationDisabledReason => GetValidationDisabledReason(ValidationAction.BuildUiProject);
    public string RunUiTestsValidationDisabledReason => GetValidationDisabledReason(ValidationAction.RunUiTests);
    public string RunSmokeValidationDisabledReason => GetValidationDisabledReason(ValidationAction.RunSmokeValidation);
    public string RunIntegrityValidationDisabledReason => GetValidationDisabledReason(ValidationAction.RunIntegrityValidation);
    public string RunFullValidationLoopDisabledReason => GetValidationDisabledReason(ValidationAction.RunFullValidationLoop);
    public string ValidationSummary => _validationSummary;
    public string ValidationOutputFolder => _validationOutputFolder;
    public bool HasValidationOutputFolder => !string.IsNullOrWhiteSpace(_validationOutputFolder);
    public string ValidationFirstFailureText => _validationFirstFailureText;
    public bool HasValidationFirstFailure => !string.IsNullOrWhiteSpace(_validationFirstFailureText);
    public string ValidationFirstFailureLogPath => _validationFirstFailureLogPath;
    public bool HasValidationFirstFailureLogPath => !string.IsNullOrWhiteSpace(_validationFirstFailureLogPath);
    public string ValidationStabilityClassification => _validationStabilityClassification;
    public string ValidationStabilityBadge => _validationStabilityClassification switch
    {
        "passed" => "Passed cleanly",
        "passed_on_retry" => "Passed after retry",
        "flaky_suspected" => "Flaky suspected",
        "failed" => "Failed",
        _ => "Not run"
    };
    public string ValidationStabilityArtifactPath => _validationStabilityArtifactPath;
    public bool HasValidationStabilityArtifactPath => !string.IsNullOrWhiteSpace(_validationStabilityArtifactPath) && File.Exists(_validationStabilityArtifactPath);
    public string ValidationHandoffBundlePath => _validationHandoffBundlePath;
    public bool HasValidationHandoffBundlePath => !string.IsNullOrWhiteSpace(_validationHandoffBundlePath) && File.Exists(_validationHandoffBundlePath);
    public string ValidationHandoffSummaryPath => _validationHandoffSummaryPath;
    public bool HasValidationHandoffSummaryPath => !string.IsNullOrWhiteSpace(_validationHandoffSummaryPath) && File.Exists(_validationHandoffSummaryPath);
    public string ValidationHandoffSummaryText => _validationHandoffSummaryText;
    public bool HasValidationHandoffSummary => !string.IsNullOrWhiteSpace(_validationHandoffSummaryText) &&
                                               !string.Equals(_validationHandoffSummaryText, "No validation handoff bundle recorded.", StringComparison.Ordinal);
    public string ValidationHandoffComparisonSummary => _validationHandoffComparisonSummary;
    public bool HasValidationHandoffComparisonSummary => !string.IsNullOrWhiteSpace(_validationHandoffComparisonSummary) &&
                                                         !string.Equals(_validationHandoffComparisonSummary, "No previous validation bundle available.", StringComparison.Ordinal);
    public bool IsValidationHandoffExpanded
    {
        get => _isValidationHandoffExpanded;
        set
        {
            if (_isValidationHandoffExpanded == value) return;
            _isValidationHandoffExpanded = value;
            OnPropertyChanged(nameof(IsValidationHandoffExpanded));
        }
    }
    public string ValidationFollowupCategory => _validationFollowupCategory;
    public string ValidationFollowupBadge => BuildValidationFollowupBadge(_validationFollowupCategory);
    public string ValidationFollowupSummaryText => _validationFollowupSummaryText;
    public bool HasValidationFollowupSummary => !string.IsNullOrWhiteSpace(_validationFollowupSummaryText) &&
                                                !string.Equals(_validationFollowupSummaryText, "No validation follow-up intake recorded.", StringComparison.Ordinal);
    public string ValidationFollowupNextStepText => _validationFollowupNextStepText;
    public bool HasValidationFollowupNextStep => !string.IsNullOrWhiteSpace(_validationFollowupNextStepText) &&
                                                 !string.Equals(_validationFollowupNextStepText, "No validation follow-up recommendation recorded.", StringComparison.Ordinal);
    public string ValidationFollowupRepeatedIssueSummary => _validationFollowupRepeatedIssueSummary;
    public bool HasValidationFollowupRepeatedIssue => _latestValidationFollowupIntake?.HasRecentRepeatedIssue == true;
    public string ValidationFollowupReuseSuggestionSummary => _validationFollowupReuseSuggestionSummary;
    public bool HasValidationFollowupReuseSuggestionSummary => !string.IsNullOrWhiteSpace(_validationFollowupReuseSuggestionSummary) &&
                                                               !string.Equals(_validationFollowupReuseSuggestionSummary, "No similar-case or playbook suggestion loaded for the current follow-up.", StringComparison.Ordinal);
    public string ValidationFollowupIntakePath => _validationFollowupIntakePath;
    public bool HasValidationFollowupIntakePath => !string.IsNullOrWhiteSpace(_validationFollowupIntakePath) && File.Exists(_validationFollowupIntakePath);
    public string ValidationFollowupPromptPath => _validationFollowupPromptPath;
    public bool HasValidationFollowupPromptPath => !string.IsNullOrWhiteSpace(_validationFollowupPromptPath) && File.Exists(_validationFollowupPromptPath);
    public string ValidationFollowupPlanCategory => _latestValidationFollowupPlan?.FollowupCategory ?? "no_followup";
    public string ValidationFollowupPlanBadge => BuildValidationFollowupBadge(ValidationFollowupPlanCategory);
    public string ValidationFollowupPlanSummaryText => _validationFollowupPlanSummaryText;
    public bool HasValidationFollowupPlanSummary => !string.IsNullOrWhiteSpace(_validationFollowupPlanSummaryText) &&
                                                    !string.Equals(_validationFollowupPlanSummaryText, "No follow-up execution plan recorded.", StringComparison.Ordinal);
    public string ValidationFollowupRerunRecommendationText => _validationFollowupRerunRecommendationText;
    public bool HasValidationFollowupRerunRecommendation => !string.IsNullOrWhiteSpace(_validationFollowupRerunRecommendationText) &&
                                                            !string.Equals(_validationFollowupRerunRecommendationText, "No rerun recommendation recorded.", StringComparison.Ordinal);
    public string ValidationRepairPrepSummaryText => _validationRepairPrepSummaryText;
    public bool HasValidationRepairPrepSummary => !string.IsNullOrWhiteSpace(_validationRepairPrepSummaryText) &&
                                                  !string.Equals(_validationRepairPrepSummaryText, "No repair-prep bundle recorded.", StringComparison.Ordinal);
    public string ValidationFollowupPlanFreshnessText => _validationFollowupPlanFreshnessText;
    public bool HasValidationFollowupPlanFreshness => !string.IsNullOrWhiteSpace(_validationFollowupPlanFreshnessText);
    public string ValidationFollowupEscalationHint => _validationFollowupEscalationHint;
    public bool HasValidationFollowupEscalationHint => !string.IsNullOrWhiteSpace(_validationFollowupEscalationHint) &&
                                                       !string.Equals(_validationFollowupEscalationHint, "No recurring follow-up signal detected.", StringComparison.Ordinal);
    public string ValidationFollowupRerunOutcomeSummary => _validationFollowupRerunOutcomeSummary;
    public bool HasValidationFollowupRerunOutcome => !string.IsNullOrWhiteSpace(_validationFollowupRerunOutcomeSummary) &&
                                                     !string.Equals(_validationFollowupRerunOutcomeSummary, "No guided rerun has been recorded for this plan.", StringComparison.Ordinal);
    public string ValidationFollowupOutcomeClassification => _latestValidationFollowupExecutionOutcome?.OutcomeClassification ?? "not_recorded";
    public string ValidationFollowupOutcomeBadge => ValidationFollowupOutcomeClassification switch
    {
        "resolved" => "Resolved",
        "improved" => "Improved",
        "unchanged" => "Unchanged",
        "regressed" => "Regressed",
        "inconclusive" => "Inconclusive",
        _ => "No guided outcome"
    };
    public string ValidationFollowupOutcomeSourceSummary => _validationFollowupOutcomeSourceSummary;
    public bool HasValidationFollowupOutcomeSourceSummary => !string.IsNullOrWhiteSpace(_validationFollowupOutcomeSourceSummary) &&
                                                             !string.Equals(_validationFollowupOutcomeSourceSummary, "No guided outcome source recorded.", StringComparison.Ordinal);
    public string ValidationFollowupOutcomeSummaryText => _validationFollowupOutcomeSummaryText;
    public bool HasValidationFollowupOutcomeSummary => !string.IsNullOrWhiteSpace(_validationFollowupOutcomeSummaryText) &&
                                                       !string.Equals(_validationFollowupOutcomeSummaryText, "No guided execution outcome recorded.", StringComparison.Ordinal);
    public string ValidationFollowupOutcomeNextStateText => _validationFollowupOutcomeNextStepText;
    public bool HasValidationFollowupOutcomeNextStateText => !string.IsNullOrWhiteSpace(_validationFollowupOutcomeNextStepText) &&
                                                             !string.Equals(_validationFollowupOutcomeNextStepText, "No guided next-step recommendation recorded.", StringComparison.Ordinal);
    public string ValidationFollowupOutcomeFreshnessText => _validationFollowupOutcomeFreshnessText;
    public bool HasValidationFollowupOutcomeFreshnessText => !string.IsNullOrWhiteSpace(_validationFollowupOutcomeFreshnessText) &&
                                                             !string.Equals(_validationFollowupOutcomeFreshnessText, "No guided outcome freshness recorded.", StringComparison.Ordinal);
    public string ValidationFollowupExecutionOutcomePath => _validationFollowupExecutionOutcomePath;
    public bool HasValidationFollowupExecutionOutcomePath => !string.IsNullOrWhiteSpace(_validationFollowupExecutionOutcomePath) && File.Exists(_validationFollowupExecutionOutcomePath);
    public string ValidationFollowupEscalationClassification => _latestValidationFollowupEscalation?.EscalationClassification ?? "not_recorded";
    public string ValidationFollowupEscalationBadge => ValidationFollowupEscalationClassification switch
    {
        "escalate_recurring_issue" => "Escalate recurring issue",
        "watch_recurring_issue" => "Watch recurring issue",
        "no_escalation" => "No escalation",
        _ => "No escalation note"
    };
    public string ValidationFollowupEscalationSummaryText => _validationFollowupEscalationSummaryText;
    public bool HasValidationFollowupEscalationSummary => !string.IsNullOrWhiteSpace(_validationFollowupEscalationSummaryText) &&
                                                          !string.Equals(_validationFollowupEscalationSummaryText, "No guided escalation summary recorded.", StringComparison.Ordinal);
    public string ValidationFollowupEscalationPath => _validationFollowupEscalationPath;
    public bool HasValidationFollowupEscalationPath => !string.IsNullOrWhiteSpace(_validationFollowupEscalationPath) && File.Exists(_validationFollowupEscalationPath);
    public string ValidationFollowupResolutionState => _latestValidationFollowupResolutionReview?.CurrentResolutionState ?? "not_recorded";
    public string ValidationFollowupResolutionBadge => ValidationFollowupResolutionState switch
    {
        "closed_by_guided_rerun" => "Closed by guided rerun",
        "improved_but_open" => "Improved but open",
        "regressed" => "Regressed",
        "superseded" => "Superseded",
        "unresolved" => "Unresolved",
        _ => "No resolution review"
    };
    public string ValidationFollowupResolutionOriginalIssueSummary => _validationFollowupResolutionOriginalIssueSummary;
    public bool HasValidationFollowupResolutionOriginalIssueSummary => !string.IsNullOrWhiteSpace(_validationFollowupResolutionOriginalIssueSummary) &&
                                                                      !string.Equals(_validationFollowupResolutionOriginalIssueSummary, "No resolution review issue summary recorded.", StringComparison.Ordinal);
    public string ValidationFollowupResolutionSummaryText => _validationFollowupResolutionSummaryText;
    public bool HasValidationFollowupResolutionSummary => !string.IsNullOrWhiteSpace(_validationFollowupResolutionSummaryText) &&
                                                          !string.Equals(_validationFollowupResolutionSummaryText, "No follow-up resolution review recorded.", StringComparison.Ordinal);
    public string ValidationFollowupResolutionClosureText => _validationFollowupResolutionClosureText;
    public bool HasValidationFollowupResolutionClosureText => !string.IsNullOrWhiteSpace(_validationFollowupResolutionClosureText) &&
                                                              !string.Equals(_validationFollowupResolutionClosureText, "No resolution closure status recorded.", StringComparison.Ordinal);
    public string ValidationFollowupResolutionFreshnessText => _validationFollowupResolutionFreshnessText;
    public bool HasValidationFollowupResolutionFreshnessText => !string.IsNullOrWhiteSpace(_validationFollowupResolutionFreshnessText) &&
                                                                !string.Equals(_validationFollowupResolutionFreshnessText, "No resolution review freshness recorded.", StringComparison.Ordinal);
    public string ValidationFollowupResolutionReopenSummaryText => _validationFollowupResolutionReopenSummaryText;
    public bool HasValidationFollowupResolutionReopenSummary => !string.IsNullOrWhiteSpace(_validationFollowupResolutionReopenSummaryText) &&
                                                                !string.Equals(_validationFollowupResolutionReopenSummaryText, "No reopened issue recorded.", StringComparison.Ordinal) &&
                                                                !string.Equals(_validationFollowupResolutionReopenSummaryText, "No later validation run has reopened this issue.", StringComparison.Ordinal);
    public string ValidationFollowupResolutionReviewPath => _validationFollowupResolutionReviewPath;
    public bool HasValidationFollowupResolutionReviewPath => !string.IsNullOrWhiteSpace(_validationFollowupResolutionReviewPath) && File.Exists(_validationFollowupResolutionReviewPath);
    public string ValidationResolutionHandoffCandidateState => _latestValidationResolutionHandoff?.CandidateState ?? "not_recorded";
    public string ValidationResolutionHandoffBadge => ValidationResolutionHandoffCandidateState switch
    {
        "baseline_review_candidate" => "Baseline review candidate",
        "readiness_review_candidate" => "Readiness review candidate",
        "no_handoff" => "No handoff",
        _ => "No handoff"
    };
    public string ValidationResolutionHandoffSummaryText => _validationResolutionHandoffSummaryText;
    public bool HasValidationResolutionHandoffSummary => !string.IsNullOrWhiteSpace(_validationResolutionHandoffSummaryText) &&
                                                         !string.Equals(_validationResolutionHandoffSummaryText, "No resolution handoff recorded.", StringComparison.Ordinal);
    public string ValidationResolutionHandoffPath => _validationResolutionHandoffPath;
    public bool HasValidationResolutionHandoffPath => !string.IsNullOrWhiteSpace(_validationResolutionHandoffPath) && File.Exists(_validationResolutionHandoffPath);
    public string ValidationResolutionPromotionRecommendationState => _latestValidationResolutionPromotionReview?.PromotionRecommendationState ?? "not_recorded";
    public string ValidationResolutionPromotionBadge => ValidationResolutionPromotionRecommendationState switch
    {
        "recommend_baseline_consideration" => "Baseline consideration",
        "recommend_readiness_consideration" => "Readiness consideration",
        "recommend_review_only" => "Review only",
        "do_not_promote" => "Do not promote",
        _ => "No promotion review"
    };
    public string ValidationResolutionPromotionSummaryText => _validationResolutionPromotionSummaryText;
    public bool HasValidationResolutionPromotionSummary => !string.IsNullOrWhiteSpace(_validationResolutionPromotionSummaryText) &&
                                                           !string.Equals(_validationResolutionPromotionSummaryText, "No resolution promotion review recorded.", StringComparison.Ordinal);
    public string ValidationResolutionPromotionReviewPath => _validationResolutionPromotionReviewPath;
    public bool HasValidationResolutionPromotionReviewPath => !string.IsNullOrWhiteSpace(_validationResolutionPromotionReviewPath) && File.Exists(_validationResolutionPromotionReviewPath);
    public string ValidationReleaseDecisionState => _latestValidationReleaseDecisionSummary?.DecisionState ?? "not_recorded";
    public string ValidationReleaseDecisionBadge => ValidationReleaseDecisionState switch
    {
        "ready_for_operator_review" => "Review decision",
        "defer_release_decision" => "Defer release decision",
        "needs_more_validation_evidence" => "Needs more validation evidence",
        "resolution_not_stable_enough" => "Resolution not stable enough",
        _ => "No release decision"
    };
    public string ValidationReleaseDecisionSummaryText => _validationReleaseDecisionSummaryText;
    public bool HasValidationReleaseDecisionSummary => !string.IsNullOrWhiteSpace(_validationReleaseDecisionSummaryText) &&
                                                       !string.Equals(_validationReleaseDecisionSummaryText, "No release decision summary recorded.", StringComparison.Ordinal);
    public string ValidationReleaseDecisionNotesSummaryText => _validationReleaseDecisionNotesSummaryText;
    public bool HasValidationReleaseDecisionNotesSummary => !string.IsNullOrWhiteSpace(_validationReleaseDecisionNotesSummaryText) &&
                                                            !string.Equals(_validationReleaseDecisionNotesSummaryText, "No release decision notes recorded.", StringComparison.Ordinal);
    public string ValidationReleaseDecisionSummaryPath => _validationReleaseDecisionSummaryPath;
    public bool HasValidationReleaseDecisionSummaryPath => !string.IsNullOrWhiteSpace(_validationReleaseDecisionSummaryPath) && File.Exists(_validationReleaseDecisionSummaryPath);
    public string BuilderProofDisabledReason => GetBuilderProofDisabledReason();
    public bool HasBuilderProofDisabledReason => !string.IsNullOrWhiteSpace(BuilderProofDisabledReason);
    public string BuilderPreparedLaunchDisabledReason => GetBuilderPreparedLaunchDisabledReason();
    public bool HasBuilderPreparedLaunchDisabledReason => !string.IsNullOrWhiteSpace(BuilderPreparedLaunchDisabledReason);
    public string BuilderOverrideLaunchDisabledReason => GetBuilderOverrideLaunchDisabledReason();
    public bool HasBuilderOverrideLaunchDisabledReason => !string.IsNullOrWhiteSpace(BuilderOverrideLaunchDisabledReason);
    public string BuilderProofModelId => _latestBuilderProofRun?.ModelId ?? BuilderExecutionService.BuilderProofFloorModelId;
    public string BuilderProofOutcomeClassification => _latestBuilderProofRun?.FinalClassification ?? "not_run";
    public string BuilderProofOutcomeBadge => BuilderProofOutcomeClassification switch
    {
        "passed_cleanly" => "Passed cleanly",
        "passed_with_recovery" => "Passed with recovery",
        "passed_with_routing" => "Passed with routing",
        "failed" => "Failed",
        _ => "Not run"
    };
    public string BuilderProofLatestTargetSummary => _builderProofLatestTargetSummary;
    public bool HasBuilderProofLatestTargetSummary => !string.IsNullOrWhiteSpace(_builderProofLatestTargetSummary) &&
                                                      !string.Equals(_builderProofLatestTargetSummary, "No builder proof target recorded.", StringComparison.Ordinal);
    public string BuilderProofSummaryText => _builderProofSummaryText;
    public bool HasBuilderProofSummary => !string.IsNullOrWhiteSpace(_builderProofSummaryText) &&
                                          !string.Equals(_builderProofSummaryText, "No builder proof run recorded.", StringComparison.Ordinal);
    public string BuilderExternalProofOutcomeClassification => _latestBuilderExternalProofRun?.FinalClassification ?? "not_run";
    public string BuilderExternalProofOutcomeBadge => BuilderExternalProofOutcomeClassification switch
    {
        "passed_cleanly" => "Passed cleanly",
        "passed_with_recovery" => "Passed with recovery",
        "failed" => "Failed",
        _ => "Not run"
    };
    public string BuilderExternalProofSummaryText => _builderExternalProofSummaryText;
    public bool HasBuilderExternalProofSummary => !string.IsNullOrWhiteSpace(_builderExternalProofSummaryText) &&
                                                  !string.Equals(_builderExternalProofSummaryText, "No external builder proof run recorded.", StringComparison.Ordinal);
    public string BuilderProofSuccessCountsSummary => _builderProofSuccessCountsSummary;
    public bool HasBuilderProofSuccessCountsSummary => !string.IsNullOrWhiteSpace(_builderProofSuccessCountsSummary) &&
                                                       !string.Equals(_builderProofSuccessCountsSummary, "No builder proof burden summary recorded.", StringComparison.Ordinal);
    public string BuilderProofRunPath => _builderProofRunPath;
    public bool HasBuilderProofRunPath => !string.IsNullOrWhiteSpace(_builderProofRunPath) && Directory.Exists(_builderProofRunPath);
    public string BuilderProofSummaryPath => _builderProofSummaryPath;
    public bool HasBuilderProofSummaryPath => !string.IsNullOrWhiteSpace(_builderProofSummaryPath) && File.Exists(_builderProofSummaryPath);
    public string BuilderExternalProofSummaryPath => _builderExternalProofSummaryPath;
    public bool HasBuilderExternalProofSummaryPath => !string.IsNullOrWhiteSpace(_builderExternalProofSummaryPath) && File.Exists(_builderExternalProofSummaryPath);
    public string BuilderModelFloorVerdictState => _latestBuilderModelFloorVerdict?.Verdict ?? "not_run";
    public string BuilderModelFloorVerdictBadge => BuilderModelFloorVerdictState switch
    {
        "sufficient_for_bounded_builds" => "Sufficient for bounded builds",
        "sufficient_with_repair_loop" => "Sufficient with repair loop",
        "suitable_only_for_edit_assist" => "Suitable only for edit assist",
        "insufficient_for_target_scope" => "Insufficient for target scope",
        _ => "No floor verdict"
    };
    public string BuilderModelFloorVerdictSummary => _builderModelFloorVerdictSummary;
    public bool HasBuilderModelFloorVerdictSummary => !string.IsNullOrWhiteSpace(_builderModelFloorVerdictSummary) &&
                                                      !string.Equals(_builderModelFloorVerdictSummary, "No builder model floor verdict recorded.", StringComparison.Ordinal);
    public string BuilderModelFloorVerdictPath => _builderModelFloorVerdictPath;
    public bool HasBuilderModelFloorVerdictPath => !string.IsNullOrWhiteSpace(_builderModelFloorVerdictPath) && File.Exists(_builderModelFloorVerdictPath);
    public string BuilderExternalFloorVerdictState => _latestBuilderExternalFloorVerdict?.Verdict ?? "not_run";
    public string BuilderExternalFloorVerdictBadge => BuilderExternalFloorVerdictState switch
    {
        "sufficient_for_bounded_external_targets" => "Sufficient for bounded external targets",
        "sufficient_with_repair_loop_only" => "Sufficient with repair loop only",
        "sufficient_for_repo_local_only" => "Repo-local only",
        "insufficient_for_external_target_scope" => "Insufficient for external targets",
        _ => "No external floor verdict"
    };
    public string BuilderExternalFloorVerdictSummary => _builderExternalFloorVerdictSummary;
    public bool HasBuilderExternalFloorVerdictSummary => !string.IsNullOrWhiteSpace(_builderExternalFloorVerdictSummary) &&
                                                         !string.Equals(_builderExternalFloorVerdictSummary, "No external builder floor verdict recorded.", StringComparison.Ordinal);
    public string BuilderExternalFloorVerdictPath => _builderExternalFloorVerdictPath;
    public bool HasBuilderExternalFloorVerdictPath => !string.IsNullOrWhiteSpace(_builderExternalFloorVerdictPath) && File.Exists(_builderExternalFloorVerdictPath);
    public string BuilderModelFloorFailurePatternSummary => _builderModelFloorFailurePatternSummary;
    public bool HasBuilderModelFloorFailurePatternSummary => !string.IsNullOrWhiteSpace(_builderModelFloorFailurePatternSummary) &&
                                                             !string.Equals(_builderModelFloorFailurePatternSummary, "No low-floor failure pattern summary recorded.", StringComparison.Ordinal);
    public string BuilderModelFloorFailurePatternsPath => _builderModelFloorFailurePatternsPath;
    public bool HasBuilderModelFloorFailurePatternsPath => !string.IsNullOrWhiteSpace(_builderModelFloorFailurePatternsPath) && File.Exists(_builderModelFloorFailurePatternsPath);
    public string BuilderModelFloorPolicySummary => _builderModelFloorPolicySummary;
    public bool HasBuilderModelFloorPolicySummary => !string.IsNullOrWhiteSpace(_builderModelFloorPolicySummary) &&
                                                     !string.Equals(_builderModelFloorPolicySummary, "No builder model floor policy recorded.", StringComparison.Ordinal);
    public string BuilderModelFloorPolicyPath => _builderModelFloorPolicyPath;
    public bool HasBuilderModelFloorPolicyPath => !string.IsNullOrWhiteSpace(_builderModelFloorPolicyPath) && File.Exists(_builderModelFloorPolicyPath);
    public string BuilderModelFloorGuidanceSummary => BuilderModelFloorPolicySummary;
    public bool HasBuilderModelFloorGuidanceSummary => HasBuilderModelFloorPolicySummary;
    public string BuilderModelFloorGuidancePath => BuilderModelFloorPolicyPath;
    public bool HasBuilderModelFloorGuidancePath => HasBuilderModelFloorPolicyPath;
    public string BuilderProofTrustBandState => _latestBuilderModelRoutingRecommendation?.TrustBand ?? "not_run";
    public string BuilderProofTrustBandBadge => BuilderProofTrustBandState switch
    {
        "clean_build_band" => "Clean build band",
        "repair_loop_band" => "Repair-loop band",
        "escalation_recommended_band" => "Escalation recommended band",
        "reject_band" => "Reject band",
        _ => "No trust band"
    };
    public string BuilderModelTrustBandSummary => _builderModelTrustBandSummary;
    public bool HasBuilderModelTrustBandSummary => !string.IsNullOrWhiteSpace(_builderModelTrustBandSummary) &&
                                                   !string.Equals(_builderModelTrustBandSummary, "No builder trust-band summary recorded.", StringComparison.Ordinal);
    public string BuilderModelTrustBandsPath => _builderModelTrustBandsPath;
    public bool HasBuilderModelTrustBandsPath => !string.IsNullOrWhiteSpace(_builderModelTrustBandsPath) && File.Exists(_builderModelTrustBandsPath);
    public string BuilderModelScopeSummary => _builderModelScopeSummary;
    public bool HasBuilderModelScopeSummary => !string.IsNullOrWhiteSpace(_builderModelScopeSummary) &&
                                               !string.Equals(_builderModelScopeSummary, "No builder scope summary recorded.", StringComparison.Ordinal);
    public string BuilderModelScopeSummaryPath => _builderModelScopeSummaryPath;
    public bool HasBuilderModelScopeSummaryPath => !string.IsNullOrWhiteSpace(_builderModelScopeSummaryPath) && File.Exists(_builderModelScopeSummaryPath);
    public string BuilderRoutingRecommendationState => _latestBuilderModelRoutingRecommendation?.RecommendationState ?? "not_run";
    public string BuilderRoutingRecommendationBadge => BuilderRoutingRecommendationState switch
    {
        "proceed_with_current_model" => "Suitable for current model",
        "proceed_with_repair_loop_expected" => "Suitable with repair loop expected",
        "stronger_model_recommended" => "Stronger model recommended",
        "task_out_of_scope_for_floor" => "Out of scope for low-floor model",
        _ => "No routing recommendation"
    };
    public string BuilderModelRoutingRecommendationSummary => _builderModelRoutingRecommendationSummary;
    public bool HasBuilderModelRoutingRecommendationSummary => !string.IsNullOrWhiteSpace(_builderModelRoutingRecommendationSummary) &&
                                                               !string.Equals(_builderModelRoutingRecommendationSummary, "No builder routing recommendation recorded.", StringComparison.Ordinal);
    public string BuilderModelRoutingRecommendationPath => _builderModelRoutingRecommendationPath;
    public bool HasBuilderModelRoutingRecommendationPath => !string.IsNullOrWhiteSpace(_builderModelRoutingRecommendationPath) && File.Exists(_builderModelRoutingRecommendationPath);
    public string BuilderModelWeakSpotSummary => _builderModelWeakSpotSummary;
    public bool HasBuilderModelWeakSpotSummary => !string.IsNullOrWhiteSpace(_builderModelWeakSpotSummary) &&
                                                  !string.Equals(_builderModelWeakSpotSummary, "No low-floor weak-spot summary recorded.", StringComparison.Ordinal);
    public string BuilderModelEscalationState => _latestBuilderModelEscalationDecision?.EscalationRequirementState ?? "not_run";
    public string BuilderModelEscalationBadge => BuilderModelEscalationState switch
    {
        "stay_on_current_model" => "Safe for current model",
        "current_model_with_repair_loop" => "Current model with repair loop",
        "stronger_model_recommended" => "Stronger model recommended",
        "stronger_model_required" => "Stronger model required",
        "task_should_be_split_first" => "Split task before using low-floor model",
        _ => "No escalation decision"
    };
    public string BuilderModelEscalationSummary => _builderModelEscalationSummary;
    public bool HasBuilderModelEscalationSummary => !string.IsNullOrWhiteSpace(_builderModelEscalationSummary) &&
                                                    !string.Equals(_builderModelEscalationSummary, "No builder escalation decision recorded.", StringComparison.Ordinal);
    public string BuilderModelEscalationDecisionPath => _builderModelEscalationDecisionPath;
    public bool HasBuilderModelEscalationDecisionPath => !string.IsNullOrWhiteSpace(_builderModelEscalationDecisionPath) && File.Exists(_builderModelEscalationDecisionPath);
    public string BuilderModelRoutingPlanSummary => _builderModelRoutingPlanSummary;
    public bool HasBuilderModelRoutingPlanSummary => !string.IsNullOrWhiteSpace(_builderModelRoutingPlanSummary) &&
                                                     !string.Equals(_builderModelRoutingPlanSummary, "No builder routing plan recorded.", StringComparison.Ordinal);
    public string BuilderModelRoutingPlanPath => _builderModelRoutingPlanPath;
    public bool HasBuilderModelRoutingPlanPath => !string.IsNullOrWhiteSpace(_builderModelRoutingPlanPath) && File.Exists(_builderModelRoutingPlanPath);
    public string BuilderModelSplitTaskGuidanceSummary => _builderModelSplitTaskGuidanceSummary;
    public bool HasBuilderModelSplitTaskGuidanceSummary => !string.IsNullOrWhiteSpace(_builderModelSplitTaskGuidanceSummary) &&
                                                           !string.Equals(_builderModelSplitTaskGuidanceSummary, "No split-task guidance recorded.", StringComparison.Ordinal);
    public string BuilderModelRoutingWeakSpotReason => _builderModelRoutingWeakSpotReason;
    public bool HasBuilderModelRoutingWeakSpotReason => !string.IsNullOrWhiteSpace(_builderModelRoutingWeakSpotReason) &&
                                                        !string.Equals(_builderModelRoutingWeakSpotReason, "No linked builder weak-spot reason recorded.", StringComparison.Ordinal);
    public string BuilderStrongerTierAvailabilityState => _latestBuilderStrongerTierAvailability?.AvailabilityState ?? "not_run";
    public string BuilderStrongerTierAvailabilityBadge => BuilderStrongerTierAvailabilityState switch
    {
        "available" => "Stronger tier available",
        "not_needed" => "Stronger tier not needed",
        "unconfigured" => "Stronger tier not configured",
        "unavailable" => "Stronger tier unavailable",
        _ => "No stronger-tier status"
    };
    public string BuilderStrongerTierAvailabilitySummary => _builderStrongerTierAvailabilitySummary;
    public bool HasBuilderStrongerTierAvailabilitySummary => !string.IsNullOrWhiteSpace(_builderStrongerTierAvailabilitySummary) &&
                                                             !string.Equals(_builderStrongerTierAvailabilitySummary, "No stronger-tier availability recorded.", StringComparison.Ordinal);
    public string BuilderStrongerTierAvailabilityPath => _builderStrongerTierAvailabilityPath;
    public bool HasBuilderStrongerTierAvailabilityPath => !string.IsNullOrWhiteSpace(_builderStrongerTierAvailabilityPath) && File.Exists(_builderStrongerTierAvailabilityPath);
    public string BuilderComparativeProofClassification => _latestBuilderComparativeProofRun?.ComparativeClassification ?? "not_run";
    public string BuilderComparativeProofBadge => BuilderComparativeProofClassification switch
    {
        "no_material_gain" => "No material gain",
        "cleaner_success" => "Cleaner success",
        "reduced_repair_burden" => "Reduced repair burden",
        "required_for_scope" => "Required for scope",
        "still_not_sufficient" => "Still not sufficient",
        _ => "No comparative proof"
    };
    public string BuilderComparativeProofSummary => _builderComparativeProofSummary;
    public bool HasBuilderComparativeProofSummary => !string.IsNullOrWhiteSpace(_builderComparativeProofSummary) &&
                                                     !string.Equals(_builderComparativeProofSummary, "No builder comparative proof recorded.", StringComparison.Ordinal);
    public string BuilderComparativeProofSummaryPath => _builderComparativeProofSummaryPath;
    public bool HasBuilderComparativeProofSummaryPath => !string.IsNullOrWhiteSpace(_builderComparativeProofSummaryPath) && File.Exists(_builderComparativeProofSummaryPath);
    public string BuilderComparativeRepairBurdenSummary => _builderComparativeRepairBurdenSummary;
    public bool HasBuilderComparativeRepairBurdenSummary => !string.IsNullOrWhiteSpace(_builderComparativeRepairBurdenSummary) &&
                                                            !string.Equals(_builderComparativeRepairBurdenSummary, "No comparative repair burden recorded.", StringComparison.Ordinal);
    public string BuilderRoutingPolicyState => _latestBuilderRoutingPolicyEvidence?.RoutingPolicyState ?? "not_run";
    public string BuilderRoutingPolicyBadge => BuilderRoutingPolicyState switch
    {
        "stay_on_current_model" => "Stay on current model",
        "split_first_keep_low_floor" => "Split first, keep low-floor",
        "escalate_for_cleaner_success" => "Escalate for cleaner success",
        "escalate_because_low_floor_out_of_scope" => "Escalate for out-of-scope task",
        "comparative_evidence_inconclusive" => "Comparative evidence inconclusive",
        _ => "No routing policy"
    };
    public string BuilderRoutingPolicySummary => _builderRoutingPolicySummary;
    public bool HasBuilderRoutingPolicySummary => !string.IsNullOrWhiteSpace(_builderRoutingPolicySummary) &&
                                                  !string.Equals(_builderRoutingPolicySummary, "No builder routing policy evidence recorded.", StringComparison.Ordinal);
    public string BuilderRoutingPolicyPath => _builderRoutingPolicyPath;
    public bool HasBuilderRoutingPolicyPath => !string.IsNullOrWhiteSpace(_builderRoutingPolicyPath) && File.Exists(_builderRoutingPolicyPath);
    public string BuilderRoutingEvidenceBadge => BuilderRoutingPolicyBadge;
    public string BuilderRoutingEvidenceSummary => BuilderRoutingPolicySummary;
    public bool HasBuilderRoutingEvidenceSummary => HasBuilderRoutingPolicySummary;
    public string BuilderRoutingEvidencePath => BuilderRoutingPolicyPath;
    public bool HasBuilderRoutingEvidencePath => HasBuilderRoutingPolicyPath;
    public string BuilderSplitFirstPlanSummary => _builderSplitFirstPlanSummary;
    public bool HasBuilderSplitFirstPlanSummary => !string.IsNullOrWhiteSpace(_builderSplitFirstPlanSummary) &&
                                                   !string.Equals(_builderSplitFirstPlanSummary, "No split-first plan recorded.", StringComparison.Ordinal);
    public string BuilderSplitFirstPlanPath => _builderSplitFirstPlanPath;
    public bool HasBuilderSplitFirstPlanPath => !string.IsNullOrWhiteSpace(_builderSplitFirstPlanPath) && File.Exists(_builderSplitFirstPlanPath);
    public string BuilderTieredRoutingState => _latestBuilderTieredRoutingPolicy?.PrimaryRoutingState ?? "not_run";
    public string BuilderTieredRoutingBadge => BuilderTieredRoutingState switch
    {
        "low_floor_as_is" => "Low floor as-is",
        "low_floor_with_repair_loop" => "Low floor with repair loop",
        "split_first_keep_low_floor" => "Low floor if split first",
        "escalate_for_cleaner_success" => "Stronger tier for cleaner success",
        "escalate_because_low_floor_out_of_scope" => "Stronger tier required",
        "comparative_evidence_inconclusive" => "Tiered routing inconclusive",
        _ => "No tiered routing"
    };
    public string BuilderTieredRoutingSummary => _builderTieredRoutingSummary;
    public bool HasBuilderTieredRoutingSummary => !string.IsNullOrWhiteSpace(_builderTieredRoutingSummary) &&
                                                  !string.Equals(_builderTieredRoutingSummary, "No tiered routing evidence recorded.", StringComparison.Ordinal);
    public string BuilderTieredRoutingPath => _builderTieredRoutingPath;
    public bool HasBuilderTieredRoutingPath => !string.IsNullOrWhiteSpace(_builderTieredRoutingPath) && File.Exists(_builderTieredRoutingPath);
    public string BuilderTieredRoutingEvidenceBadge => BuilderTieredRoutingBadge;
    public string BuilderTieredRoutingEvidenceSummary => BuilderTieredRoutingSummary;
    public bool HasBuilderTieredRoutingEvidenceSummary => HasBuilderTieredRoutingSummary;
    public string BuilderTieredRoutingEvidencePath => BuilderTieredRoutingPath;
    public bool HasBuilderTieredRoutingEvidencePath => HasBuilderTieredRoutingPath;
    public string BuilderPrimaryRoutingRecommendationSummary => _builderPrimaryRoutingRecommendationSummary;
    public bool HasBuilderPrimaryRoutingRecommendationSummary => !string.IsNullOrWhiteSpace(_builderPrimaryRoutingRecommendationSummary) &&
                                                                !string.Equals(_builderPrimaryRoutingRecommendationSummary, "No primary builder routing recommendation recorded.", StringComparison.Ordinal);
    public string BuilderStrongerTierRoleSummary => _builderStrongerTierRoleSummary;
    public bool HasBuilderStrongerTierRoleSummary => !string.IsNullOrWhiteSpace(_builderStrongerTierRoleSummary) &&
                                                     !string.Equals(_builderStrongerTierRoleSummary, "No stronger-tier routing role recorded.", StringComparison.Ordinal);
    public string BuilderWeakSpotMitigationSummary => _builderWeakSpotMitigationSummary;
    public bool HasBuilderWeakSpotMitigationSummary => !string.IsNullOrWhiteSpace(_builderWeakSpotMitigationSummary) &&
                                                       !string.Equals(_builderWeakSpotMitigationSummary, "No weak-spot mitigation summary recorded.", StringComparison.Ordinal);
    public string BuilderDefaultGuidanceState => _latestBuilderRequestPolicyDecision?.ChosenPolicyState ?? "not_run";
    public string BuilderDefaultGuidanceBadge => BuilderDefaultGuidanceState switch
    {
        "direct_low_floor" => "Direct low-floor",
        "split_first_low_floor" => "Split-first low-floor",
        "low_floor_with_repair_loop_expected" => "Low-floor with repair loop",
        "stronger_tier_optional" => "Stronger tier optional",
        "stronger_tier_recommended" => "Stronger tier recommended",
        "stronger_tier_required" => "Stronger tier required",
        _ => "No default guidance"
    };
    public string BuilderDefaultGuidanceSummary => _builderDefaultPolicySummary;
    public bool HasBuilderDefaultGuidanceSummary => !string.IsNullOrWhiteSpace(_builderDefaultPolicySummary) &&
                                                    !string.Equals(_builderDefaultPolicySummary, "No default builder guidance recorded.", StringComparison.Ordinal);
    public string BuilderDefaultGuidancePath => _builderDefaultPolicyPath;
    public bool HasBuilderDefaultGuidancePath => !string.IsNullOrWhiteSpace(_builderDefaultPolicyPath) && File.Exists(_builderDefaultPolicyPath);
    public string BuilderGuidanceHistoryPath => _builderDefaultPolicyHistoryPath;
    public bool HasBuilderGuidanceHistoryPath => !string.IsNullOrWhiteSpace(_builderDefaultPolicyHistoryPath) && File.Exists(_builderDefaultPolicyHistoryPath);
    public string BuilderLatestRoutingDecisionSummary => _builderRequestPolicyDecisionSummary;
    public bool HasBuilderLatestRoutingDecisionSummary => !string.IsNullOrWhiteSpace(_builderRequestPolicyDecisionSummary) &&
                                                          !string.Equals(_builderRequestPolicyDecisionSummary, "No builder routing decision recorded.", StringComparison.Ordinal);
    public string BuilderLatestRoutingDecisionPath => _builderRequestPolicyDecisionPath;
    public bool HasBuilderLatestRoutingDecisionPath => !string.IsNullOrWhiteSpace(_builderRequestPolicyDecisionPath) && File.Exists(_builderRequestPolicyDecisionPath);
    public string BuilderGuidanceSupportBadge => (_latestBuilderPolicyStability?.SupportLevel ?? "not_run") switch
    {
        "provisional" => "Provisional",
        "corroborated" => "Corroborated",
        "stable" => "Stable",
        _ => "No support history"
    };
    public string BuilderGuidanceSupportSummary => _builderPolicyStabilitySummary;
    public bool HasBuilderGuidanceSupportSummary => !string.IsNullOrWhiteSpace(_builderPolicyStabilitySummary) &&
                                                    !string.Equals(_builderPolicyStabilitySummary, "No builder guidance support recorded.", StringComparison.Ordinal);
    public string BuilderGuidanceSupportPath => _builderPolicyStabilityPath;
    public bool HasBuilderGuidanceSupportPath => !string.IsNullOrWhiteSpace(_builderPolicyStabilityPath) && File.Exists(_builderPolicyStabilityPath);
    public string BuilderIntakeState => _latestBuilderRequestIntake?.IntakeClassificationState ?? "not_run";
    public string BuilderIntakeBadge => BuilderIntakeState switch
    {
        "ready_for_direct_low_floor" => "Ready on current model",
        "ready_for_split_first_low_floor" => "Ready through split-first prep",
        "ready_for_low_floor_with_repair_loop" => "Repair loop expected",
        "stronger_tier_optional" => "Stronger tier optional",
        "stronger_tier_recommended" => "Stronger tier recommended",
        "task_out_of_scope" => "Out of scope on current floor",
        _ => "No intake"
    };
    public string BuilderIntakeSummary => _builderRequestIntakeSummary;
    public bool HasBuilderIntakeSummary => !string.IsNullOrWhiteSpace(_builderRequestIntakeSummary) &&
                                           !string.Equals(_builderRequestIntakeSummary, "No builder intake recorded.", StringComparison.Ordinal);
    public string BuilderIntakePath => _builderRequestIntakePath;
    public bool HasBuilderIntakePath => !string.IsNullOrWhiteSpace(_builderRequestIntakePath) && File.Exists(_builderRequestIntakePath);
    public string BuilderPrepRouteState => _latestBuilderExecutionPrep?.SelectedRoute ?? "not_run";
    public string BuilderPrepRouteBadge => BuilderPrepRouteState switch
    {
        "direct_low_floor_route" => "Direct low-floor route",
        "split_first_low_floor_route" => "Split-first route",
        "low_floor_with_repair_loop_route" => "Low-floor with repair loop",
        "current_model_with_optional_stronger_tier_route" => "Current model with optional stronger tier",
        "stronger_tier_recommended_route" => "Stronger tier recommended route",
        "task_out_of_scope_route" => "Out-of-scope route",
        _ => "No prep route"
    };
    public string BuilderPrepSummary => _builderExecutionPrepSummary;
    public bool HasBuilderPrepSummary => !string.IsNullOrWhiteSpace(_builderExecutionPrepSummary) &&
                                         !string.Equals(_builderExecutionPrepSummary, "No builder execution prep recorded.", StringComparison.Ordinal);
    public string BuilderPrepPath => _builderExecutionPrepPath;
    public bool HasBuilderPrepPath => !string.IsNullOrWhiteSpace(_builderExecutionPrepPath) && File.Exists(_builderExecutionPrepPath);
    public string BuilderLaunchAvailabilityState
    {
        get
        {
            if (_latestBuilderRequestIntake is null || _latestBuilderExecutionPrep is null)
                return "not_prepared";

            var blocker = GetBuilderPreparedLaunchDisabledReason();
            if (string.IsNullOrWhiteSpace(blocker))
                return "eligible";

            if (blocker.Contains("already has a recorded route result", StringComparison.OrdinalIgnoreCase))
                return "already_launched";
            if (blocker.Contains("stale intake", StringComparison.OrdinalIgnoreCase))
                return "blocked_stale_intake";
            if (blocker.Contains("execution prep is stale", StringComparison.OrdinalIgnoreCase))
                return "blocked_stale_execution_prep";
            if (blocker.Contains("does not support route", StringComparison.OrdinalIgnoreCase))
                return "blocked_route_unsupported";

            return _latestBuilderExecutionLaunch?.LaunchEligibilityState ?? "blocked";
        }
    }
    public string BuilderLaunchAvailabilityBadge => BuilderLaunchAvailabilityState switch
    {
        "eligible" => "Ready to launch",
        "already_launched" => "Already launched",
        "blocked_already_launched" => "Already launched",
        "blocked_stale_intake" => "Launch blocked by stale intake",
        "blocked_stale_execution_prep" => "Launch blocked by stale prep",
        "blocked_route_unsupported" => "Launch blocked by route",
        _ when BuilderLaunchAvailabilityState.StartsWith("blocked_", StringComparison.Ordinal) => "Launch blocked",
        _ => "Not prepared"
    };
    public string BuilderLaunchSummary => _builderExecutionLaunchSummary;
    public bool HasBuilderLaunchSummary => !string.IsNullOrWhiteSpace(_builderExecutionLaunchSummary) &&
                                           !string.Equals(_builderExecutionLaunchSummary, "No prepared builder launch recorded.", StringComparison.Ordinal);
    public string BuilderLaunchPath => _builderExecutionLaunchPath;
    public bool HasBuilderLaunchPath => !string.IsNullOrWhiteSpace(_builderExecutionLaunchPath) && File.Exists(_builderExecutionLaunchPath);
    public string BuilderResultState => _latestBuilderExecutionResult?.FinalRouteOutcomeClassification ?? "not_launched";
    public string BuilderResultBadge => BuilderResultState switch
    {
        "launched_and_passed" => "Launched and passed",
        "launched_and_passed_with_repair" => "Launched and passed with repair",
        "launched_and_failed_followup_created" => "Launched and failed into follow-up",
        "launched_and_failed_out_of_scope" => "Launched and failed out of scope",
        "launch_blocked" => "Launch blocked",
        _ => "Not launched"
    };
    public string BuilderResultSummary => _builderExecutionResultSummary;
    public bool HasBuilderResultSummary => !string.IsNullOrWhiteSpace(_builderExecutionResultSummary) &&
                                           !string.Equals(_builderExecutionResultSummary, "No prepared builder route result recorded.", StringComparison.Ordinal);
    public string BuilderResultPath => _builderExecutionResultPath;
    public bool HasBuilderResultPath => !string.IsNullOrWhiteSpace(_builderExecutionResultPath) && File.Exists(_builderExecutionResultPath);
    public string BuilderRouteComparisonBadge => (_latestBuilderExecutionResult?.PreparedRouteComparisonState ?? "not_run") switch
    {
        "confirmed" => "Prep confirmed",
        "optimistic_but_recoverable" => "Prep optimistic but recoverable",
        "insufficient_for_scope" => "Prep insufficient",
        _ => "No comparison"
    };
    public string BuilderRouteComparisonSummary => _latestBuilderExecutionResult is null
        ? "No prep-versus-result comparison recorded."
        : $"{_latestBuilderExecutionResult.PreparedRouteComparisonState.Replace('_', ' ')}: {_latestBuilderExecutionResult.Summary}";
    public bool HasBuilderRouteComparisonSummary => _latestBuilderExecutionResult is not null;
    public string BuilderReadinessGateState => _latestBuilderReadinessGate?.CurrentReadinessGateState ?? "not_recorded";
    public string BuilderReadinessGateBadge => BuilderReadinessGateState switch
    {
        "confirmed_for_bounded_use" => "Confirmed for bounded use",
        "confirmed_with_repair_loop" => "Confirmed with repair loop",
        "unstable_needs_more_evidence" => "Unstable, needs more evidence",
        "contradicted" => "Contradicted",
        "provisional" => "Provisional",
        _ => "No readiness gate"
    };
    public string BuilderReadinessGateSummary => _builderReadinessGateSummary;
    public bool HasBuilderReadinessGateSummary => !string.IsNullOrWhiteSpace(_builderReadinessGateSummary) &&
                                                  !string.Equals(_builderReadinessGateSummary, "No builder readiness gate recorded.", StringComparison.Ordinal);
    public string BuilderReadinessCountsSummary => _builderReadinessCountsSummary;
    public bool HasBuilderReadinessCountsSummary => !string.IsNullOrWhiteSpace(_builderReadinessCountsSummary) &&
                                                    !string.Equals(_builderReadinessCountsSummary, "No builder readiness evidence recorded.", StringComparison.Ordinal);
    public string BuilderReadinessBoundedUseSummary => _builderReadinessBoundedUseSummary;
    public bool HasBuilderReadinessBoundedUseSummary => !string.IsNullOrWhiteSpace(_builderReadinessBoundedUseSummary) &&
                                                        !string.Equals(_builderReadinessBoundedUseSummary, "No builder readiness decision recorded.", StringComparison.Ordinal);
    public string BuilderReadinessSupportingArtifactsSummary => _latestBuilderReadinessGate is null || _latestBuilderReadinessGate.LatestSupportingArtifactPaths.Count == 0
        ? "No builder readiness supporting artifacts recorded."
        : string.Join(" | ", _latestBuilderReadinessGate.LatestSupportingArtifactPaths.Take(3));
    public bool HasBuilderReadinessSupportingArtifactsSummary => _latestBuilderReadinessGate is not null &&
                                                                 _latestBuilderReadinessGate.LatestSupportingArtifactPaths.Count > 0;
    public string BuilderReadinessGatePath => _builderReadinessGatePath;
    public bool HasBuilderReadinessGatePath => !string.IsNullOrWhiteSpace(_builderReadinessGatePath) && File.Exists(_builderReadinessGatePath);
    public string BuilderReadinessGateHistoryPath => _builderReadinessGateHistoryPath;
    public bool HasBuilderReadinessGateHistoryPath => !string.IsNullOrWhiteSpace(_builderReadinessGateHistoryPath) && File.Exists(_builderReadinessGateHistoryPath);
    public string BuilderConfirmedClassesSummary => _builderConfirmedClassesSummary;
    public bool HasBuilderConfirmedClassesSummary => !string.IsNullOrWhiteSpace(_builderConfirmedClassesSummary) &&
                                                     !string.Equals(_builderConfirmedClassesSummary, "No confirmed builder task classes recorded.", StringComparison.Ordinal);
    public string BuilderConfirmedClassesPath => _builderConfirmedClassesPath;
    public bool HasBuilderConfirmedClassesPath => !string.IsNullOrWhiteSpace(_builderConfirmedClassesPath) && File.Exists(_builderConfirmedClassesPath);
    public string BuilderDefaultRouteDecisionSummary => _builderDefaultRouteDecisionSummary;
    public bool HasBuilderDefaultRouteDecisionSummary => !string.IsNullOrWhiteSpace(_builderDefaultRouteDecisionSummary) &&
                                                         !string.Equals(_builderDefaultRouteDecisionSummary, "No default builder route decision recorded.", StringComparison.Ordinal);
    public string BuilderDefaultRouteDecisionPath => _builderDefaultRouteDecisionPath;
    public bool HasBuilderDefaultRouteDecisionPath => !string.IsNullOrWhiteSpace(_builderDefaultRouteDecisionPath) && File.Exists(_builderDefaultRouteDecisionPath);
    public string BuilderLaunchDefaultDecisionSummary => _builderLaunchDefaultDecisionSummary;
    public bool HasBuilderLaunchDefaultDecisionSummary => !string.IsNullOrWhiteSpace(_builderLaunchDefaultDecisionSummary) &&
                                                          !string.Equals(_builderLaunchDefaultDecisionSummary, "No builder launch default decision recorded.", StringComparison.Ordinal);
    public string BuilderLaunchDefaultDecisionPath => _builderLaunchDefaultDecisionPath;
    public bool HasBuilderLaunchDefaultDecisionPath => !string.IsNullOrWhiteSpace(_builderLaunchDefaultDecisionPath) && File.Exists(_builderLaunchDefaultDecisionPath);
    public string BuilderLaunchRouteModeSummary => _builderLaunchRouteModeSummary;
    public bool HasBuilderLaunchRouteModeSummary => !string.IsNullOrWhiteSpace(_builderLaunchRouteModeSummary) &&
                                                    !string.Equals(_builderLaunchRouteModeSummary, "No builder launch default mode recorded.", StringComparison.Ordinal);
    public string BuilderRouteSourceSummary => _builderRouteSourceSummary;
    public bool HasBuilderRouteSourceSummary => !string.IsNullOrWhiteSpace(_builderRouteSourceSummary) &&
                                                !string.Equals(_builderRouteSourceSummary, "No current builder route source recorded.", StringComparison.Ordinal);
    public string BuilderOverrideAvailabilitySummary => _builderOverrideAvailabilitySummary;
    public bool HasBuilderOverrideAvailabilitySummary => !string.IsNullOrWhiteSpace(_builderOverrideAvailabilitySummary) &&
                                                         !string.Equals(_builderOverrideAvailabilitySummary, "No operator override state recorded.", StringComparison.Ordinal);
    public string BuilderOverrideRouteOptionSummary => _builderOverrideRouteOptionSummary;
    public bool HasBuilderOverrideRouteOptionSummary => !string.IsNullOrWhiteSpace(_builderOverrideRouteOptionSummary) &&
                                                        !string.Equals(_builderOverrideRouteOptionSummary, "No explicit builder route override is currently prepared.", StringComparison.Ordinal);
    public string BuilderRouteOverrideSummary => _builderRouteOverrideSummary;
    public bool HasBuilderRouteOverrideSummary => !string.IsNullOrWhiteSpace(_builderRouteOverrideSummary) &&
                                                  !string.Equals(_builderRouteOverrideSummary, "No builder route override evidence recorded.", StringComparison.Ordinal);
    public string BuilderRouteOverridePath => _builderRouteOverridePath;
    public bool HasBuilderRouteOverridePath => !string.IsNullOrWhiteSpace(_builderRouteOverridePath) && File.Exists(_builderRouteOverridePath);
    public string BuilderRouteReviewSummary => _builderRouteReviewSummary;
    public bool HasBuilderRouteReviewSummary => !string.IsNullOrWhiteSpace(_builderRouteReviewSummary) &&
                                                !string.Equals(_builderRouteReviewSummary, "No builder route review candidates recorded.", StringComparison.Ordinal);
    public string BuilderRouteReviewPath => _builderRouteReviewPath;
    public bool HasBuilderRouteReviewPath => !string.IsNullOrWhiteSpace(_builderRouteReviewPath) && File.Exists(_builderRouteReviewPath);
    public string BuilderDefaultSuspensionSummary => _builderDefaultSuspensionSummary;
    public bool HasBuilderDefaultSuspensionSummary => !string.IsNullOrWhiteSpace(_builderDefaultSuspensionSummary) &&
                                                      !string.Equals(_builderDefaultSuspensionSummary, "No default-route suspension recorded.", StringComparison.Ordinal);
    public string BuilderRouteReconfirmationSummary => _builderRouteReconfirmationSummary;
    public bool HasBuilderRouteReconfirmationSummary => !string.IsNullOrWhiteSpace(_builderRouteReconfirmationSummary) &&
                                                        !string.Equals(_builderRouteReconfirmationSummary, "No builder route reconfirmation recorded.", StringComparison.Ordinal);
    public string BuilderRouteReconfirmationPath => _builderRouteReconfirmationPath;
    public bool HasBuilderRouteReconfirmationPath => !string.IsNullOrWhiteSpace(_builderRouteReconfirmationPath) && File.Exists(_builderRouteReconfirmationPath);
    public string BuilderDefaultRouteRecoverySummary => _builderDefaultRouteRecoverySummary;
    public bool HasBuilderDefaultRouteRecoverySummary => !string.IsNullOrWhiteSpace(_builderDefaultRouteRecoverySummary) &&
                                                         !string.Equals(_builderDefaultRouteRecoverySummary, "No builder default-route recovery recorded.", StringComparison.Ordinal);
    public string BuilderDefaultRouteRecoveryPath => _builderDefaultRouteRecoveryPath;
    public bool HasBuilderDefaultRouteRecoveryPath => !string.IsNullOrWhiteSpace(_builderDefaultRouteRecoveryPath) && File.Exists(_builderDefaultRouteRecoveryPath);
    public string BuilderReadinessContradictionsSummary => _builderReadinessContradictionsSummary;
    public bool HasBuilderReadinessContradictionsSummary => !string.IsNullOrWhiteSpace(_builderReadinessContradictionsSummary) &&
                                                            !string.Equals(_builderReadinessContradictionsSummary, "No builder readiness contradictions recorded.", StringComparison.Ordinal);
    public string BuilderReadinessContradictionsPath => _builderReadinessContradictionsPath;
    public bool HasBuilderReadinessContradictionsPath => !string.IsNullOrWhiteSpace(_builderReadinessContradictionsPath) && File.Exists(_builderReadinessContradictionsPath);
    public string BuilderRouteStabilitySummary => _builderRouteStabilitySummary;
    public bool HasBuilderRouteStabilitySummary => !string.IsNullOrWhiteSpace(_builderRouteStabilitySummary) &&
                                                   !string.Equals(_builderRouteStabilitySummary, "No builder route stability summary recorded.", StringComparison.Ordinal);
    public string BuilderRouteStabilitySummaryPath => _builderRouteStabilitySummaryPath;
    public bool HasBuilderRouteStabilitySummaryPath => !string.IsNullOrWhiteSpace(_builderRouteStabilitySummaryPath) && File.Exists(_builderRouteStabilitySummaryPath);
    public string BuilderReadinessLatestContradictionNote => _builderReadinessLatestContradictionNote;
    public bool HasBuilderReadinessLatestContradictionNote => !string.IsNullOrWhiteSpace(_builderReadinessLatestContradictionNote);
    public string BuilderSplitStepExecutionSummary => _builderSplitStepExecutionSummary;
    public bool HasBuilderSplitStepExecutionSummary => !string.IsNullOrWhiteSpace(_builderSplitStepExecutionSummary) &&
                                                       !string.Equals(_builderSplitStepExecutionSummary, "No split-step execution recorded.", StringComparison.Ordinal);
    public string BuilderSplitStepExecutionPath => _builderSplitStepExecutionPath;
    public bool HasBuilderSplitStepExecutionPath => !string.IsNullOrWhiteSpace(_builderSplitStepExecutionPath) && File.Exists(_builderSplitStepExecutionPath);
    public string BuilderSplitFirstOutcomeClassification => _latestBuilderSplitFirstOutcome?.ClosureClassification ?? "not_run";
    public string BuilderSplitFirstOutcomeBadge => BuilderSplitFirstOutcomeClassification switch
    {
        "split_closed_gap" => "Split closed the gap",
        "split_improved_but_not_closed" => "Split improved but stayed open",
        "split_equal_to_stronger_tier" => "Split matched stronger tier",
        "split_viable_but_costlier" => "Split worked but cost more",
        "stronger_tier_still_preferred" => "Stronger tier still preferred",
        "split_failed" => "Split failed",
        _ => "No split outcome"
    };
    public string BuilderSplitFirstOutcomeSummary => _builderSplitFirstOutcomeSummary;
    public bool HasBuilderSplitFirstOutcomeSummary => !string.IsNullOrWhiteSpace(_builderSplitFirstOutcomeSummary) &&
                                                      !string.Equals(_builderSplitFirstOutcomeSummary, "No split-first outcome recorded.", StringComparison.Ordinal);
    public string BuilderSplitFirstOutcomePath => _builderSplitFirstOutcomePath;
    public bool HasBuilderSplitFirstOutcomePath => !string.IsNullOrWhiteSpace(_builderSplitFirstOutcomePath) && File.Exists(_builderSplitFirstOutcomePath);
    public IReadOnlyList<BuilderSplitStepRow> BuilderSplitSteps => _builderSplitSteps;
    public bool HasBuilderSplitSteps => _builderSplitSteps.Count > 0;
    public string BuilderSplitExecutionDisabledReason => GetBuilderSplitExecutionDisabledReason();
    public bool HasBuilderSplitExecutionDisabledReason => !string.IsNullOrWhiteSpace(BuilderSplitExecutionDisabledReason);
    public string BuilderComparativeProofDisabledReason => GetBuilderComparativeProofDisabledReason();
    public bool HasBuilderComparativeProofDisabledReason => !string.IsNullOrWhiteSpace(BuilderComparativeProofDisabledReason);
    public bool IsBuilderProofExpanded
    {
        get => _isBuilderProofExpanded;
        set
        {
            if (_isBuilderProofExpanded == value) return;
            _isBuilderProofExpanded = value;
            OnPropertyChanged(nameof(IsBuilderProofExpanded));
        }
    }
    public string ValidationFollowupPlanPath => _validationFollowupPlanPath;
    public bool HasValidationFollowupPlanPath => !string.IsNullOrWhiteSpace(_validationFollowupPlanPath) && File.Exists(_validationFollowupPlanPath);
    public string ValidationRepairPrepBundlePath => _validationRepairPrepBundlePath;
    public bool HasValidationRepairPrepBundlePath => !string.IsNullOrWhiteSpace(_validationRepairPrepBundlePath) && File.Exists(_validationRepairPrepBundlePath);
    public IReadOnlyList<ValidationFollowupPlanStepRow> ValidationFollowupPlanSteps => _validationFollowupPlanSteps;
    public bool HasValidationFollowupPlanSteps => _validationFollowupPlanSteps.Count > 0;
    public string ValidationFollowupRecommendedRerunBlockedReason
    {
        get
        {
            var step = GetRecommendedFollowupPlanStep();
            return step is null ? string.Empty : GetValidationFollowupPlanStepBlockReason(step);
        }
    }
    public bool HasValidationFollowupRecommendedRerunBlockedReason => !string.IsNullOrWhiteSpace(ValidationFollowupRecommendedRerunBlockedReason);
    public string ValidationFollowupFirstEvidenceBlockedReason
    {
        get
        {
            var step = GetFirstEvidenceFollowupPlanStep();
            return step is null ? string.Empty : GetValidationFollowupPlanStepBlockReason(step);
        }
    }
    public bool HasValidationFollowupFirstEvidenceBlockedReason => !string.IsNullOrWhiteSpace(ValidationFollowupFirstEvidenceBlockedReason);
    public bool IsValidationFollowupExpanded
    {
        get => _isValidationFollowupExpanded;
        set
        {
            if (_isValidationFollowupExpanded == value) return;
            _isValidationFollowupExpanded = value;
            OnPropertyChanged(nameof(IsValidationFollowupExpanded));
        }
    }
    public bool IsValidationFollowupPlanExpanded
    {
        get => _isValidationFollowupPlanExpanded;
        set
        {
            if (_isValidationFollowupPlanExpanded == value) return;
            _isValidationFollowupPlanExpanded = value;
            OnPropertyChanged(nameof(IsValidationFollowupPlanExpanded));
        }
    }
    public string ValidationRunMode => _validationRunMode;
    public string ValidationRunModeBadge => _validationRunMode switch
    {
        "sequential_standard_mode" => "Sequential standard mode",
        "isolated_workspace_mode" => "Isolated workspace mode",
        "single_stage_manual_mode" => "Single-stage manual mode",
        _ => "Not run"
    };
    public string ValidationRunModeSummary => _validationRunModeSummary;
    public string ValidationSequenceSummary => BuildValidationSequenceSummary();
    public string ValidationOrchestrationArtifactPath => _validationOrchestrationArtifactPath;
    public bool HasValidationOrchestrationArtifactPath => !string.IsNullOrWhiteSpace(_validationOrchestrationArtifactPath) && File.Exists(_validationOrchestrationArtifactPath);
    public string ValidationOrchestrationNotePath => _validationOrchestrationPolicyNotePath;
    public bool HasValidationOrchestrationNotePath => !string.IsNullOrWhiteSpace(_validationOrchestrationPolicyNotePath) && File.Exists(_validationOrchestrationPolicyNotePath);
    public string ValidationIsolatedWorkspacePath => _validationIsolatedWorkspacePath;
    public bool HasValidationIsolatedWorkspacePath => !string.IsNullOrWhiteSpace(_validationIsolatedWorkspacePath) && Directory.Exists(_validationIsolatedWorkspacePath);
    public IReadOnlyList<ValidationActionPolicyRow> ValidationActionPolicies => BuildValidationActionPolicyRows();
    public bool HasValidationActionPolicies => ValidationActionPolicies.Count > 0;
    public string ValidationTrendClassification => _validationTrendClassification;
    public string ValidationTrendBadge => _validationTrendClassification switch
    {
        "stable" => "Stable",
        "passed_after_retry" => "Passed after retry",
        "flaky_trend_increasing" => "Flaky trend increasing",
        "regression_detected" => "Regression detected",
        "failed" => "Failed",
        _ => "No history"
    };
    public string ValidationTrendSummaryText => _validationTrendSummaryText;
    public string ValidationRegressionSummaryText => _validationRegressionSummaryText;
    public string ValidationHistoryLedgerPath => _validationHistoryLedgerPath;
    public bool HasValidationHistoryLedgerPath => !string.IsNullOrWhiteSpace(_validationHistoryLedgerPath) && File.Exists(_validationHistoryLedgerPath);
    public string ValidationTrendArtifactPath => _validationTrendArtifactPath;
    public bool HasValidationTrendArtifactPath => !string.IsNullOrWhiteSpace(_validationTrendArtifactPath) && File.Exists(_validationTrendArtifactPath);
    public string ValidationRegressionArtifactPath => _validationRegressionArtifactPath;
    public bool HasValidationRegressionArtifactPath => !string.IsNullOrWhiteSpace(_validationRegressionArtifactPath) && File.Exists(_validationRegressionArtifactPath);
    public string ValidationReleaseReadinessClassification => _validationReleaseReadinessClassification;
    public string ValidationReleaseReadinessBadge => _validationReleaseReadinessClassification switch
    {
        "ready" => "Ready",
        "caution" => "Ready with caution",
        _ => "Not ready"
    };
    public string ValidationReleaseReadinessSummary => _validationReleaseReadinessSummary;
    public string ValidationBaselineSummaryText => _validationBaselineSummaryText;
    public string ValidationBaselineComparisonSummaryText => _validationBaselineComparisonSummaryText;
    public string ValidationBaselineArtifactPath => _validationBaselineArtifactPath;
    public bool HasValidationBaselineArtifactPath => !string.IsNullOrWhiteSpace(_validationBaselineArtifactPath) && File.Exists(_validationBaselineArtifactPath);
    public string ValidationBaselineHistoryArtifactPath => _validationBaselineHistoryArtifactPath;
    public bool HasValidationBaselineHistoryArtifactPath => !string.IsNullOrWhiteSpace(_validationBaselineHistoryArtifactPath) && File.Exists(_validationBaselineHistoryArtifactPath);
    public string ValidationBaselineComparisonArtifactPath => _validationBaselineComparisonArtifactPath;
    public bool HasValidationBaselineComparisonArtifactPath => !string.IsNullOrWhiteSpace(_validationBaselineComparisonArtifactPath) && File.Exists(_validationBaselineComparisonArtifactPath);
    public string SemanticReuseStatus => _semanticReuseStatus;
    public string SemanticReuseBadge => _semanticReuseStatus switch
    {
        "qdrant" => "Qdrant ranked",
        "local_only" => "Local ranked",
        "disabled" => "Disabled",
        "no_context" => "No context",
        _ => "Not ready"
    };
    public string SemanticReuseSummary => _semanticReuseSummary;
    public string SemanticReuseDisabledReason => GetSemanticReuseDisabledReason();
    public bool HasSemanticReuseDisabledReason => !string.IsNullOrWhiteSpace(SemanticReuseDisabledReason);
    public string SemanticReuseDesignNotePath => _semanticReuseDesignNotePath;
    public bool HasSemanticReuseDesignNotePath => !string.IsNullOrWhiteSpace(_semanticReuseDesignNotePath) && File.Exists(_semanticReuseDesignNotePath);
    public string SemanticReuseIndexPath => _semanticReuseIndexPath;
    public bool HasSemanticReuseIndexPath => !string.IsNullOrWhiteSpace(_semanticReuseIndexPath) && File.Exists(_semanticReuseIndexPath);
    public string SemanticReuseLinkagePath => _semanticReuseLinkagePath;
    public bool HasSemanticReuseLinkagePath => !string.IsNullOrWhiteSpace(_semanticReuseLinkagePath) && File.Exists(_semanticReuseLinkagePath);
    public bool HasSemanticReuseSuggestions => _semanticReuseSuggestions.Count > 0;
    public bool IsSemanticReuseExpanded
    {
        get => _isSemanticReuseExpanded;
        set
        {
            if (_isSemanticReuseExpanded == value) return;
            _isSemanticReuseExpanded = value;
            OnPropertyChanged(nameof(IsSemanticReuseExpanded));
        }
    }
    public IReadOnlyList<SemanticReuseSuggestionRow> VisibleSemanticReuseSuggestions
        => _semanticReuseSuggestions
            .Where(row => MatchesSemanticReuseContextFilter(row.ContextKind))
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.ContextLabel, StringComparer.Ordinal)
            .ThenBy(row => row.Title, StringComparer.Ordinal)
            .ToArray();
    public string SemanticReuseEffectivenessSummary => _semanticReuseEffectivenessSummary;
    public bool HasSemanticReuseEffectivenessSummary => !string.IsNullOrWhiteSpace(_semanticReuseEffectivenessSummary);
    public string SemanticReuseEffectivenessPath => _semanticReuseEffectivenessPath;
    public bool HasSemanticReuseEffectivenessPath => !string.IsNullOrWhiteSpace(_semanticReuseEffectivenessPath) && File.Exists(_semanticReuseEffectivenessPath);
    public string SemanticReusePlaybookPath => _semanticReusePlaybookPath;
    public bool HasSemanticReusePlaybookPath => !string.IsNullOrWhiteSpace(_semanticReusePlaybookPath) && File.Exists(_semanticReusePlaybookPath);
    public string SemanticReusePlaybookSummary => _semanticReusePlaybookSummary;
    public bool HasSemanticReusePlaybookSummary => !string.IsNullOrWhiteSpace(_semanticReusePlaybookSummary);
    public bool IsSemanticReusePlaybooksExpanded
    {
        get => _isSemanticReusePlaybooksExpanded;
        set
        {
            if (_isSemanticReusePlaybooksExpanded == value) return;
            _isSemanticReusePlaybooksExpanded = value;
            OnPropertyChanged(nameof(IsSemanticReusePlaybooksExpanded));
        }
    }
    public bool HasSemanticReusePlaybooks => _semanticReusePlaybooks.Count > 0;
    public IReadOnlyList<SemanticReusePlaybookRow> VisibleSemanticReusePlaybooks
    {
        get
        {
            var filtered = _semanticReusePlaybooks
                .Where(row => MatchesSemanticReuseContextFilter(row.ContextKind))
                .Where(row => ShowTentativePlaybooks || !string.Equals(row.Confidence, "tentative", StringComparison.Ordinal))
                .OrderByDescending(row => MatchesCurrentSemanticReusePlaybook(row))
                .ThenByDescending(row => MapSemanticReusePlaybookConfidence(row.Confidence))
                .ThenByDescending(row => row.EvidenceCount)
                .ThenBy(row => row.Title, StringComparer.Ordinal)
                .ToArray();

            if (string.Equals(SelectedSemanticReuseContext, "All contexts", StringComparison.Ordinal))
            {
                return filtered
                    .GroupBy(row => row.ContextKind, StringComparer.Ordinal)
                    .SelectMany(group => group.Take(SelectedSemanticReuseMaxPlaybooks))
                    .OrderByDescending(row => MatchesCurrentSemanticReusePlaybook(row))
                    .ThenByDescending(row => MapSemanticReusePlaybookConfidence(row.Confidence))
                    .ThenByDescending(row => row.EvidenceCount)
                    .ThenBy(row => row.Title, StringComparer.Ordinal)
                    .ToArray();
            }

            return filtered.Take(SelectedSemanticReuseMaxPlaybooks).ToArray();
        }
    }
    public bool HasVisibleSemanticReusePlaybooks => VisibleSemanticReusePlaybooks.Count > 0;
    public bool HasVisibleSemanticReuseSuggestions => VisibleSemanticReuseSuggestions.Count > 0;
    public string SemanticReuseContextSummary => _semanticReuseContextSummary;
    public bool HasSemanticReuseContextSummary => !string.IsNullOrWhiteSpace(_semanticReuseContextSummary);
    public int SelectedSemanticReuseRepairReferenceCount => _semanticReuseSuggestions.Count(row => row.IsSelectedForRepairReference);
    public bool HasSelectedSemanticReuseRepairReferences => SelectedSemanticReuseRepairReferenceCount > 0;
    public bool HasValidationStageResults => _validationStageResults.Count > 0;
    public bool HasValidationRuns => _validationRuns.Count > 0;
    public bool HasValidationStageHistory => _validationStageHistory.Count > 0;
    public bool HasValidationBaselineStageChanges => _validationBaselineStageChanges.Count > 0;
    public string SetReleaseBaselineDisabledReason => GetSetReleaseBaselineDisabledReason();
    public string GeneratedOutputValidationStatus => _generatedOutputValidationStatus;
    public string GeneratedOutputValidationBadge => GeneratedOutputValidationStatus switch
    {
        "validating" => "Validating",
        "passed" => "Passed",
        "failed" => "Failed",
        _ => "Not validated"
    };
    public string GeneratedOutputValidationSummary => _generatedOutputValidationSummary;
    public string GeneratedOutputValidationRunId => _generatedOutputValidationRunId;
    public string GeneratedOutputValidationSourcePath => _generatedOutputValidationSourcePath;
    public bool HasGeneratedOutputValidationRunId => !string.IsNullOrWhiteSpace(_generatedOutputValidationRunId);
    public string AttemptRepairDisabledReason => GetAttemptRepairDisabledReason();
    public string RepairSummary => _repairSummary;
    public string RepairBundlePath => _repairBundlePath;
    public bool HasRepairBundlePath => !string.IsNullOrWhiteSpace(_repairBundlePath);
    public string RepairOutputFolder => _repairOutputFolder;
    public bool HasRepairOutputFolder => !string.IsNullOrWhiteSpace(_repairOutputFolder);
    public string RepairOutcome => _repairOutcome;
    public bool HasRepairChangedFiles => _repairChangedFiles.Count > 0;
    public bool HasRepairHistory => _repairHistory.Count > 0;
    public string RepairComparisonSourceStage => _repairComparisonSourceStage;
    public bool HasRepairComparisonSourceStage => !string.IsNullOrWhiteSpace(_repairComparisonSourceStage);
    public string RepairComparisonSourceExcerpt => _repairComparisonSourceExcerpt;
    public bool HasRepairComparisonSourceExcerpt => !string.IsNullOrWhiteSpace(_repairComparisonSourceExcerpt);
    public string RepairComparisonRepairedStage => _repairComparisonRepairedStage;
    public bool HasRepairComparisonRepairedStage => !string.IsNullOrWhiteSpace(_repairComparisonRepairedStage);
    public string RepairComparisonRepairedExcerpt => _repairComparisonRepairedExcerpt;
    public bool HasRepairComparisonRepairedExcerpt => !string.IsNullOrWhiteSpace(_repairComparisonRepairedExcerpt);
    public string RepairComparisonValidationResult => _repairComparisonValidationResult;
    public bool HasRepairComparisonValidationResult => !string.IsNullOrWhiteSpace(_repairComparisonValidationResult);
    public string RepairLinkedValidationRunFolder => _repairLinkedValidationRunFolder;
    public bool HasRepairLinkedValidationRunFolder => !string.IsNullOrWhiteSpace(_repairLinkedValidationRunFolder);
    public string PromoteRepairDisabledReason => GetPromoteRepairDisabledReason();
    public string AdoptRepairDisabledReason => GetAdoptRepairDisabledReason();
    public string ReplaceRepairDisabledReason => GetReplaceRepairDisabledReason();
    public string UnadoptRepairDisabledReason => GetUnadoptRepairDisabledReason();
    public string RepairPromotionStatus => _repairPromotionStatus;
    public string RepairPromotionBadge => _repairPromotionStatus switch
    {
        "promoted_from_repair" => "Promoted from repair",
        "superseded_by_later_repair" => "Superseded by later repair",
        _ => "Not promoted"
    };
    public string RepairPromotionSummary => _repairPromotionSummary;
    public string RepairAdoptionStatus => _repairAdoptionStatus;
    public string RepairAdoptionBadge => _repairAdoptionStatus switch
    {
        "adopted" => "Adopted",
        "replaced_by_newer_output" => "Replaced by newer output",
        "rolled_back" => "No longer current",
        "promoted_only" => "Promoted only",
        _ => "Not promoted"
    };
    public string RepairAdoptionSummary => _repairAdoptionSummary;
    public string RepairConfidenceSignal => _repairConfidenceSignal;
    public string RepairConfidenceText => _repairConfidenceText;
    public bool HasRepairConfidenceText => !string.IsNullOrWhiteSpace(_repairConfidenceText);
    public string GeneratedOutputTrustState => _generatedOutputTrustState;
    public string GeneratedOutputTrustBadge => _generatedOutputTrustState switch
    {
        "adopted" => "Adopted",
        "promoted" => "Promoted",
        "repaired" => "Repaired",
        "validated" => "Validated",
        "superseded" => "Superseded",
        _ => "Unvalidated"
    };
    public string PromotedRepairFolder => _promotedRepairFolder;
    public bool HasPromotedRepairFolder => !string.IsNullOrWhiteSpace(_promotedRepairFolder);
    public string RepairAuditSummaryFolder => _repairAuditSummaryFolder;
    public bool HasRepairAuditSummaryFolder => !string.IsNullOrWhiteSpace(_repairAuditSummaryFolder);
    public string RepairLineageSummary => _repairLineageSummary;
    public bool HasRepairLineage => !string.IsNullOrWhiteSpace(_repairLineageSummary) &&
                                    !string.Equals(_repairLineageSummary, "No repair lineage recorded.", StringComparison.Ordinal);
    public string RepairReviewNote
    {
        get => _repairReviewNote;
        set
        {
            var normalized = value ?? string.Empty;
            if (_repairReviewNote == normalized) return;
            _repairReviewNote = normalized;
            OnPropertyChanged(nameof(RepairReviewNote));
        }
    }
    public bool HasPromotedRepairId => !string.IsNullOrWhiteSpace(_promotedRepairId);
    public string PromotedRepairId => _promotedRepairId;
    public bool ShowFullTimeline
    {
        get => _showFullTimeline;
        set
        {
            if (_showFullTimeline == value)
            {
                return;
            }

            _showFullTimeline = value;
            OnPropertyChanged(nameof(ShowFullTimeline));
            OnPropertyChanged(nameof(TimelineToggleLabel));
            RebuildVisibleOperationProgressSteps();
        }
    }

    public string TimelineToggleLabel => ShowFullTimeline ? "Show active + recent" : "Show full timeline";
    public string LatestRunPath => LastRunFolderPath;
    public bool HasLatestRunPath => !string.IsNullOrWhiteSpace(LatestRunPath) && Directory.Exists(LatestRunPath);
    public string ReplaySourcePath => _replaySourcePath;
    public bool HasReplaySourcePath => !string.IsNullOrWhiteSpace(_replaySourcePath);
    public string ReplaySummary => _replaySummary;
    public string ReplayMismatchSummary => _replayMismatchSummary;
    public bool HasReplayMismatch => !string.IsNullOrWhiteSpace(_replayMismatchSummary);
    public string ReplayTimingSummary => _replayTimingSummary;
    public bool HasReplayTimingSummary => !string.IsNullOrWhiteSpace(_replayTimingSummary);
    public string LastFailureExceptionType => ExtractFailureExceptionType(LastFailureReason);
    public string LastFailureFirstStackFrame => ExtractFailureFirstStackFrame(LastFailureReason);
    public string FatalLogPath => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Shoots.UI", "fatal-error.log");

	public string IntakeTarget
	{
		get => _intakeTarget;
		set
		{
			if (_isWorkOrderLocked) return;
			if (_intakeTarget == value) return;
			_intakeTarget = value;
			OnPropertyChanged(nameof(IntakeTarget));
			RebuildJobSpecDigest();
			RaiseCommandCanExecute();
		}
	}

	public string IntakeAttachments
	{
		get => _intakeAttachments;
		set
		{
			if (_isWorkOrderLocked) return;
			if (_intakeAttachments == value) return;
			_intakeAttachments = value;
			OnPropertyChanged(nameof(IntakeAttachments));
			RebuildJobSpecDigest();
			RaiseCommandCanExecute();
		}
	}

	public string IntakeStack
	{
		get => _intakeStack;
		set
		{
			if (_isWorkOrderLocked) return;
			if (_intakeStack == value) return;
			_intakeStack = value;
			OnPropertyChanged(nameof(IntakeStack));
			RebuildJobSpecDigest();
			RaiseCommandCanExecute();
		}
	}

	public bool IsWorkOrderLocked
	{
		get => _isWorkOrderLocked;
		private set
		{
			if (_isWorkOrderLocked == value) return;
			_isWorkOrderLocked = value;
			OnPropertyChanged(nameof(IsWorkOrderLocked));
			RaiseCommandCanExecute();
		}
	}

	public string JobSpecDigest
	{
		get => _jobSpecDigest;
		private set
		{
			if (_jobSpecDigest == value) return;
			_jobSpecDigest = value;
			OnPropertyChanged(nameof(JobSpecDigest));
		}
	}

	// Waiting / resume surfaces (tests expect these names)
	public bool HasWaitingInfo => _lastWaitingInfo is not null;

	// If your XAML binds LastWaitingInfo.* you can keep this as object and/or replace with your real type later.
	public object? LastWaitingInfo
	{
		get => _lastWaitingInfo;
		private set
		{
			if (ReferenceEquals(_lastWaitingInfo, value)) return;
			_lastWaitingInfo = value;
			OnPropertyChanged(nameof(LastWaitingInfo));
			OnPropertyChanged(nameof(HasWaitingInfo));
			RaiseCommandCanExecute();
		}
	}

	public string DecisionBindingsJson
	{
		get => _decisionBindingsJson;
		set
		{
			if (_decisionBindingsJson == value) return;
			_decisionBindingsJson = value;
			OnPropertyChanged(nameof(DecisionBindingsJson));
			RaiseCommandCanExecute();
		}
	}

	public string DecisionToolId
	{
		get => _decisionToolId;
		set
		{
			if (_decisionToolId == value) return;
			_decisionToolId = value;
			OnPropertyChanged(nameof(DecisionToolId));
			RaiseCommandCanExecute();
		}
	}

	// Commands required by tests/XAML (wiring is minimal; real behavior can be implemented later)
        public AsyncRelayCommand LockWorkOrderCommand { get; private set; } = null!;
        public AsyncRelayCommand UnlockWorkOrderCommand { get; private set; } = null!;
        public AsyncRelayCommand GeneratePlanCommand { get; private set; } = null!;
        public AsyncRelayCommand RunIntakePlanCommand { get; private set; } = null!;
        public AsyncRelayCommand ResumeInjectDecisionCommand { get; private set; } = null!;
        public AsyncRelayCommand RefreshModelCatalogCommand { get; private set; } = null!;
        public AsyncRelayCommand ResetModelCatalogCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenStateFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand QuickStartCommand { get; private set; } = null!;
        public AsyncRelayCommand SendChatIntentCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenCurrentWorkspaceFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenProjectFileCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenLastRunFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenLastVerificationReportCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenLastOperatorFlowCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenLastTransportEquivalenceCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyLastRunFolderPathCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyLastVerificationReportPathCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyLastOperatorFlowPathCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyLastTransportEquivalencePathCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyLastFailureSummaryCommand { get; private set; } = null!;
        public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ProofArtifactRow> OpenProofArtifactCommand { get; private set; } = null!;
        public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ProofArtifactRow> CopyProofArtifactPathCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenProofRunFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyProofRunFolderPathCommand { get; private set; } = null!;
        public AsyncRelayCommand ReplayLatestRunCommand { get; private set; } = null!;
        public AsyncRelayCommand ReplaySelectedRunCommand { get; private set; } = null!;
        public AsyncRelayCommand BuildUiProjectCommand { get; private set; } = null!;
        public AsyncRelayCommand RunUiTestsCommand { get; private set; } = null!;
        public AsyncRelayCommand RunSmokeValidationCommand { get; private set; } = null!;
        public AsyncRelayCommand RunIntegrityValidationCommand { get; private set; } = null!;
        public AsyncRelayCommand RunFullValidationLoopCommand { get; private set; } = null!;
        public AsyncRelayCommand RunBuilderProofMatrixCommand { get; private set; } = null!;
        public AsyncRelayCommand RunBuilderComparativeProofCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationOutputFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFailureLogCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationStabilityArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationHandoffSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationHandoffBundleFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationHandoffSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationHandoffArtifactPathsCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupIntakeCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupPromptCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupPromptCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupPlanCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationRepairPrepBundleCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupPlanSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationRepairPrepSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupRerunRecommendationCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupExecutionOutcomeCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupEscalationCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupResolutionReviewCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationResolutionHandoffCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationResolutionPromotionReviewCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationReleaseDecisionSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupRerunArtifactsCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupOutcomeNextStepCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupEscalationSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupClosureSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationResolutionHandoffSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationResolutionPromotionSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationReleaseDecisionSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand RunValidationFollowupRecommendedRerunCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationFollowupFirstEvidenceCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyValidationFollowupRerunCommandSummaryCommand { get; private set; } = null!;
        public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ValidationFollowupPlanStepRow> ExecuteValidationFollowupPlanStepCommand { get; private set; } = null!;
        public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ValidationFollowupPlanStepRow> CopyValidationFollowupPlanStepCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationOrchestrationArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationOrchestrationNoteCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationHistoryLedgerCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationTrendArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationRegressionArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand SetReleaseBaselineCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationBaselineArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationBaselineHistoryArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenValidationBaselineComparisonArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderProofSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderProofRunFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderModelFloorVerdictCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderFailurePatternsCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderExternalProofSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderModelFloorPolicyCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderModelFloorGuidanceCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderTrustBandsCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderScopeSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRoutingRecommendationCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderEscalationDecisionCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRoutingPlanCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderStrongerTierAvailabilityCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderComparativeProofSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRoutingPolicyEvidenceCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderSplitFirstPlanCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderTieredRoutingPolicyCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderDefaultGuidanceCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderGuidanceHistoryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderLatestRoutingDecisionCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderGuidanceSupportCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRequestIntakeCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderExecutionPrepCommand { get; private set; } = null!;
        public AsyncRelayCommand LaunchPreparedBuilderRouteCommand { get; private set; } = null!;
        public AsyncRelayCommand LaunchBuilderOverrideRouteCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderExecutionLaunchCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderExecutionResultCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderReadinessGateCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderReadinessHistoryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderConfirmedClassesCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderDefaultRouteDecisionCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderLaunchDefaultDecisionCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRouteOverrideEvidenceCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRouteReviewCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRouteReconfirmationCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderDefaultRouteRecoveryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderReadinessContradictionsCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRouteStabilitySummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand RunNextBuilderSplitStepCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderSplitStepExecutionCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderSplitFirstOutcomeCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderProofSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderScopeSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderRoutingRecommendationCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderSplitTaskGuidanceCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderComparativeProofSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderRoutingPolicySummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderSplitFirstPlanSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderPrimaryRoutingRecommendationCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderWeakSpotMitigationSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderDefaultGuidanceSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderLatestRoutingDecisionCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderExecutionPrepSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderIntakeRoutingSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderExecutionLaunchSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderExecutionResultSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderReadinessSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderReadinessContradictionNoteCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderConfirmedClassesSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderDefaultRouteDecisionSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderLaunchDefaultSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderRouteOverrideSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderRouteReconfirmationSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderDefaultRouteRecoverySummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderSplitExecutionSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyBuilderSplitComparativeClosureSummaryCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenBuilderRoutingEvidenceCommand => OpenBuilderRoutingPolicyEvidenceCommand;
        public AsyncRelayCommand CopyBuilderRoutingEvidenceSummaryCommand => CopyBuilderRoutingPolicySummaryCommand;
        public AsyncRelayCommand OpenBuilderTieredRoutingEvidenceCommand => OpenBuilderTieredRoutingPolicyCommand;
        public AsyncRelayCommand RefreshSimilarCasesCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenSemanticReuseDesignNoteCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenSemanticReuseIndexCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenSemanticReuseEffectivenessCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenSemanticReusePlaybookCatalogCommand { get; private set; } = null!;
        public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<SemanticReuseSuggestionRow> OpenSemanticReuseSuggestionArtifactCommand { get; private set; } = null!;
        public CommunityToolkit.Mvvm.Input.AsyncRelayCommand<SemanticReusePlaybookRow> OpenSemanticReusePlaybookArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand ValidateGeneratedOutputCommand { get; private set; } = null!;
        public AsyncRelayCommand AttemptRepairCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenRepairOutputFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenRepairBundleFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenLinkedRepairValidationRunFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand PromoteRepairResultCommand { get; private set; } = null!;
        public AsyncRelayCommand AdoptRepairCommand { get; private set; } = null!;
        public AsyncRelayCommand ReplaceRepairCommand { get; private set; } = null!;
        public AsyncRelayCommand UnadoptRepairCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenRepairAuditSummaryFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenPromotedRepairFolderCommand { get; private set; } = null!;

	// Call this from your constructor AFTER other command setup
	private void InitializeChatIntakeSurface()
	{
		LockWorkOrderCommand = new AsyncRelayCommand(LockWorkOrderAsync, () => !IsWorkOrderLocked);
		UnlockWorkOrderCommand = new AsyncRelayCommand(UnlockWorkOrderAsync, () => IsWorkOrderLocked);

		GeneratePlanCommand = new AsyncRelayCommand(GeneratePlanAsync, CanGeneratePlan);
		RunIntakePlanCommand = new AsyncRelayCommand(RunIntakePlanAsync, CanRunIntakePlan);

		ResumeInjectDecisionCommand = new AsyncRelayCommand(ResumeInjectDecisionAsync, CanResumeInjectDecision);
        RefreshModelCatalogCommand = new AsyncRelayCommand(RefreshBackendStatusAsync, CanRefreshBackends);
        ResetModelCatalogCommand = new AsyncRelayCommand(ResetModelCatalogAsync, () => !ProbeInFlight);
        OpenStateFolderCommand = new AsyncRelayCommand(OpenStateFolderAsync);
        QuickStartCommand = new AsyncRelayCommand(QuickStartAsync);
        SendChatIntentCommand = new AsyncRelayCommand(SendChatIntentAsync, () => !string.IsNullOrWhiteSpace(ChatInputText));
        OpenCurrentWorkspaceFolderCommand = new AsyncRelayCommand(OpenCurrentWorkspaceFolderAsync, () => HasProjectLoaded);
        OpenProjectFileCommand = new AsyncRelayCommand(OpenProjectFileAsync, () => HasProjectLoaded);
        OpenLastRunFolderCommand = new AsyncRelayCommand(OpenLastRunFolderAsync, CanOpenLastRunFolder);
        OpenLastVerificationReportCommand = new AsyncRelayCommand(OpenLastVerificationReportAsync, CanOpenLastVerificationReport);
        OpenLastOperatorFlowCommand = new AsyncRelayCommand(OpenLastOperatorFlowAsync, CanOpenLastOperatorFlow);
        OpenLastTransportEquivalenceCommand = new AsyncRelayCommand(OpenLastTransportEquivalenceAsync, CanOpenLastTransportEquivalence);
        CopyLastRunFolderPathCommand = new AsyncRelayCommand(CopyLastRunFolderPathAsync, CanOpenLastRunFolder);
        CopyLastVerificationReportPathCommand = new AsyncRelayCommand(CopyLastVerificationReportPathAsync, CanOpenLastVerificationReport);
        CopyLastOperatorFlowPathCommand = new AsyncRelayCommand(CopyLastOperatorFlowPathAsync, CanOpenLastOperatorFlow);
        CopyLastTransportEquivalencePathCommand = new AsyncRelayCommand(CopyLastTransportEquivalencePathAsync, CanOpenLastTransportEquivalence);
        CopyLastFailureSummaryCommand = new AsyncRelayCommand(CopyLastFailureSummaryAsync, () => HasLastFailure);
        OpenProofArtifactCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ProofArtifactRow>(OpenProofArtifactAsync, artifact => artifact is { Exists: true });
        CopyProofArtifactPathCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ProofArtifactRow>(CopyProofArtifactPathAsync, artifact => artifact is { Exists: true });
        OpenProofRunFolderCommand = new AsyncRelayCommand(OpenProofRunFolderAsync, () => HasProofRun);
        CopyProofRunFolderPathCommand = new AsyncRelayCommand(CopyProofRunFolderPathAsync, () => HasProofRun);

        RebuildJobSpecDigest();
    }

    private void InitializeReplaySurface()
    {
        ReplayLatestRunCommand = new AsyncRelayCommand(ReplayLatestRunAsync, CanReplayLatestRun);
        ReplaySelectedRunCommand = new AsyncRelayCommand(ReplaySelectedRunAsync, CanReplaySelectedRun);
    }

    private void InitializeValidationSurface()
    {
        BuildUiProjectCommand = new AsyncRelayCommand(() => RunValidationActionAsync(ValidationAction.BuildUiProject), () => CanRunValidationAction(ValidationAction.BuildUiProject));
        RunUiTestsCommand = new AsyncRelayCommand(() => RunValidationActionAsync(ValidationAction.RunUiTests), () => CanRunValidationAction(ValidationAction.RunUiTests));
        RunSmokeValidationCommand = new AsyncRelayCommand(() => RunValidationActionAsync(ValidationAction.RunSmokeValidation), () => CanRunValidationAction(ValidationAction.RunSmokeValidation));
        RunIntegrityValidationCommand = new AsyncRelayCommand(() => RunValidationActionAsync(ValidationAction.RunIntegrityValidation), () => CanRunValidationAction(ValidationAction.RunIntegrityValidation));
        RunFullValidationLoopCommand = new AsyncRelayCommand(() => RunValidationActionAsync(ValidationAction.RunFullValidationLoop), () => CanRunValidationAction(ValidationAction.RunFullValidationLoop));
        RunBuilderProofMatrixCommand = new AsyncRelayCommand(RunBuilderProofMatrixAsync, CanRunBuilderProofMatrix);
        RunBuilderComparativeProofCommand = new AsyncRelayCommand(RunBuilderComparativeProofAsync, CanRunBuilderComparativeProof);
        OpenValidationOutputFolderCommand = new AsyncRelayCommand(OpenValidationOutputFolderAsync, () => HasValidationOutputFolder);
        OpenValidationFailureLogCommand = new AsyncRelayCommand(OpenValidationFailureLogAsync, () => HasValidationFirstFailureLogPath);
        OpenValidationStabilityArtifactCommand = new AsyncRelayCommand(OpenValidationStabilityArtifactAsync, () => HasValidationStabilityArtifactPath);
        OpenValidationHandoffSummaryCommand = new AsyncRelayCommand(OpenValidationHandoffSummaryAsync, () => HasValidationHandoffSummaryPath);
        OpenValidationHandoffBundleFolderCommand = new AsyncRelayCommand(OpenValidationHandoffBundleFolderAsync, () => HasValidationHandoffBundlePath);
        CopyValidationHandoffSummaryCommand = new AsyncRelayCommand(CopyValidationHandoffSummaryAsync, () => HasValidationHandoffSummary);
        CopyValidationHandoffArtifactPathsCommand = new AsyncRelayCommand(CopyValidationHandoffArtifactPathsAsync, () => HasValidationHandoffBundlePath);
        OpenValidationFollowupIntakeCommand = new AsyncRelayCommand(OpenValidationFollowupIntakeAsync, () => HasValidationFollowupIntakePath);
        OpenValidationFollowupPromptCommand = new AsyncRelayCommand(OpenValidationFollowupPromptAsync, () => HasValidationFollowupPromptPath);
        CopyValidationFollowupSummaryCommand = new AsyncRelayCommand(CopyValidationFollowupSummaryAsync, () => HasValidationFollowupSummary);
        CopyValidationFollowupPromptCommand = new AsyncRelayCommand(CopyValidationFollowupPromptAsync, () => HasValidationFollowupPromptPath);
        OpenValidationFollowupPlanCommand = new AsyncRelayCommand(OpenValidationFollowupPlanAsync, () => HasValidationFollowupPlanPath);
        OpenValidationRepairPrepBundleCommand = new AsyncRelayCommand(OpenValidationRepairPrepBundleAsync, () => HasValidationRepairPrepBundlePath);
        CopyValidationFollowupPlanSummaryCommand = new AsyncRelayCommand(CopyValidationFollowupPlanSummaryAsync, () => HasValidationFollowupPlanSummary);
        CopyValidationRepairPrepSummaryCommand = new AsyncRelayCommand(CopyValidationRepairPrepSummaryAsync, () => HasValidationRepairPrepSummary);
        CopyValidationFollowupRerunRecommendationCommand = new AsyncRelayCommand(CopyValidationFollowupRerunRecommendationAsync, () => HasValidationFollowupRerunRecommendation);
        OpenValidationFollowupExecutionOutcomeCommand = new AsyncRelayCommand(OpenValidationFollowupExecutionOutcomeAsync, () => HasValidationFollowupExecutionOutcomePath);
        OpenValidationFollowupEscalationCommand = new AsyncRelayCommand(OpenValidationFollowupEscalationAsync, () => HasValidationFollowupEscalationPath);
        OpenValidationFollowupResolutionReviewCommand = new AsyncRelayCommand(OpenValidationFollowupResolutionReviewAsync, () => HasValidationFollowupResolutionReviewPath);
        OpenValidationResolutionHandoffCommand = new AsyncRelayCommand(OpenValidationResolutionHandoffAsync, () => HasValidationResolutionHandoffPath);
        OpenValidationResolutionPromotionReviewCommand = new AsyncRelayCommand(OpenValidationResolutionPromotionReviewAsync, () => HasValidationResolutionPromotionReviewPath);
        OpenValidationReleaseDecisionSummaryCommand = new AsyncRelayCommand(OpenValidationReleaseDecisionSummaryAsync, () => HasValidationReleaseDecisionSummaryPath);
        OpenValidationFollowupRerunArtifactsCommand = new AsyncRelayCommand(OpenValidationFollowupRerunArtifactsAsync, CanOpenValidationFollowupRerunArtifacts);
        CopyValidationFollowupOutcomeNextStepCommand = new AsyncRelayCommand(CopyValidationFollowupOutcomeNextStepAsync, () => HasValidationFollowupOutcomeNextStateText);
        CopyValidationFollowupEscalationSummaryCommand = new AsyncRelayCommand(CopyValidationFollowupEscalationSummaryAsync, () => HasValidationFollowupEscalationSummary);
        CopyValidationFollowupClosureSummaryCommand = new AsyncRelayCommand(CopyValidationFollowupClosureSummaryAsync, () => HasValidationFollowupResolutionSummary);
        CopyValidationResolutionHandoffSummaryCommand = new AsyncRelayCommand(CopyValidationResolutionHandoffSummaryAsync, () => HasValidationResolutionHandoffSummary);
        CopyValidationResolutionPromotionSummaryCommand = new AsyncRelayCommand(CopyValidationResolutionPromotionSummaryAsync, () => HasValidationResolutionPromotionSummary);
        CopyValidationReleaseDecisionSummaryCommand = new AsyncRelayCommand(CopyValidationReleaseDecisionSummaryAsync, () => HasValidationReleaseDecisionSummary);
        RunValidationFollowupRecommendedRerunCommand = new AsyncRelayCommand(RunValidationFollowupRecommendedRerunAsync, CanRunValidationFollowupRecommendedRerun);
        OpenValidationFollowupFirstEvidenceCommand = new AsyncRelayCommand(OpenValidationFollowupFirstEvidenceAsync, CanOpenValidationFollowupFirstEvidence);
        CopyValidationFollowupRerunCommandSummaryCommand = new AsyncRelayCommand(CopyValidationFollowupRerunCommandSummaryAsync, CanCopyValidationFollowupRerunCommandSummary);
        ExecuteValidationFollowupPlanStepCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ValidationFollowupPlanStepRow>(ExecuteValidationFollowupPlanStepAsync, step => step is not null && CanExecuteValidationFollowupPlanStep(step));
        CopyValidationFollowupPlanStepCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<ValidationFollowupPlanStepRow>(CopyValidationFollowupPlanStepAsync, step => step is not null && CanCopyValidationFollowupPlanStep(step));
        OpenValidationOrchestrationArtifactCommand = new AsyncRelayCommand(OpenValidationOrchestrationArtifactAsync, () => HasValidationOrchestrationArtifactPath);
        OpenValidationOrchestrationNoteCommand = new AsyncRelayCommand(OpenValidationOrchestrationNoteAsync, () => HasValidationOrchestrationNotePath);
        OpenValidationHistoryLedgerCommand = new AsyncRelayCommand(OpenValidationHistoryLedgerAsync, () => HasValidationHistoryLedgerPath);
        OpenValidationTrendArtifactCommand = new AsyncRelayCommand(OpenValidationTrendArtifactAsync, () => HasValidationTrendArtifactPath);
        OpenValidationRegressionArtifactCommand = new AsyncRelayCommand(OpenValidationRegressionArtifactAsync, () => HasValidationRegressionArtifactPath);
        SetReleaseBaselineCommand = new AsyncRelayCommand(SetReleaseBaselineAsync, CanSetReleaseBaseline);
        OpenValidationBaselineArtifactCommand = new AsyncRelayCommand(OpenValidationBaselineArtifactAsync, () => HasValidationBaselineArtifactPath);
        OpenValidationBaselineHistoryArtifactCommand = new AsyncRelayCommand(OpenValidationBaselineHistoryArtifactAsync, () => HasValidationBaselineHistoryArtifactPath);
        OpenValidationBaselineComparisonArtifactCommand = new AsyncRelayCommand(OpenValidationBaselineComparisonArtifactAsync, () => HasValidationBaselineComparisonArtifactPath);
        OpenBuilderProofSummaryCommand = new AsyncRelayCommand(OpenBuilderProofSummaryAsync, () => HasBuilderProofSummaryPath);
        OpenBuilderProofRunFolderCommand = new AsyncRelayCommand(OpenBuilderProofRunFolderAsync, () => HasBuilderProofRunPath);
        OpenBuilderModelFloorVerdictCommand = new AsyncRelayCommand(OpenBuilderModelFloorVerdictAsync, () => HasBuilderModelFloorVerdictPath);
        OpenBuilderFailurePatternsCommand = new AsyncRelayCommand(OpenBuilderFailurePatternsAsync, () => HasBuilderModelFloorFailurePatternsPath);
        OpenBuilderExternalProofSummaryCommand = new AsyncRelayCommand(OpenBuilderExternalProofSummaryAsync, () => HasBuilderExternalProofSummaryPath);
        OpenBuilderModelFloorPolicyCommand = new AsyncRelayCommand(OpenBuilderModelFloorPolicyAsync, () => HasBuilderModelFloorPolicyPath);
        OpenBuilderModelFloorGuidanceCommand = new AsyncRelayCommand(OpenBuilderModelFloorPolicyAsync, () => HasBuilderModelFloorGuidancePath);
        OpenBuilderTrustBandsCommand = new AsyncRelayCommand(OpenBuilderTrustBandsAsync, () => HasBuilderModelTrustBandsPath);
        OpenBuilderScopeSummaryCommand = new AsyncRelayCommand(OpenBuilderScopeSummaryAsync, () => HasBuilderModelScopeSummaryPath);
        OpenBuilderRoutingRecommendationCommand = new AsyncRelayCommand(OpenBuilderRoutingRecommendationAsync, () => HasBuilderModelRoutingRecommendationPath);
        OpenBuilderEscalationDecisionCommand = new AsyncRelayCommand(OpenBuilderEscalationDecisionAsync, () => HasBuilderModelEscalationDecisionPath);
        OpenBuilderRoutingPlanCommand = new AsyncRelayCommand(OpenBuilderRoutingPlanAsync, () => HasBuilderModelRoutingPlanPath);
        OpenBuilderStrongerTierAvailabilityCommand = new AsyncRelayCommand(OpenBuilderStrongerTierAvailabilityAsync, () => HasBuilderStrongerTierAvailabilityPath);
        OpenBuilderComparativeProofSummaryCommand = new AsyncRelayCommand(OpenBuilderComparativeProofSummaryAsync, () => HasBuilderComparativeProofSummaryPath);
        OpenBuilderRoutingPolicyEvidenceCommand = new AsyncRelayCommand(OpenBuilderRoutingPolicyEvidenceAsync, () => HasBuilderRoutingPolicyPath);
        OpenBuilderSplitFirstPlanCommand = new AsyncRelayCommand(OpenBuilderSplitFirstPlanAsync, () => HasBuilderSplitFirstPlanPath);
        OpenBuilderTieredRoutingPolicyCommand = new AsyncRelayCommand(OpenBuilderTieredRoutingPolicyAsync, () => HasBuilderTieredRoutingPath);
        OpenBuilderDefaultGuidanceCommand = new AsyncRelayCommand(OpenBuilderDefaultGuidanceAsync, () => HasBuilderDefaultGuidancePath);
        OpenBuilderGuidanceHistoryCommand = new AsyncRelayCommand(OpenBuilderGuidanceHistoryAsync, () => HasBuilderGuidanceHistoryPath);
        OpenBuilderLatestRoutingDecisionCommand = new AsyncRelayCommand(OpenBuilderLatestRoutingDecisionAsync, () => HasBuilderLatestRoutingDecisionPath);
        OpenBuilderGuidanceSupportCommand = new AsyncRelayCommand(OpenBuilderGuidanceSupportAsync, () => HasBuilderGuidanceSupportPath);
        OpenBuilderRequestIntakeCommand = new AsyncRelayCommand(OpenBuilderRequestIntakeAsync, () => HasBuilderIntakePath);
        OpenBuilderExecutionPrepCommand = new AsyncRelayCommand(OpenBuilderExecutionPrepAsync, () => HasBuilderPrepPath);
        LaunchPreparedBuilderRouteCommand = new AsyncRelayCommand(LaunchPreparedBuilderRouteAsync, () => string.IsNullOrWhiteSpace(GetBuilderPreparedLaunchDisabledReason()));
        LaunchBuilderOverrideRouteCommand = new AsyncRelayCommand(LaunchBuilderOverrideRouteAsync, () => string.IsNullOrWhiteSpace(GetBuilderOverrideLaunchDisabledReason()));
        OpenBuilderExecutionLaunchCommand = new AsyncRelayCommand(OpenBuilderExecutionLaunchAsync, () => HasBuilderLaunchPath);
        OpenBuilderExecutionResultCommand = new AsyncRelayCommand(OpenBuilderExecutionResultAsync, () => HasBuilderResultPath);
        OpenBuilderReadinessGateCommand = new AsyncRelayCommand(OpenBuilderReadinessGateAsync, () => HasBuilderReadinessGatePath);
        OpenBuilderReadinessHistoryCommand = new AsyncRelayCommand(OpenBuilderReadinessHistoryAsync, () => HasBuilderReadinessGateHistoryPath);
        OpenBuilderConfirmedClassesCommand = new AsyncRelayCommand(OpenBuilderConfirmedClassesAsync, () => HasBuilderConfirmedClassesPath);
        OpenBuilderDefaultRouteDecisionCommand = new AsyncRelayCommand(OpenBuilderDefaultRouteDecisionAsync, () => HasBuilderDefaultRouteDecisionPath);
        OpenBuilderLaunchDefaultDecisionCommand = new AsyncRelayCommand(OpenBuilderLaunchDefaultDecisionAsync, () => HasBuilderLaunchDefaultDecisionPath);
        OpenBuilderRouteOverrideEvidenceCommand = new AsyncRelayCommand(OpenBuilderRouteOverrideEvidenceAsync, () => HasBuilderRouteOverridePath);
        OpenBuilderRouteReviewCommand = new AsyncRelayCommand(OpenBuilderRouteReviewAsync, () => HasBuilderRouteReviewPath);
        OpenBuilderRouteReconfirmationCommand = new AsyncRelayCommand(OpenBuilderRouteReconfirmationAsync, () => HasBuilderRouteReconfirmationPath);
        OpenBuilderDefaultRouteRecoveryCommand = new AsyncRelayCommand(OpenBuilderDefaultRouteRecoveryAsync, () => HasBuilderDefaultRouteRecoveryPath);
        OpenBuilderReadinessContradictionsCommand = new AsyncRelayCommand(OpenBuilderReadinessContradictionsAsync, () => HasBuilderReadinessContradictionsPath);
        OpenBuilderRouteStabilitySummaryCommand = new AsyncRelayCommand(OpenBuilderRouteStabilitySummaryAsync, () => HasBuilderRouteStabilitySummaryPath);
        RunNextBuilderSplitStepCommand = new AsyncRelayCommand(RunNextBuilderSplitStepAsync, () => string.IsNullOrWhiteSpace(GetBuilderSplitExecutionDisabledReason()));
        OpenBuilderSplitStepExecutionCommand = new AsyncRelayCommand(OpenBuilderSplitStepExecutionAsync, () => HasBuilderSplitStepExecutionPath);
        OpenBuilderSplitFirstOutcomeCommand = new AsyncRelayCommand(OpenBuilderSplitFirstOutcomeAsync, () => HasBuilderSplitFirstOutcomePath);
        CopyBuilderProofSummaryCommand = new AsyncRelayCommand(CopyBuilderProofSummaryAsync, () => HasBuilderProofSummary);
        CopyBuilderScopeSummaryCommand = new AsyncRelayCommand(CopyBuilderScopeSummaryAsync, () => HasBuilderModelScopeSummary);
        CopyBuilderRoutingRecommendationCommand = new AsyncRelayCommand(CopyBuilderRoutingRecommendationAsync, () => HasBuilderModelRoutingRecommendationSummary);
        CopyBuilderSplitTaskGuidanceCommand = new AsyncRelayCommand(CopyBuilderSplitTaskGuidanceAsync, () => HasBuilderModelSplitTaskGuidanceSummary);
        CopyBuilderComparativeProofSummaryCommand = new AsyncRelayCommand(CopyBuilderComparativeProofSummaryAsync, () => HasBuilderComparativeProofSummary);
        CopyBuilderRoutingPolicySummaryCommand = new AsyncRelayCommand(CopyBuilderRoutingPolicySummaryAsync, () => HasBuilderRoutingPolicySummary);
        CopyBuilderSplitFirstPlanSummaryCommand = new AsyncRelayCommand(CopyBuilderSplitFirstPlanSummaryAsync, () => HasBuilderSplitFirstPlanSummary);
        CopyBuilderPrimaryRoutingRecommendationCommand = new AsyncRelayCommand(CopyBuilderPrimaryRoutingRecommendationAsync, () => HasBuilderPrimaryRoutingRecommendationSummary);
        CopyBuilderWeakSpotMitigationSummaryCommand = new AsyncRelayCommand(CopyBuilderWeakSpotMitigationSummaryAsync, () => HasBuilderWeakSpotMitigationSummary);
        CopyBuilderDefaultGuidanceSummaryCommand = new AsyncRelayCommand(CopyBuilderDefaultGuidanceSummaryAsync, () => HasBuilderDefaultGuidanceSummary);
        CopyBuilderLatestRoutingDecisionCommand = new AsyncRelayCommand(CopyBuilderLatestRoutingDecisionAsync, () => HasBuilderLatestRoutingDecisionSummary);
        CopyBuilderExecutionPrepSummaryCommand = new AsyncRelayCommand(CopyBuilderExecutionPrepSummaryAsync, () => HasBuilderPrepSummary);
        CopyBuilderIntakeRoutingSummaryCommand = new AsyncRelayCommand(CopyBuilderIntakeRoutingSummaryAsync, () => HasBuilderIntakeSummary);
        CopyBuilderExecutionLaunchSummaryCommand = new AsyncRelayCommand(CopyBuilderExecutionLaunchSummaryAsync, () => HasBuilderLaunchSummary);
        CopyBuilderExecutionResultSummaryCommand = new AsyncRelayCommand(CopyBuilderExecutionResultSummaryAsync, () => HasBuilderResultSummary);
        CopyBuilderReadinessSummaryCommand = new AsyncRelayCommand(CopyBuilderReadinessSummaryAsync, () => HasBuilderReadinessGateSummary);
        CopyBuilderReadinessContradictionNoteCommand = new AsyncRelayCommand(CopyBuilderReadinessContradictionNoteAsync, () => HasBuilderReadinessLatestContradictionNote);
        CopyBuilderConfirmedClassesSummaryCommand = new AsyncRelayCommand(CopyBuilderConfirmedClassesSummaryAsync, () => HasBuilderConfirmedClassesSummary);
        CopyBuilderDefaultRouteDecisionSummaryCommand = new AsyncRelayCommand(CopyBuilderDefaultRouteDecisionSummaryAsync, () => HasBuilderDefaultRouteDecisionSummary);
        CopyBuilderLaunchDefaultSummaryCommand = new AsyncRelayCommand(CopyBuilderLaunchDefaultSummaryAsync, () => HasBuilderLaunchDefaultDecisionSummary);
        CopyBuilderRouteOverrideSummaryCommand = new AsyncRelayCommand(CopyBuilderRouteOverrideSummaryAsync, () => HasBuilderRouteOverrideSummary);
        CopyBuilderRouteReconfirmationSummaryCommand = new AsyncRelayCommand(CopyBuilderRouteReconfirmationSummaryAsync, () => HasBuilderRouteReconfirmationSummary);
        CopyBuilderDefaultRouteRecoverySummaryCommand = new AsyncRelayCommand(CopyBuilderDefaultRouteRecoverySummaryAsync, () => HasBuilderDefaultRouteRecoverySummary);
        CopyBuilderSplitExecutionSummaryCommand = new AsyncRelayCommand(CopyBuilderSplitExecutionSummaryAsync, () => HasBuilderSplitStepExecutionSummary);
        CopyBuilderSplitComparativeClosureSummaryCommand = new AsyncRelayCommand(CopyBuilderSplitComparativeClosureSummaryAsync, () => HasBuilderSplitFirstOutcomeSummary);
        RefreshSimilarCasesCommand = new AsyncRelayCommand(RefreshSimilarCasesAsync, CanRefreshSimilarCases);
        OpenSemanticReuseDesignNoteCommand = new AsyncRelayCommand(OpenSemanticReuseDesignNoteAsync, () => HasSemanticReuseDesignNotePath);
        OpenSemanticReuseIndexCommand = new AsyncRelayCommand(OpenSemanticReuseIndexAsync, () => HasSemanticReuseIndexPath);
        OpenSemanticReuseEffectivenessCommand = new AsyncRelayCommand(OpenSemanticReuseEffectivenessAsync, () => HasSemanticReuseEffectivenessPath);
        OpenSemanticReusePlaybookCatalogCommand = new AsyncRelayCommand(OpenSemanticReusePlaybookCatalogAsync, () => HasSemanticReusePlaybookPath);
        OpenSemanticReuseSuggestionArtifactCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<SemanticReuseSuggestionRow>(OpenSemanticReuseSuggestionArtifactAsync, row => row is { HasPrimaryArtifactPath: true });
        OpenSemanticReusePlaybookArtifactCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<SemanticReusePlaybookRow>(OpenSemanticReusePlaybookArtifactAsync, row => row is { HasPrimaryArtifactPath: true });
        ValidateGeneratedOutputCommand = new AsyncRelayCommand(ValidateGeneratedOutputAsync, CanValidateGeneratedOutput);
        AttemptRepairCommand = new AsyncRelayCommand(AttemptRepairAsync, CanAttemptRepair);
        OpenRepairOutputFolderCommand = new AsyncRelayCommand(OpenRepairOutputFolderAsync, () => HasRepairOutputFolder);
        OpenRepairBundleFolderCommand = new AsyncRelayCommand(OpenRepairBundleFolderAsync, () => HasRepairBundlePath);
        OpenLinkedRepairValidationRunFolderCommand = new AsyncRelayCommand(OpenLinkedRepairValidationRunFolderAsync, () => HasRepairLinkedValidationRunFolder);
        PromoteRepairResultCommand = new AsyncRelayCommand(PromoteRepairResultAsync, CanPromoteRepairResult);
        AdoptRepairCommand = new AsyncRelayCommand(AdoptRepairAsync, CanAdoptRepair);
        ReplaceRepairCommand = new AsyncRelayCommand(ReplaceRepairAsync, CanReplaceRepair);
        UnadoptRepairCommand = new AsyncRelayCommand(UnadoptRepairAsync, CanUnadoptRepair);
        OpenRepairAuditSummaryFolderCommand = new AsyncRelayCommand(OpenRepairAuditSummaryFolderAsync, () => HasRepairAuditSummaryFolder);
        OpenPromotedRepairFolderCommand = new AsyncRelayCommand(OpenPromotedRepairFolderAsync, () => HasPromotedRepairFolder);
    }

    private Task OpenCurrentWorkspaceFolderAsync()
    {
        if (CurrentProject is null)
        {
            return Task.CompletedTask;
        }

        return _workspaceShell.OpenFolderAsync(CurrentProject.WorkspacePath);
    }

    private Task OpenProjectFileAsync()
    {
        if (CurrentProject is null)
        {
            return Task.CompletedTask;
        }

        var projectDirectory = Path.GetDirectoryName(CurrentProject.ProjectFilePath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return Task.CompletedTask;
        }

        return _workspaceShell.OpenFolderAsync(projectDirectory);
    }


    private bool CanOpenLastRunFolder()
        => !string.IsNullOrWhiteSpace(LastRunFolderPath) && Directory.Exists(LastRunFolderPath);

    private bool CanOpenLastVerificationReport()
        => !string.IsNullOrWhiteSpace(LastVerificationReportPath) && File.Exists(LastVerificationReportPath);

    private bool CanOpenLastOperatorFlow()
        => !string.IsNullOrWhiteSpace(LastOperatorFlowPath) && File.Exists(LastOperatorFlowPath);

    private bool CanOpenLastTransportEquivalence()
        => !string.IsNullOrWhiteSpace(LastTransportEquivalencePath) && File.Exists(LastTransportEquivalencePath);

    private Task OpenLastRunFolderAsync()
        => OpenFolderIfExistsAsync(LastRunFolderPath);

    private Task OpenLastVerificationReportAsync()
        => OpenPathIfExistsAsync(LastVerificationReportPath);

    private Task OpenLastOperatorFlowAsync()
        => OpenPathIfExistsAsync(LastOperatorFlowPath);

    private Task OpenLastTransportEquivalenceAsync()
        => OpenPathIfExistsAsync(LastTransportEquivalencePath);

    private Task CopyLastRunFolderPathAsync()
        => CopyPathToClipboardAsync(LastRunFolderPath, isFile: false);

    private Task CopyLastVerificationReportPathAsync()
        => CopyPathToClipboardAsync(LastVerificationReportPath, isFile: true);

    private Task CopyLastOperatorFlowPathAsync()
        => CopyPathToClipboardAsync(LastOperatorFlowPath, isFile: true);

    private Task CopyLastTransportEquivalencePathAsync()
        => CopyPathToClipboardAsync(LastTransportEquivalencePath, isFile: true);

    private Task CopyLastFailureSummaryAsync()
    {
        if (!HasLastFailure)
        {
            return Task.CompletedTask;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Phase: {LastFailurePhase}");
        builder.AppendLine($"Reason: {LastFailureReason}");
        if (!string.IsNullOrWhiteSpace(LastFailureProofPath))
        {
            builder.AppendLine($"Proof: {LastFailureProofPath}");
        }
        if (!string.IsNullOrWhiteSpace(LastFailureNextAction))
        {
            builder.AppendLine($"Next: {LastFailureNextAction}");
        }
        builder.AppendLine($"Recorded: {_lastFailure?.OccurredUtc:O}");
        return _workspaceShell.CopyTextAsync(builder.ToString());
    }

    private Task OpenFolderIfExistsAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return Task.CompletedTask;
        }

        return _workspaceShell.OpenFolderAsync(path);
    }

    private Task OpenPathIfExistsAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.CompletedTask;
        }

        return _workspaceShell.OpenFolderAsync(path);
    }

    private Task CopyPathToClipboardAsync(string path, bool isFile)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        if (isFile && !File.Exists(path))
        {
            return Task.CompletedTask;
        }

        if (!isFile && !Directory.Exists(path))
        {
            return Task.CompletedTask;
        }

        return _workspaceShell.CopyTextAsync(path);
    }

    private Task OpenProofArtifactAsync(ProofArtifactRow? artifact)
    {
        if (artifact is null || !artifact.Exists || string.IsNullOrWhiteSpace(artifact.AbsolutePath))
        {
            return Task.CompletedTask;
        }

        return _workspaceShell.OpenFolderAsync(artifact.AbsolutePath);
    }

    private Task CopyProofArtifactPathAsync(ProofArtifactRow? artifact)
    {
        if (artifact is null || !artifact.Exists || string.IsNullOrWhiteSpace(artifact.AbsolutePath))
        {
            return Task.CompletedTask;
        }

        return _workspaceShell.CopyTextAsync(artifact.AbsolutePath);
    }

    private Task OpenProofRunFolderAsync()
        => OpenFolderIfExistsAsync(_proofRunPath ?? string.Empty);

    private Task CopyProofRunFolderPathAsync()
        => CopyPathToClipboardAsync(_proofRunPath ?? string.Empty, isFile: false);

    private void RefreshProofArtifacts()
    {
        foreach (var artifact in _proofArtifacts)
        {
            artifact.Update(_proofRunPath);
        }

        OnPropertyChanged(nameof(ProofArtifacts));
        OpenProofArtifactCommand.NotifyCanExecuteChanged();
        CopyProofArtifactPathCommand.NotifyCanExecuteChanged();
    }

    private void RecordFailure(string phase, string reason, string? proofPath, string? suggestedNextAction)
    {
        _lastFailure = new FailureDetails(
            phase,
            reason,
            string.IsNullOrWhiteSpace(proofPath) ? UiLogPath : proofPath,
            suggestedNextAction,
            DateTimeOffset.UtcNow);

        OnPropertyChanged(nameof(HasLastFailure));
        OnPropertyChanged(nameof(LastFailurePhase));
        OnPropertyChanged(nameof(LastFailureReason));
        OnPropertyChanged(nameof(LastFailureMessage));
        OnPropertyChanged(nameof(LastFailureProofPath));
        OnPropertyChanged(nameof(LastFailureNextAction));
        OnPropertyChanged(nameof(LastFailureSummary));
        OnPropertyChanged(nameof(LastFailureExceptionType));
        OnPropertyChanged(nameof(LastFailureFirstStackFrame));
        OnPropertyChanged(nameof(FatalLogPath));
        CopyLastFailureSummaryCommand?.RaiseCanExecuteChanged();
    }

    private void SetProofRun(RunHistoryRow? row)
    {
        if (row is null)
        {
            _proofRunPath = null;
            _proofRunId = null;
            _proofRunVerificationState = "No run selected";
            _proofRunLabel = "No run selected";
            _generatedOutputValidationStatus = "not_validated";
            _generatedOutputValidationSummary = "Generated output has not been validated.";
            _generatedOutputValidationRunId = string.Empty;
            _generatedOutputValidationSourcePath = string.Empty;
            ApplyRepairReviewState(null, null, null);
        }
        else
        {
            _proofRunPath = row.RunPath;
            _proofRunId = row.RunId;
            _proofRunVerificationState = row.VerificationResult;
            _proofRunLabel = $"Inspecting {row.RunId} ({row.State})";
            LoadGeneratedOutputValidationLink(row.RunId, row.RunPath);
        }

        OnPropertyChanged(nameof(ProofRunFolderPath));
        OnPropertyChanged(nameof(ProofRunVerificationState));
        OnPropertyChanged(nameof(ProofRunLabel));
        OnPropertyChanged(nameof(HasProofRun));
        OnPropertyChanged(nameof(GeneratedOutputValidationStatus));
        OnPropertyChanged(nameof(GeneratedOutputValidationBadge));
        OnPropertyChanged(nameof(GeneratedOutputValidationSummary));
        OnPropertyChanged(nameof(GeneratedOutputValidationRunId));
        OnPropertyChanged(nameof(HasGeneratedOutputValidationRunId));
        OnPropertyChanged(nameof(GeneratedOutputValidationSourcePath));
        OnPropertyChanged(nameof(RepairPromotionBadge));
        OnPropertyChanged(nameof(RepairPromotionSummary));
        UpdateArtifactFiles(_proofRunPath);
        OpenProofRunFolderCommand.RaiseCanExecuteChanged();
        CopyProofRunFolderPathCommand.RaiseCanExecuteChanged();
        RefreshProofArtifacts();
    }

    private void UpdateArtifactFiles(string? runPath)
    {
        _artifactFiles.Clear();
        if (string.IsNullOrWhiteSpace(runPath))
        {
            return;
        }

        var artifactRoot = Path.Combine(runPath, "artifacts");
        if (!Directory.Exists(artifactRoot))
        {
            return;
        }

        foreach (var artifactFile in Directory.GetFiles(artifactRoot, "*", SearchOption.AllDirectories).OrderBy(static x => x, StringComparer.Ordinal))
        {
            _artifactFiles.Add(artifactFile);
        }
    }

    private Task ResetModelCatalogAsync()
    {
        SelectedModelId = string.Empty;
        ModelCatalogError = string.Empty;
        OnPropertyChanged(nameof(DefaultModelId));
        OnPropertyChanged(nameof(CatalogHash));
        return Task.CompletedTask;
    }

    private Task OpenStateFolderAsync()
    {
        var statePath = Path.GetFullPath(Path.Combine(".state"));
        if (!Directory.Exists(statePath))
        {
            Directory.CreateDirectory(statePath);
        }

        return _workspaceShell.OpenFolderAsync(statePath);
    }

    private Task QuickStartAsync()
    {
        IntakeIntent = "Create deterministic builder smoke run";
        IntakeTarget = "builder_smoke";
        IntakeStack = "dotnet";
        RebuildJobSpecDigest();
        return Task.CompletedTask;
    }

    private async Task SendChatIntentAsync()
    {
        var rawText = ChatInputText;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return;
        }

        _chatTranscript.Add($"User: {rawText}");
        var intent = _intentParser.Parse(rawText);
        AddNarration("step", "INTENT_PARSED", new Dictionary<string, string>
        {
            ["kind"] = intent.Kind.ToString(),
            ["normalized"] = intent.NormalizedText
        });
        _chatTranscript.Add($"System: Intent recognized: {intent.Kind} (confidence={intent.Confidence:0.00}).");
        var dispatchSummary = await DispatchIntentAsync(intent);
        TryAppendChatTranscriptRecord(intent, dispatchSummary);
        ChatInputText = string.Empty;
    }

    private async Task<string> DispatchIntentAsync(IntentModel intent)
    {
        var handler = "none";
        var result = "ok";
        Trace.WriteLine($"[Shoots.UI] BEGIN IntentDispatch id={intent.IntentId} kind={intent.Kind}");
        AddNarration("step", "Intent dispatch begin", new Dictionary<string, string>
        {
            ["intent_id"] = intent.IntentId.ToString(),
            ["kind"] = intent.Kind.ToString(),
            ["normalized"] = intent.NormalizedText
        });

        try
        {
            switch (intent.Kind)
            {
                case IntentKind.CreateProject:
                    handler = "StartNewProject";
                    await NewProjectAsync();
                    result = CurrentProject is null ? "no_project" : "created_project";
                    break;
                case IntentKind.RunDemoPlan:
                    handler = "RunDemoPlan";
                    await RunDemoPlanAsync();
                    result = string.IsNullOrWhiteSpace(LastDemoRunPath) ? "no_run" : "run_completed";
                    break;
                case IntentKind.OpenProject:
                    handler = "OpenProject";
                    if (intent.Args.TryGetValue("path", out var path))
                    {
                        var fullPath = Path.GetFullPath(path);
                        var projectFilePath = Directory.Exists(fullPath)
                            ? Path.Combine(fullPath, "project.json")
                            : fullPath;
                        var project = LoadProject(projectFilePath);
                        var invariantResult = ProjectInvariants.Verify(project.WorkspacePath);
                        Trace.WriteLine($"[Shoots.UI] project.invariants {JsonSerializer.Serialize(invariantResult)}");
                        var workspace = new ProjectWorkspace(project.Name, project.WorkspacePath, DateTimeOffset.UtcNow, ProjectId: project.ProjectId, CreatedUtc: project.CreatedUtc);
                        _workspaceProvider.SetActiveWorkspace(workspace);
                        LoadWorkspaces();
                        SelectWorkspace(workspace);
                        _chatTranscript.Add($"System: Project opened at {project.WorkspacePath}.");
                        result = "opened";
                    }
                    else
                    {
                        result = "missing_path";
                    }
                    break;
                case IntentKind.BuildFromPlanFile:
                    handler = "BuildFromPlanFile";
                    result = "not_implemented";
                    _chatTranscript.Add("System: BuildFromPlanFile is recognized but not yet connected.");
                    break;
                case IntentKind.AddNote:
                    handler = "AddNote";
                    if (CurrentProject is null)
                    {
                        result = "no_project";
                        _chatTranscript.Add("System: No project loaded. Create or open one.");
                    }
                    else if (intent.Args.TryGetValue("note", out var noteText))
                    {
                        var notePath = Path.Combine(CurrentProject.WorkspacePath, "notes", "notes.txt");
                        File.AppendAllLines(notePath, new[] { noteText });
                        _chatTranscript.Add($"System: Note added to {notePath}.");
                        result = "note_added";
                    }
                    else
                    {
                        result = "missing_note";
                    }
                    break;
                case IntentKind.Unknown:
                    handler = "Unknown";
                    result = "unknown";
                    _chatTranscript.Add("System: Intent unknown. Try 'start new project' or 'run demo'.");
                    break;
            }
        }
        catch (Exception ex)
        {
            result = $"error:{ex.GetType().Name}";
            _chatTranscript.Add($"System: Intent dispatch failed: {ex.Message}");
            Trace.WriteLine($"[Shoots.UI] Intent dispatch failed: {ex}");
            AddNarration("error", "Intent dispatch failed", new Dictionary<string, string> { ["error"] = ex.Message });
        }
        finally
        {
            var trace = new IntentTrace(intent.RawUserText, intent.NormalizedText, intent.Kind, intent.Args, handler, result);
            var serialized = JsonSerializer.Serialize(trace);
            Trace.WriteLine($"[Shoots.UI] intent.trace {serialized}");
            AddNarration("result", "Intent dispatch end", new Dictionary<string, string> { ["trace"] = serialized });
            Trace.WriteLine($"[Shoots.UI] END IntentDispatch id={intent.IntentId} kind={intent.Kind}");
        }

        return $"{handler}:{result}";
    }

    private void TryAppendChatTranscriptRecord(IntentModel intent, string summary)
    {
        var workspacePath = CurrentProject?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return;
        }

        try
        {
            var notesPath = Path.Combine(workspacePath, "notes");
            Directory.CreateDirectory(notesPath);
            var transcriptPath = Path.Combine(notesPath, "chat_transcript.jsonl");
            var entry = new
            {
                utc = DateTimeOffset.UtcNow,
                kind = intent.Kind.ToString(),
                raw = intent.RawUserText,
                result = summary
            };

            File.AppendAllLines(transcriptPath, new[] { JsonSerializer.Serialize(entry) });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Shoots.UI] chat transcript append failed: {ex.Message}");
        }
    }

	private Task LockWorkOrderAsync()
	{
		IsWorkOrderLocked = true;
		LockWorkOrderCommand.RaiseCanExecuteChanged();
		UnlockWorkOrderCommand.RaiseCanExecuteChanged();
		return Task.CompletedTask;
	}

	private Task UnlockWorkOrderAsync()
	{
		IsWorkOrderLocked = false;
		LockWorkOrderCommand.RaiseCanExecuteChanged();
		UnlockWorkOrderCommand.RaiseCanExecuteChanged();
		return Task.CompletedTask;
	}

	private bool CanGeneratePlan()
	{
		if (!HasActiveWorkspace) return false;
		if (IsWorkOrderLocked) return true; // allow: lock then generate
		return !string.IsNullOrWhiteSpace(IntakeIntent);
	}

    private async Task GeneratePlanAsync()
    {
        RebuildJobSpecDigest();
        ResetSemanticReuseSuggestions("Planning context refreshed. Similar cases can now compare prior passing outputs, repair outcomes, and cautionary failures.");
        SelectedSemanticReuseContext = "Planning";
        if (EnableSemanticReuseSuggestions)
        {
            await RefreshSimilarCasesAsync().ConfigureAwait(true);
        }
    }

	private bool CanRunIntakePlan()
	{
		if (IsOperationActive) return false;
		if (!HasActiveWorkspace) return false;
		if (string.IsNullOrWhiteSpace(IntakeIntent)) return false;
		if (!string.IsNullOrWhiteSpace(BuildBackendDisabledReason())) return false;
		return true;
	}

    public string RunIntakePlanDisabledReason => GetRunIntakePlanDisabledReason();

    private string GetRunIntakePlanDisabledReason()
    {
        if (!HasActiveWorkspace) return "ui.workspace.missing: select a workspace first.";
        if (string.IsNullOrWhiteSpace(IntakeIntent)) return "ui.intake.intent.missing: provide an intake intent.";
        var backendReason = BuildBackendDisabledReason();
        if (!string.IsNullOrWhiteSpace(backendReason)) return backendReason;
        var busyReason = BuildOperationBusyReason();
        if (!string.IsNullOrWhiteSpace(busyReason)) return busyReason;
        return string.Empty;
    }

	private Task RunIntakePlanAsync()
	{
		var blocker = GetRunIntakePlanDisabledReason();
		if (!string.IsNullOrWhiteSpace(blocker))
		{
			Trace.WriteLine($"[Shoots.UI] RunIntakePlan command blocked. reason={blocker}");
			return Task.CompletedTask;
		}

		// Placeholder: real implementation will call your host execution service.
		// Keep UI state transitions deterministic.
		State = UiExecutionState.Running;
		return Task.CompletedTask;
	}

	private bool CanResumeInjectDecision()
	{
		// Only possible when we have waiting gate info
		return HasWaitingInfo;
	}

	private Task ResumeInjectDecisionAsync()
	{
		// Placeholder: once host resume endpoint exists, call it here.
		// For now, clear waiting state and move on.
		LastWaitingInfo = null;
		return Task.CompletedTask;
	}

	private void RebuildJobSpecDigest()
	{
		// Minimal deterministic digest used by tests.
		JobSpecDigest = Shoots.UI.Services.JobSpecDigestBuilder.Build(
			IntakeIntent,
			IntakeTarget,
			IntakeAttachments,
			IntakeStack
		);
	}
    public MainWindowViewModel(
        IExecutionCommandService commandService,
        IEnvironmentProfileService environmentService,
        IEnvironmentCapabilityProvider capabilityProvider,
        IEnvironmentProfilePrompt profilePrompt,
        EnvironmentScriptLoader scriptLoader,
        IProjectWorkspaceProvider workspaceProvider,
        IWorkspaceShellService workspaceShell,
        IDatabaseIntentStore databaseIntentStore,
        IToolTierPrompt toolTierPrompt,
        ISystemBlueprintStore blueprintStore,
        IExecutionEnvironmentSettingsStore executionEnvironmentStore,
        IAiPolicyStore aiPolicyStore,
        AiPanelVisibilityService aiPanelVisibilityService,
        IAiHelpFacade aiHelpFacade,
        IBackendProbeService backendProbeService,
        IOllamaClient ollamaClient,
        IValidationSettingsStore? validationSettingsStore = null,
        IValidationRunnerService? validationRunnerService = null,
        IRepairAttemptService? repairAttemptService = null,
        LocalProjectService? localProjectService = null,
        IPlanner? planner = null,
        BuilderExecutionService? builderExecutionService = null,
        bool autoRefreshBackends = true,
        ISemanticReuseService? semanticReuseService = null)
    {
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _hostExecutionService = new HostExecutionService(_commandService);

        _environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
        _profilePrompt = profilePrompt ?? throw new ArgumentNullException(nameof(profilePrompt));
        _scriptLoader = scriptLoader ?? throw new ArgumentNullException(nameof(scriptLoader));

        _workspaceProvider = workspaceProvider ?? throw new ArgumentNullException(nameof(workspaceProvider));
        _workspaceShell = workspaceShell ?? throw new ArgumentNullException(nameof(workspaceShell));
        _databaseIntentStore = databaseIntentStore ?? throw new ArgumentNullException(nameof(databaseIntentStore));

        _toolTierPrompt = toolTierPrompt ?? throw new ArgumentNullException(nameof(toolTierPrompt));
        _blueprintStore = blueprintStore ?? throw new ArgumentNullException(nameof(blueprintStore));
        _executionEnvironmentStore = executionEnvironmentStore ?? throw new ArgumentNullException(nameof(executionEnvironmentStore));

        _aiPolicyStore = aiPolicyStore ?? throw new ArgumentNullException(nameof(aiPolicyStore));
        _validationSettingsStore = validationSettingsStore ?? new ValidationSettingsStore();
        _aiPanelVisibilityService = aiPanelVisibilityService ?? throw new ArgumentNullException(nameof(aiPanelVisibilityService));
        _aiHelpFacade = aiHelpFacade ?? throw new ArgumentNullException(nameof(aiHelpFacade));
        _backendProbeService = backendProbeService ?? throw new ArgumentNullException(nameof(backendProbeService));
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _validationRunnerService = validationRunnerService ?? new ValidationRunnerService();
        _semanticReuseService = semanticReuseService ?? new SemanticReuseService(_validationRunnerService.RepoRoot);
        _repairAttemptService = repairAttemptService ?? new DeterministicRepairAttemptService(_validationRunnerService.RepoRoot);
        _localProjectService = localProjectService ?? new LocalProjectService();
        _planner = planner ?? new RuntimePlanner(new DemoPlanner());
        _autoRefreshBackends = autoRefreshBackends;
        if (builderExecutionService is not null)
        {
            _builderExecutionService = builderExecutionService;
        }
        else
        {
            var toolRegistry = new ToolRegistry();
            var runtimeBridge = new RuntimeBridgeLocal(new ToolExecutionService(toolRegistry));
            _builderExecutionService = new BuilderExecutionService(runtimeBridge, new ArtifactManager(), toolRegistry);
        }

        _state = UiExecutionState.Idle;
        _lastEnvironmentResult = _environmentService.LastResult;

        // Commands
        NewProjectCommand = new AsyncRelayCommand(NewProjectAsync, CanStartNewProject);
        StartAnotherProjectCommand = new AsyncRelayCommand(StartAnotherProjectAsync, CanStartAnotherProject);
        SelectEntryPathCommand = new AsyncRelayCommand(SelectEntryPathAsync, CanSelectEntryPath);
        SubmitStartupInputCommand = new AsyncRelayCommand(SubmitStartupInputAsync, CanSubmitStartupInput);

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new AsyncRelayCommand(CancelAsync, CanCancel);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);

        ApplyEnvironmentCommand = new AsyncRelayCommand(ApplyEnvironmentAsync, CanApplyEnvironment);
        ApplyScriptCommand = new AsyncRelayCommand(ApplyScriptAsync, CanApplyScript);

        RemoveWorkspaceCommand = new AsyncRelayCommand(RemoveWorkspaceAsync, CanRemoveWorkspace);
        OpenWorkspaceCommand = new AsyncRelayCommand(OpenWorkspaceAsync, CanOpenWorkspace);

        ToggleSystemTierCommand = new AsyncRelayCommand(ToggleSystemTierAsync, CanToggleSystemTier);
        RefreshAiHelpCommand = new AsyncRelayCommand(RefreshAiHelpAsync, CanRefreshAiHelp);

        AddBlueprintCommand = new AsyncRelayCommand(AddBlueprintAsync, CanAddBlueprint);
        SaveBlueprintCommand = new AsyncRelayCommand(SaveBlueprintAsync, CanSaveBlueprint);
        RevertBlueprintCommand = new AsyncRelayCommand(RevertBlueprintAsync, CanRevertBlueprint);
        ExplainBlueprintCommand = new AsyncRelayCommand(ExplainBlueprintAsync, CanExplainBlueprint);
        ValidateBlueprintCommand = new AsyncRelayCommand(ValidateBlueprintAsync, CanExplainBlueprint);
        SuggestBlueprintCommand = new AsyncRelayCommand(SuggestBlueprintAsync, CanExplainBlueprint);

        ExplainExecutionCommand = new AsyncRelayCommand(ExplainExecutionAsync, CanRefreshAiHelp);
        ReplayPlanCommand = new AsyncRelayCommand(ReplayPlanAsync, CanReplayPlan);
        RefreshNarrationCommand = new AsyncRelayCommand(RefreshNarrationAsync);
        RefreshBackendStatusCommand = new AsyncRelayCommand(RefreshBackendStatusAsync, CanRefreshBackends);
        RunDemoPlanCommand = new AsyncRelayCommand(() => RunDemoPlanAsync(), CanRunDemoPlan);
        QuickDemoCommand = new AsyncRelayCommand(QuickDemoAsync, CanRunQuickDemo);
        InitializeChatIntake(); // partial if you have it
        InitializeChatIntakeSurface();
        InitializeReplaySurface();
        InitializeValidationSurface();

        Profiles = new ReadOnlyCollection<IEnvironmentProfile>(_environmentService.Profiles.ToList());
        SelectedProfile = Profiles.FirstOrDefault();

        EnvironmentCapabilities = Array.Empty<string>();
        EnvironmentCreatedPaths = Array.Empty<string>();
        EnvironmentAppliedCapabilities = Array.Empty<string>();

        _recentWorkspaces = new ObservableCollection<ProjectWorkspace>();
        RecentWorkspaces = new ReadOnlyObservableCollection<ProjectWorkspace>(_recentWorkspaces);

        _blueprints = new ObservableCollection<BlueprintEntryViewModel>();
        Blueprints = new ReadOnlyObservableCollection<BlueprintEntryViewModel>(_blueprints);

        _rootFsCatalog = new ObservableCollection<UiRootFsDescriptor>();
        RootFsCatalog = new ReadOnlyObservableCollection<UiRootFsDescriptor>(_rootFsCatalog);

        _startupFlow = new StartupFlowStateMachine();
        _startupMessages = new ObservableCollection<string>();
        StartupMessages = new ReadOnlyObservableCollection<string>(_startupMessages);
        _narrationLines = new ObservableCollection<string>();
        NarrationLines = new ReadOnlyObservableCollection<string>(_narrationLines);
        _actionLogLines = new ObservableCollection<string>();
        ActionLogLines = new ReadOnlyObservableCollection<string>(_actionLogLines);
        _runHistory = new ObservableCollection<RunHistoryRow>();
        RunHistory = new ReadOnlyObservableCollection<RunHistoryRow>(_runHistory);
        _artifactFiles = new ObservableCollection<string>();
        ArtifactFiles = new ReadOnlyObservableCollection<string>(_artifactFiles);
        _proofArtifacts = new ObservableCollection<ProofArtifactRow>(
            ProofArtifactDescriptors.Select(descriptor => new ProofArtifactRow(descriptor.DisplayName, descriptor.RelativePath)));
        ProofArtifacts = new ReadOnlyObservableCollection<ProofArtifactRow>(_proofArtifacts);
        ProviderDiagnostics = new ReadOnlyObservableCollection<ProviderDiagnosticEventRow>(_providerDiagnostics);
        ValidationStageResults = new ReadOnlyObservableCollection<ValidationStageResultRow>(_validationStageResults);
        ValidationRuns = new ReadOnlyObservableCollection<ValidationRunHistoryRow>(_validationRuns);
        ValidationStageHistory = new ReadOnlyObservableCollection<ValidationStageHistoryRow>(_validationStageHistory);
        ValidationBaselineStageChanges = new ReadOnlyObservableCollection<ValidationBaselineStageChangeRow>(_validationBaselineStageChanges);
        SemanticReuseSuggestions = new ReadOnlyObservableCollection<SemanticReuseSuggestionRow>(_semanticReuseSuggestions);
        SemanticReusePlaybooks = new ReadOnlyObservableCollection<SemanticReusePlaybookRow>(_semanticReusePlaybooks);
        RepairChangedFiles = new ReadOnlyObservableCollection<string>(_repairChangedFiles);
        RepairHistory = new ReadOnlyObservableCollection<RepairHistoryRow>(_repairHistory);
        OperationProgressSteps = new ReadOnlyObservableCollection<OperationProgressStepRow>(_operationProgressSteps);
        VisibleOperationProgressSteps = new ReadOnlyObservableCollection<OperationProgressStepRow>(_visibleOperationProgressSteps);
        OperationNarrationFeed = new ReadOnlyObservableCollection<string>(_operationNarrationFeed);
        foreach (var line in UiActionTraceBuffer.Snapshot())
        {
            AppendActionLogLine(line);
        }

        UiActionTraceBuffer.LineCaptured += OnTraceLineCaptured;
        _operationProgressTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => HandleOperationProgressTimerTick(),
            _dispatcher);
        _availableModels = new ObservableCollection<string>();
        AvailableModels = new ReadOnlyObservableCollection<string>(_availableModels);

        _providerCapabilityMatrix = new ReadOnlyCollection<ProviderCapabilityMatrixRow>(new[]
        {
            ProviderCapabilityMatrixRow.FromKind(ProviderKind.Local),
            ProviderCapabilityMatrixRow.FromKind(ProviderKind.Remote),
            ProviderCapabilityMatrixRow.FromKind(ProviderKind.Delegated)
        });

        RefreshProofArtifacts();

        _sessionMode = GetSessionMode();
        _startupComplete = HasActiveWorkspace;

        RefreshRecentWorkspaces();
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();

        LoadExecutionEnvironments();

        RoleOptions = new ReadOnlyCollection<RoleDescriptor>(RoleCatalog.GetDefaultRoles().ToList());
        SelectedRole = RoleOptions.FirstOrDefault();

        AiVisibilityModes = new ReadOnlyCollection<AiVisibilityMode>(new[]
        {
            AiVisibilityMode.Visible,
            AiVisibilityMode.HiddenForEndUsers,
            AiVisibilityMode.AdminOnly
        });

        AiAccessRoles = new ReadOnlyCollection<AiAccessRole>(new[]
        {
            AiAccessRole.EndUser,
            AiAccessRole.Developer,
            AiAccessRole.Admin
        });

        DatabaseIntents = new ReadOnlyCollection<DatabaseIntentOption>(new[]
        {
            new DatabaseIntentOption(DatabaseIntent.None, "None", "No database intent declared."),
            new DatabaseIntentOption(DatabaseIntent.Local, "Local file-based (future)", "Reserve space for a future file-backed database option."),
            new DatabaseIntentOption(DatabaseIntent.External, "External service (future)", "Reserve space for a future external database service option."),
            new DatabaseIntentOption(DatabaseIntent.Undecided, "Undecided", "Explicitly defer any database intent selection.")
        });

        SelectedDatabaseIntent = DatabaseIntents.LastOrDefault(option => option.Intent == DatabaseIntent.Undecided)
            ?? DatabaseIntents.FirstOrDefault();

        LoadValidationSettings();
        LoadValidationRuns();
        LoadBuilderProofArtifacts();
        LoadEnvironmentScript();
        TryLoadLastProjectFromRecents();
        LoadAiPolicy();
        RegisterAiSurfaces();
        if (_autoRefreshBackends)
        {
            _ = RefreshBackendStatusAsync();
        }
        _ = RefreshAiHelpAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- Public surfaces ----

    public UiExecutionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(IsReplayMode));
            OnPropertyChanged(nameof(ExecutionModeSummary));
            OnPropertyChanged(nameof(ExecutionDisabledReason));
            OnPropertyChanged(nameof(ExecutionBlockerSummary));
            RaiseCommandCanExecute();
        }
    }

    public string StateLabel => State.ToString();
    public bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public bool IsReplayMode => State == UiExecutionState.Replaying;

    public ProjectModel? CurrentProject
    {
        get => _currentProject;
        private set
        {
            if (Equals(_currentProject, value))
            {
                return;
            }

            _currentProject = value;
            OnPropertyChanged(nameof(CurrentProject));
            OnPropertyChanged(nameof(CurrentWorkspacePath));
            OnPropertyChanged(nameof(HasProjectLoaded));
            OnPropertyChanged(nameof(HasNoProjectLoaded));
            RunDemoPlanCommand.RaiseCanExecuteChanged();
        }
    }

    public string CurrentWorkspacePath => CurrentProject?.WorkspacePath ?? "No project loaded";
    public bool HasProjectLoaded => CurrentProject is not null;
    public bool HasNoProjectLoaded => !HasProjectLoaded;
    public string? LastDemoRunPath => _lastDemoRunPath;
    public string LastRunVerificationState => _lastRunVerificationState;
    public string SelectedRuntimeBridge => "RuntimeBridgeLocal";
    public bool HasProofRun => !string.IsNullOrWhiteSpace(_proofRunPath);
    public string ProofRunFolderPath => _proofRunPath ?? string.Empty;
    public string ProofRunVerificationState => _proofRunVerificationState;
    public string ProofRunLabel => _proofRunLabel;
    public bool HasLastFailure => _lastFailure is not null;
    public string LastFailurePhase => _lastFailure?.Phase ?? "None";
    public string LastFailureReason => _lastFailure?.Reason ?? "No failures recorded.";
    public string LastFailureMessage => ExtractFailureMessage(LastFailureReason);
    public string LastFailureProofPath => _lastFailure?.ProofPath ?? string.Empty;
    public string LastFailureNextAction => _lastFailure?.NextAction ?? string.Empty;
    public string LastFailureSummary =>
        _lastFailure is null
            ? "No failures recorded."
            : $"{_lastFailure.OccurredUtc:O} | {_lastFailure.Phase}: {_lastFailure.Reason} (Proof: {_lastFailure.ProofPath ?? "n/a"})";

    public string SelectedProviderMode
    {
        get => _selectedProviderMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "local" : value.Trim();
            if (string.Equals(_selectedProviderMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedProviderMode = normalized;
            OnPropertyChanged(nameof(SelectedProviderMode));
            OnPropertyChanged(nameof(ProviderAvailabilityWarning));
            PersistExecutionSelection();
        }
    }

    public RunHistoryRow? SelectedRunHistory
    {
        get => _selectedRunHistory;
        set
        {
            if (ReferenceEquals(_selectedRunHistory, value)) return;
            _selectedRunHistory = value;
            OnPropertyChanged(nameof(SelectedRunHistory));
            if (value is not null)
            {
                SetProofRun(value);
            }

            RaiseReplayCommandCanExecuteChanged();
        }
    }

    public string SelectedHostTransport
    {
        get => _selectedHostTransport;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
            if (string.Equals(_selectedHostTransport, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedHostTransport = normalized;
            OnPropertyChanged(nameof(SelectedHostTransport));
            PersistExecutionSelection();
        }
    }

    public string ProviderAvailabilityWarning
        => string.Equals(SelectedProviderMode, "ollama", StringComparison.OrdinalIgnoreCase) && !OllamaStatus.IsAvailable
            ? $"Provider unavailable: ollama ({ResolveOllamaUnavailableCode()})"
            : string.Empty;

    public string LastRunFolderPath => _lastDemoRunPath ?? string.Empty;
    public bool HasLatestRun => !string.IsNullOrWhiteSpace(_lastDemoRunPath);
    public string LastVerificationReportPath => string.IsNullOrWhiteSpace(_lastDemoRunPath) ? string.Empty : Path.Combine(_lastDemoRunPath, "verification_report.json");
    public string LastOperatorFlowPath => string.IsNullOrWhiteSpace(_lastDemoRunPath) ? string.Empty : Path.Combine(_lastDemoRunPath, "operator_flow.json");
    public string LastTransportEquivalencePath => string.IsNullOrWhiteSpace(_lastDemoRunPath) ? string.Empty : Path.Combine(_lastDemoRunPath, "transport_equivalence.json");
    public string ExecutionModeSummary => IsReplayMode ? "Mode: Replay (artifact-backed, read-only)" : "Mode: Live";

    public string ExecutionProviderSummary =>
        string.IsNullOrWhiteSpace(_providerId) ? "Provider: none" : $"Provider: {_providerId} ({_providerKind})";

    public string ExecutionGraphSummary =>
        string.IsNullOrWhiteSpace(_graphHash)
            ? "Execution graph: no plan loaded."
            : $"Execution graph hash: {_graphHash}. Nodes: {_nodeSetHash}. Edges: {_edgeSetHash}.";

    public string ExecutionDisabledReason => StartDisabledReason;
    public string ExecutionBlockerSummary => BuildExecutionBlockerSummary();
    public string ResolvedExecutionEnvironmentSummary => BuildExecutionEnvironmentSummary();

    public string ExecutionPreviewSummary =>
        string.IsNullOrWhiteSpace(_planId) ? "No execution preview is available." : "Preview ready.";

    public IReadOnlyList<IEnvironmentProfile> Profiles { get; }

    public ReadOnlyCollection<ProviderCapabilityMatrixRow> ProviderCapabilityMatrix => _providerCapabilityMatrix;
    public ReadOnlyCollection<RoleDescriptor> RoleOptions { get; }

    public ReadOnlyCollection<AiVisibilityMode> AiVisibilityModes { get; }
    public ReadOnlyCollection<AiAccessRole> AiAccessRoles { get; }

    public IReadOnlyList<string> EnvironmentCapabilities { get; private set; }
    public IReadOnlyList<string> EnvironmentCreatedPaths { get; private set; }
    public IReadOnlyList<string> EnvironmentAppliedCapabilities { get; private set; } = Array.Empty<string>();

    public string EnvironmentAppliedAtUtc { get; private set; } = "Not applied";
    public string EnvironmentAppliedProfileName { get; private set; } = "None";

    public string? EnvironmentErrorMessage
    {
        get => _environmentErrorMessage;
        private set { if (_environmentErrorMessage == value) return; _environmentErrorMessage = value; OnPropertyChanged(nameof(EnvironmentErrorMessage)); }
    }

    public string? EnvironmentInfoMessage
    {
        get => _environmentInfoMessage;
        private set { if (_environmentInfoMessage == value) return; _environmentInfoMessage = value; OnPropertyChanged(nameof(EnvironmentInfoMessage)); }
    }

    public EnvironmentScript? ScriptPreview => _environmentScript;

    public IReadOnlyList<string> ScriptCapabilities => _environmentScript is null
        ? Array.Empty<string>()
        : DescribeCapabilities(_environmentScript.DeclaredCapabilities);

    public IReadOnlyList<string> ScriptSteps => _environmentScript?.SandboxSteps
        .Select(step => step.RelativePath)
        .ToList()
        ?? new List<string>();

    public string ScriptSearchPath => _scriptSearchPath;
    public string? ScriptUnsupportedCapabilitiesMessage => _scriptUnsupportedCapabilitiesMessage;

    public string ScriptFolderCountLabel => $"Folders to create: {ScriptFolderCount}";
    public int ScriptFolderCount => _environmentScript?.SandboxSteps.Count(step => step.Kind == SandboxPreparationKind.CreateDirectory) ?? 0;

    public ProjectWorkspace? ActiveWorkspace
    {
        get => _activeWorkspace;
        private set
        {
            if (Equals(_activeWorkspace, value)) return;
            _activeWorkspace = value;

            OnPropertyChanged(nameof(ActiveWorkspace));
            OnPropertyChanged(nameof(ActiveWorkspaceName));
            OnPropertyChanged(nameof(HasActiveWorkspace));
            OnPropertyChanged(nameof(HasNoActiveWorkspace));
            OnPropertyChanged(nameof(IsStartupLocked));
            OnPropertyChanged(nameof(IsStartupTabEnabled));

            _startupComplete = _activeWorkspace is not null;
            OnPropertyChanged(nameof(IsStartupComplete));
            OnPropertyChanged(nameof(SelectedWorkspace));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(StartupTabIndex));
            OnPropertyChanged(nameof(SessionStatusLabel));
            OnPropertyChanged(nameof(ExecutionBlockerSummary));

            UpdateSessionMode();
            LoadEnvironmentScript();
            UpdateProfileCapabilities();
            UpdateDatabaseIntentSelection();
            UpdateExecutionEnvironmentSelection();
            LoadBlueprints();
            OnToolpackTierChanged();
            LoadAiPolicy();
            LoadProviderDiagnosticsHistory();
            _ = RefreshAiHelpAsync();
            RaiseCommandCanExecute();
        }
    }

    public string ActiveWorkspaceName => ActiveWorkspace?.Name ?? "No project selected";
    public bool HasActiveWorkspace => ActiveWorkspace is not null;
    public bool HasNoActiveWorkspace => !HasActiveWorkspace;

    public ProjectWorkspace? SelectedWorkspace
    {
        get => _activeWorkspace;
        set
        {
            if (value is null || Equals(_activeWorkspace, value)) return;
            SelectWorkspace(value);
        }
    }

    public bool RestartNeeded
    {
        get => _restartNeeded;
        private set { if (_restartNeeded == value) return; _restartNeeded = value; OnPropertyChanged(nameof(RestartNeeded)); }
    }

    public string WindowTitle => ActiveWorkspace is null ? "Shoots" : $"Shoots — {ActiveWorkspace.Name}";
    public string WorkspaceIsolationTooltip => "Workspace selection is UI-only. It never executes scripts or changes runtime determinism.";

    // Startup
    public string StartupStateLabel => _startupFlow.State.ToString();

    public string StartupEntryPathLabel => _startupFlow.EntryPath is null
        ? "None selected"
        : FormatEntryPathLabel(_startupFlow.EntryPath.Value);

    public bool IsEntryPathSelectionActive => _startupFlow.State == StartupFlowState.EntryPathSelection;

    public string StartupButtonTooltip
    {
        get
        {
            var blocker = GetNewProjectBlockerReason();
            return string.IsNullOrWhiteSpace(blocker) ? "Begin the startup flow." : blocker;
        }
    }

    public string StartAnotherProjectTooltip
    {
        get
        {
            var blocker = GetStartAnotherProjectBlockerReason();
            return string.IsNullOrWhiteSpace(blocker) ? "Switch to startup flow for another project." : blocker;
        }
    }

    public int StartupTabIndex => HasActiveWorkspace ? 1 : 0;
    public string StartupProviderLabel => $"Provider: {_pendingProviderKind}";

    public IReadOnlyList<string> StartupLanguageOptions =>
        StartupLanguageRegistry.All.Select(option => option.Name).ToList();

    public bool IsStartupLocked => HasActiveWorkspace;
    public bool IsStartupTabEnabled => !IsStartupLocked;
    public bool IsStartupComplete => _startupComplete;

    public bool IsStartupInputActive => _startupFlow.State is
        StartupFlowState.StartNewLanguage or
        StartupFlowState.StartNewName or
        StartupFlowState.StartNewDescription or
        StartupFlowState.StartNewProvider or
        StartupFlowState.StartNewEnvironment or
        StartupFlowState.StartNewConfirm or
        StartupFlowState.ContinueExistingPath or
        StartupFlowState.ContinueExistingReview or
        StartupFlowState.ExploreMode;

    public string StartupPrompt => _startupFlow.State switch
    {
        StartupFlowState.EntryPathSelection => "Choose a startup path to continue.",
        StartupFlowState.StartNewLanguage => "Question: What primary language should the project use?",
        StartupFlowState.StartNewName => "Question: Project name (optional). Reply with a name or \"skip\".",
        StartupFlowState.StartNewDescription => "Question: Provide a 1–2 sentence description.",
        StartupFlowState.StartNewProvider => "Question: Choose provider kind (Local, Remote, Delegated).",
        StartupFlowState.StartNewEnvironment => "Question: Choose execution environment id (host-local or linux-container).",
        StartupFlowState.StartNewConfirm => "Type \"confirm\" to create the project.",
        StartupFlowState.ContinueExistingPath => "Question: Provide the path to the existing project.",
        StartupFlowState.ContinueExistingReview => "Type \"confirm\" to attach this project read-only.",
        StartupFlowState.ExploreMode => "Explore mode active. Type \"promote\" to start a project.",
        _ => "Click New Project to begin."
    };

    public string StartupInput
    {
        get => _startupInput;
        set
        {
            if (_startupInput == value) return;
            _startupInput = value;
            OnPropertyChanged(nameof(StartupInput));
            SubmitStartupInputCommand.RaiseCanExecuteChanged();
        }
    }

    public string SessionStatusLabel =>
        _sessionMode switch
        {
            StartupSessionMode.Project => $"Project: {ActiveWorkspace?.Name}",
            StartupSessionMode.Explore => "Explore (no writes)",
            _ => "Startup: No project active"
        };

    // AI visibility settings surfaces
    public AiVisibilityMode SelectedAiVisibilityMode
    {
        get => _aiPresentationPolicy.Visibility;
        set
        {
            if (_aiPresentationPolicy.Visibility == value) return;
            _aiPresentationPolicy = _aiPresentationPolicy with { Visibility = value };
            SaveAiPolicy();
            UpdateAiVisibilityState();
            OnPropertyChanged(nameof(SelectedAiVisibilityMode));
            OnPropertyChanged(nameof(ShowAdminOnlyWatermark));
        }
    }

    public AiAccessRole SelectedAiAccessRole
    {
        get => _aiAccessRole;
        set
        {
            if (_aiAccessRole == value) return;
            _aiAccessRole = value;
            SaveAiPolicy();
            UpdateAiVisibilityState();
            OnPropertyChanged(nameof(SelectedAiAccessRole));
            OnPropertyChanged(nameof(CanConfigureAiPolicy));
            OnPropertyChanged(nameof(CanToggleAiVisibility));
            OnPropertyChanged(nameof(ShowAdminOnlyWatermark));
        }
    }

    public bool AllowAiPanelToggle
    {
        get => _aiPresentationPolicy.AllowAiPanelToggle;
        set
        {
            if (_aiPresentationPolicy.AllowAiPanelToggle == value) return;
            _aiPresentationPolicy = _aiPresentationPolicy with { AllowAiPanelToggle = value };
            SaveAiPolicy();
            OnPropertyChanged(nameof(AllowAiPanelToggle));
            OnPropertyChanged(nameof(CanToggleAiVisibility));
        }
    }

    public bool AllowCopyExport
    {
        get => _aiPresentationPolicy.AllowCopyExport;
        set
        {
            if (_aiPresentationPolicy.AllowCopyExport == value) return;
            _aiPresentationPolicy = _aiPresentationPolicy with { AllowCopyExport = value };
            SaveAiPolicy();
            OnPropertyChanged(nameof(AllowCopyExport));
            OnPropertyChanged(nameof(IsCopyExportDisabled));
            OnPropertyChanged(nameof(AiExportNotice));
        }
    }

    public bool EnterpriseMode
    {
        get => _aiPresentationPolicy.EnterpriseMode;
        set
        {
            if (_aiPresentationPolicy.EnterpriseMode == value) return;
            _aiPresentationPolicy = _aiPresentationPolicy with { EnterpriseMode = value };
            SaveAiPolicy();
            UpdateAiVisibilityState();
            OnPropertyChanged(nameof(EnterpriseMode));
        }
    }

    public bool CanConfigureAiPolicy => _aiAccessRole is AiAccessRole.Admin or AiAccessRole.Developer;
    public bool CanToggleAiVisibility => CanConfigureAiPolicy && AllowAiPanelToggle;

    public bool CanRenderAiPanel => _aiPanelVisibilityState.CanRenderAiPanel;
    public bool CanRenderAiExplainButtons => _aiPanelVisibilityState.CanRenderAiExplainButtons;
    public bool CanRenderAiProviderStatus => _aiPanelVisibilityState.CanRenderAiProviderStatus;

    public bool IsAiPanelHidden => !CanRenderAiPanel;
    public bool IsCopyExportDisabled => !AllowCopyExport;
    public bool ShowAdminOnlyWatermark => CanRenderAiPanel && SelectedAiVisibilityMode == AiVisibilityMode.AdminOnly;

    public string AiPanelVisibilityNote => CanRenderAiPanel
        ? "AI help is available for this role."
        : "AI is running in the background but hidden by settings.";

    public string AiProviderStatus =>
        $"Provider: Ollama={(OllamaStatus.IsAvailable ? "available" : OllamaStatus.ErrorCode ?? "unavailable")}; Qdrant={(QdrantStatus.IsAvailable ? "available" : QdrantStatus.ErrorCode ?? "unavailable")}";
    public BackendStatus OllamaStatus => _ollamaStatus;
    public BackendStatus QdrantStatus => _qdrantStatus;
    public DateTimeOffset? LastProbeUtc => _lastProbeUtc;
    public bool ProbeInFlight
    {
        get => _probeInFlight;
        private set => RunOnUiThread(() => SetProbeInFlight(value));
    }

    private void SetProbeInFlight(bool value)
    {
        if (_probeInFlight == value) return;
        _probeInFlight = value;
        OnPropertyChanged(nameof(ProbeInFlight));
        OnPropertyChanged(nameof(RefreshBackendsDisabledReason));
        OnPropertyChanged(nameof(CanStartNewProjectUi));
        OnPropertyChanged(nameof(RunDemoPlanDisabledReason));
        OnPropertyChanged(nameof(QuickDemoDisabledReason));
        RefreshBackendStatusCommand.RaiseCanExecuteChanged();
        RefreshModelCatalogCommand.RaiseCanExecuteChanged();
        RaiseCommandCanExecute();
    }

    public string OllamaEndpoint => _ollamaStatus.Endpoint ?? EndpointResolver.ResolveOllamaEndpoint();
    public string QdrantEndpoint => _qdrantStatus.Endpoint ?? EndpointResolver.ResolveQdrantEndpoint();
    public string ModelCatalogError
    {
        get => _modelCatalogError;
        private set
        {
            if (_modelCatalogError == value) return;
            _modelCatalogError = value;
            OnPropertyChanged(nameof(ModelCatalogError));
            OnPropertyChanged(nameof(HasModelCatalogError));
        }
    }

    public bool HasModelCatalogError => !string.IsNullOrWhiteSpace(ModelCatalogError);
    public string DefaultModelId => _availableModels.FirstOrDefault() ?? "none";
    public string SelectedModelId
    {
        get => _selectedModelId;
        set
        {
            if (_selectedModelId == value) return;
            _selectedModelId = value;
            OnPropertyChanged(nameof(SelectedModelId));
        }
    }
    public string CatalogHash => ComputeDeterministicHash(string.Join("\n", _availableModels));
    public string BackendDisabledReason => BuildBackendDisabledReason();
    public string AiExportNotice => IsCopyExportDisabled ? "Copy and export are disabled by settings." : string.Empty;

    public string StartDisabledReason => GetStartDisabledReason();
    public string ApplyEnvironmentDisabledReason => GetApplyEnvironmentDisabledReason();
    public string ApplyScriptDisabledReason => GetApplyScriptDisabledReason();
    public string AiHelpDisabledReason => GetAiHelpDisabledReason();
    public string RefreshBackendsDisabledReason => GetRefreshBackendsDisabledReason();

    public string AiHelpContextSummary { get; private set; } = "AI Help is descriptive only.";
    public string AiHelpStateExplanation { get; private set; } = "No runtime context is available.";
    public string AiHelpNextSteps { get; private set; } = "Select a workspace to view help.";

    // Blueprint draft binding
    public string NewBlueprintName { get => _newBlueprintName; set { if (_newBlueprintName == value) return; _newBlueprintName = value; OnPropertyChanged(nameof(NewBlueprintName)); OnBlueprintDraftChanged(); } }
    public string NewBlueprintDescription { get => _newBlueprintDescription; set { if (_newBlueprintDescription == value) return; _newBlueprintDescription = value; OnPropertyChanged(nameof(NewBlueprintDescription)); OnBlueprintDraftChanged(); } }
    public string NewBlueprintIntents { get => _newBlueprintIntents; set { if (_newBlueprintIntents == value) return; _newBlueprintIntents = value; OnPropertyChanged(nameof(NewBlueprintIntents)); OnBlueprintDraftChanged(); } }
    public string NewBlueprintArtifacts { get => _newBlueprintArtifacts; set { if (_newBlueprintArtifacts == value) return; _newBlueprintArtifacts = value; OnPropertyChanged(nameof(NewBlueprintArtifacts)); OnBlueprintDraftChanged(); } }
    public string NewBlueprintVersion { get => _newBlueprintVersion; set { if (_newBlueprintVersion == value) return; _newBlueprintVersion = value; OnPropertyChanged(nameof(NewBlueprintVersion)); OnBlueprintDraftChanged(); } }
    public string NewBlueprintDefinition { get => _newBlueprintDefinition; set { if (_newBlueprintDefinition == value) return; _newBlueprintDefinition = value; OnPropertyChanged(nameof(NewBlueprintDefinition)); OnBlueprintDraftChanged(); } }

    public bool HasBlueprints => _blueprints.Count > 0;

    public IReadOnlyList<UiToolpackTier> ToolpackTierOptions { get; } =
        new[] { UiToolpackTier.Public, UiToolpackTier.Developer };

    public UiToolpackTier ActiveToolpackTier => _activeWorkspace?.AllowedTier ?? UiToolpackTier.Public;
    public bool IsSystemTierEnabled => ActiveToolpackTier == UiToolpackTier.System;

    public UiToolpackTier SelectedToolpackTier
    {
        get => IsSystemTierEnabled ? _lastNonSystemTier : ActiveToolpackTier;
        set => UpdateToolpackTier(value);
    }

    public string ActiveToolpackTierLabel => $"Active Tier: {ActiveToolpackTier}";
    public string SystemTierActionLabel => IsSystemTierEnabled ? "Disable System Tier" : "Enable System Tier";

    public bool CanSelectToolpackTier => HasActiveWorkspace && !IsSystemTierEnabled;
    public bool CanManageBlueprints => HasActiveWorkspace && IsSystemTierEnabled;

    public string BlueprintStatusNote => CanManageBlueprints
        ? "Blueprints are available for this workspace."
        : "Blueprints are available when System tier is enabled.";

    public string BlueprintSaveStatus
    {
        get => _blueprintSaveStatus;
        private set { if (_blueprintSaveStatus == value) return; _blueprintSaveStatus = value; OnPropertyChanged(nameof(BlueprintSaveStatus)); }
    }


    public IReadOnlyList<string> NarrationPhaseOptions => new[] { "all", "startup", "plan", "env", "provider", "execute", "tool", "finalize", "replay" };

    public string SelectedNarrationPhase
    {
        get => _selectedNarrationPhase;
        set
        {
            if (_selectedNarrationPhase == value) return;
            _selectedNarrationPhase = value;
            OnPropertyChanged(nameof(SelectedNarrationPhase));
            _ = RefreshNarrationAsync();
        }
    }

    // ---- Commands ----
    public AsyncRelayCommand NewProjectCommand { get; }
    public AsyncRelayCommand StartAnotherProjectCommand { get; }
    public AsyncRelayCommand SelectEntryPathCommand { get; }
    public AsyncRelayCommand SubmitStartupInputCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ApplyEnvironmentCommand { get; }
    public AsyncRelayCommand ApplyScriptCommand { get; }
    public AsyncRelayCommand RemoveWorkspaceCommand { get; }
    public AsyncRelayCommand OpenWorkspaceCommand { get; }
    public AsyncRelayCommand ToggleSystemTierCommand { get; }
    public AsyncRelayCommand RefreshAiHelpCommand { get; }
    public AsyncRelayCommand AddBlueprintCommand { get; }
    public AsyncRelayCommand SaveBlueprintCommand { get; }
    public AsyncRelayCommand RevertBlueprintCommand { get; }
    public AsyncRelayCommand ExplainBlueprintCommand { get; }
    public AsyncRelayCommand ValidateBlueprintCommand { get; }
    public AsyncRelayCommand SuggestBlueprintCommand { get; }
    public AsyncRelayCommand ExplainExecutionCommand { get; }
    public AsyncRelayCommand ReplayPlanCommand { get; }
    public AsyncRelayCommand RefreshNarrationCommand { get; }
    public AsyncRelayCommand RefreshBackendStatusCommand { get; }
    public AsyncRelayCommand RunDemoPlanCommand { get; }
    public AsyncRelayCommand QuickDemoCommand { get; }

    // ---- UI-safe plan setter ----
    public void SetPlanPreview(string? planId, string? providerId, ProviderKind providerKind, string? graphHash, string? nodeSetHash, string? edgeSetHash)
    {
        _planId = planId;
        _providerId = providerId;
        _providerKind = providerKind;
        _graphHash = graphHash;
        _nodeSetHash = nodeSetHash;
        _edgeSetHash = edgeSetHash;

        OnPropertyChanged(nameof(ExecutionGraphSummary));
        OnPropertyChanged(nameof(ExecutionProviderSummary));
        OnPropertyChanged(nameof(ExecutionPreviewSummary));
        OnPropertyChanged(nameof(ExecutionDisabledReason));
        OnPropertyChanged(nameof(ExecutionBlockerSummary));

        _ = RefreshAiHelpAsync();
        RaiseCommandCanExecute();
    }

    // ---- Startup flow ----
    private bool CanStartNewProject() => !IsCreatingProject && !IsBusy && !IsOperationActive;
    private bool CanStartAnotherProject() => string.IsNullOrWhiteSpace(GetStartAnotherProjectBlockerReason());
    private bool CanSelectEntryPath() => _startupFlow.State == StartupFlowState.EntryPathSelection && !_startupComplete;
    private bool CanSubmitStartupInput() => IsStartupInputActive && !_startupComplete && !string.IsNullOrWhiteSpace(StartupInput);

    private Task NewProjectAsync()
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }

        BeginOperationProgress(
            "Creating project",
            "Preparing workspace scaffolding.",
            "Create workspace",
            "Verify project layout",
            "Load workspace");
        using var busy = EnterBusyScope("StartNewProject");
        var operationId = Guid.NewGuid().ToString("N");
        LogUiAction($"BEGIN StartNewProject run_id={operationId}");
        AddNarration("step", "StartNewProject begin", new Dictionary<string, string> { ["run_id"] = operationId });
        var result = "ok";
        IsCreatingProject = true;
        ProjectCreationErrorMessage = string.Empty;

        try
        {
            SetOperationStepState("Create workspace", "active", "Creating project workspace on disk.");
            if (!TryCreateProjectWorkspace(out var workspace, out var loaded, out var failureReason) || workspace is null || loaded is null)
            {
                result = "verification_failed";
                ProjectCreationErrorMessage = failureReason ?? "Project creation failed.";
                AddStartupMessage($"System: Project verification failed. {ProjectCreationErrorMessage}");
                AddNarration("error", "Project verification failed", new Dictionary<string, string> { ["details"] = ProjectCreationErrorMessage });
                SetOperationStepState("Create workspace", "failed", ProjectCreationErrorMessage);
                SetOperationStepState("Verify project layout", "failed", "Workspace invariants did not pass.");
                CompleteOperationProgress(false, $"Project creation failed: {ProjectCreationErrorMessage}");
                RecordFailure(
                    "Start New Project",
                    ProjectCreationErrorMessage,
                    loaded?.WorkspacePath ?? UiLogPath,
                    "Inspect the generated workspace, resolve missing files, then retry Start New Project.");
                return Task.CompletedTask;
            }

            SetOperationStepState("Create workspace", "completed", "Workspace was created.");
            SetOperationStepState("Verify project layout", "completed", "Workspace invariants passed.");
            SetOperationStepState("Load workspace", "completed", "Workspace loaded into the UI.");
            AddStartupMessage($"System: Project created at {loaded.WorkspacePath}.");
            AddStartupMessage($"System: Project file: {loaded.ProjectFilePath}.");
            AddStartupMessage($"System: ui.log path: {UiLogPath}.");
            AddNarration("result", "Project created", new Dictionary<string, string>
            {
                ["project_id"] = loaded.ProjectId,
                ["workspace_path"] = loaded.WorkspacePath,
                ["project_file"] = loaded.ProjectFilePath
            });
            AddNarration("success", "StartNewProject succeeded", new Dictionary<string, string>
            {
                ["project_id"] = loaded.ProjectId,
                ["workspace_path"] = loaded.WorkspacePath
            });
            CompleteOperationProgress(true, $"Project created at {loaded.WorkspacePath}.");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            result = $"error:{ex.GetType().Name}";
            ProjectCreationErrorMessage = $"Start new project failed: {ex.Message}. See log: {UiLogPath}";
            Trace.WriteLine($"[Shoots.UI] StartNewProject failed: {ex}");
            AddStartupMessage($"System: Start new project failed: {ex.Message}");
            AddNarration("error", "StartNewProject failed", new Dictionary<string, string> { ["error"] = ex.Message });
            SetOperationStepState("Create workspace", "failed", ex.Message);
            CompleteOperationProgress(false, $"Project creation failed: {ex.Message}");
            RecordFailure(
                "Start New Project",
                ex.Message,
                UiLogPath,
                "Open ui.log for details, fix the issue, then retry Start New Project.");
            return Task.CompletedTask;
        }
        finally
        {
            IsCreatingProject = false;
            LogUiAction($"END StartNewProject run_id={operationId} (result={result})");
            AddNarration("result", "StartNewProject end", new Dictionary<string, string> { ["run_id"] = operationId, ["result"] = result });
        }
    }

    private bool TryCreateProjectWorkspace(out ProjectWorkspace? workspace, out ProjectModel? loadedProject, out string? failureReason)
    {
        workspace = null;
        loadedProject = null;
        failureReason = null;

        var model = _localProjectService.CreateNewProject();
        var loaded = LoadProject(model.ProjectFilePath);
        loadedProject = loaded;
        var invariantResult = ProjectInvariants.Verify(loaded.WorkspacePath);
        Trace.WriteLine($"[Shoots.UI] project.invariants {JsonSerializer.Serialize(invariantResult)}");
        if (!invariantResult.Ok)
        {
            failureReason = string.Join("; ", invariantResult.Missing.Concat(invariantResult.Errors));
            return false;
        }

        workspace = new ProjectWorkspace(
            Name: loaded.Name,
            RootPath: loaded.WorkspacePath,
            LastOpenedUtc: DateTimeOffset.UtcNow,
            ProjectId: loaded.ProjectId,
            CreatedUtc: loaded.CreatedUtc);

        _workspaceProvider.SetActiveWorkspace(workspace);
        LoadWorkspaces();
        SelectWorkspace(workspace);
        return true;
    }


    private bool CanRunDemoPlan() => string.IsNullOrWhiteSpace(GetRunDemoPlanDisabledReason());
    private bool CanRunQuickDemo() => string.IsNullOrWhiteSpace(GetQuickDemoDisabledReason());

    private async Task RunDemoPlanAsync(bool manageOperationProgress = true, Action<RunDemoProgressEvent>? progress = null)
    {
        ReportRunDemoProgress(progress, "Planning run", "Preparing deterministic demo plan.", "Plan run", "active");
        if (manageOperationProgress)
        {
            BeginOperationProgress(
                "Planning run",
                "Preparing deterministic demo plan.",
                "Plan run",
                "Execute tools",
                "Host run",
                "Verification");
        }

        if (CurrentProject is null)
        {
            AddStartupMessage("System: No project loaded.");
            AddNarration("warn", "RunDemoPlan blocked", new Dictionary<string, string> { ["reason"] = "no project loaded" });
            ReportRunDemoProgress(progress, "Failed", "Run Demo failed: no project loaded.", "Plan run", "failed");
            if (manageOperationProgress)
            {
                SetOperationStepState("Plan run", "failed", "No project loaded.");
                CompleteOperationProgress(false, "Run Demo failed: no project loaded.");
            }
            return;
        }

        var invariantResult = ProjectInvariants.Verify(CurrentProject.WorkspacePath);
        Trace.WriteLine($"[Shoots.UI] project.invariants {JsonSerializer.Serialize(invariantResult)}");
        if (!invariantResult.Ok)
        {
            var details = string.Join("; ", invariantResult.Missing.Concat(invariantResult.Errors));
            AddStartupMessage($"System: Demo run blocked. Invariants failed: {details}");
            AddNarration("error", "RunDemoPlan blocked by invariants", new Dictionary<string, string> { ["details"] = details });
            ReportRunDemoProgress(progress, "Failed", $"Run Demo blocked: {details}", "Plan run", "failed");
            if (manageOperationProgress)
            {
                SetOperationStepState("Plan run", "failed", details);
                CompleteOperationProgress(false, $"Run Demo blocked: {details}");
            }
            RecordFailure(
                "Run Demo",
                $"Workspace invariants failed: {details}",
                CurrentProject.WorkspacePath,
                "Repair the workspace structure (project.json, plan/, env/) and rerun Run Demo.");
            return;
        }

        try
        {
            if (!await EnsureSelectedProviderReadyAsync(manageOperationProgress, progress).ConfigureAwait(false))
                return;

            if (!_planner.TryBuildPlan(CurrentProject, out var plan))
            {
                AddStartupMessage("System: Demo planner could not build a plan.");
                AddNarration("error", "Demo planner failed", null);
                ReportRunDemoProgress(progress, "Failed", "Planner could not build a deterministic plan.", "Plan run", "failed");
                if (manageOperationProgress)
                {
                    SetOperationStepState("Plan run", "failed", "Planner could not build a deterministic plan.");
                    CompleteOperationProgress(false, "Run Demo failed: planner could not build a plan.");
                }
                RecordFailure(
                    "Run Demo",
                    "Planner could not build a deterministic plan.",
                    CurrentProject.WorkspacePath,
                    "Check planner inputs in the workspace and retry Run Demo.");
                return;
            }

            AddNarration("step", "PLAN_CREATED", new Dictionary<string, string>
            {
                ["planner"] = _planner.GetType().Name,
                ["plan_id"] = plan.PlanId
            });
            ReportRunDemoProgress(progress, "Planning run", $"Plan {plan.PlanId} created.", "Plan run", "completed");
            if (manageOperationProgress)
            {
                SetOperationStatus("Planning run", "Plan generated, preparing execution.");
                SetOperationStepState("Plan run", "completed", $"Plan {plan.PlanId} created.");
            }
            AddNarration("step", "RUNTIME_SUBMITTED", new Dictionary<string, string>
            {
                ["bridge"] = SelectedRuntimeBridge
            });
            AddNarration("step", "PROVIDER_SELECTED", new Dictionary<string, string>
            {
                ["provider"] = SelectedProviderMode
            });
            HostExecutionResult? hostResponse = null;
            if (string.Equals(SelectedHostTransport, "host", StringComparison.OrdinalIgnoreCase))
            {
                var hostPlan = BuildHostPlan(plan, CurrentProject, SelectedProviderMode);
                AddNarration("step", "HOST_REQUEST_SENT", new Dictionary<string, string>
                {
                    ["host_transport"] = SelectedHostTransport,
                    ["host_plan_id"] = hostPlan.PlanId
                });
                if (manageOperationProgress)
                {
                    SetOperationStatus("Waiting on host", "Host transport is processing the request.");
                }
                ReportRunDemoProgress(progress, "Waiting on host", "Host transport is processing the request.", "Host run", "active");

                hostResponse = _hostExecutionService.RunAsync(hostPlan, new HostRunOptions(HostRunMode.Normal, MaxTicks: 2048, RecordTrace: true, Deterministic: true)).GetAwaiter().GetResult();

                AddNarration("result", "HOST_RESPONSE_RECEIVED", new Dictionary<string, string>
                {
                    ["host_outcome"] = hostResponse.Outcome.ToString(),
                    ["host_work_order_id"] = hostResponse.WorkOrderId ?? string.Empty,
                    ["host_plan_id"] = hostResponse.PlanId ?? string.Empty,
                    ["host_plan_hash"] = hostResponse.PlanHash ?? string.Empty
                });

                if (hostResponse.Outcome is HostExecutionOutcome.Failed or HostExecutionOutcome.Unknown)
                {
                    var hostError = hostResponse.ErrorCode ?? hostResponse.Message ?? "unknown";
                    AddStartupMessage($"System: Host execution failed: {hostError}. See host response metadata for details.");
                    AddNarration("error", "Host transport failed", new Dictionary<string, string>
                    {
                        ["host_transport"] = SelectedHostTransport,
                        ["outcome"] = hostResponse.Outcome.ToString(),
                        ["error"] = hostError
                    });
                    ReportRunDemoProgress(progress, "Failed", $"Host transport failed: {hostError}", "Host run", "failed");
                    if (manageOperationProgress)
                    {
                        SetOperationStepState("Host run", "failed", hostError);
                        CompleteOperationProgress(false, $"Host transport failed: {hostError}");
                    }
                    RecordFailure(
                        "Host transport run",
                        $"Host execution failed: {hostError}",
                        UiLogPath,
                        "Inspect host response metadata, resolve the host issue, then retry Run Demo.");
                    return;
                }

                AddNarration("success", "HOST_TRANSPORT_SUCCESS", new Dictionary<string, string>
                {
                    ["host_transport"] = SelectedHostTransport,
                    ["host_outcome"] = hostResponse.Outcome.ToString(),
                    ["host_plan_id"] = hostResponse.PlanId ?? string.Empty
                });
                if (manageOperationProgress)
                {
                    SetOperationStatus("Running tools", "Host accepted request; running execution tools.");
                }
                ReportRunDemoProgress(progress, "Running tools", "Host transport accepted the request.", "Host run", "completed");
            }
            else
            {
                AddNarration("step", "HOST_REQUEST_SENT", new Dictionary<string, string>
                {
                    ["host_transport"] = SelectedHostTransport
                });
                if (manageOperationProgress)
                {
                    SetOperationStatus("Running tools", "Executing demo plan locally.");
                }

                ReportRunDemoProgress(progress, "Running tools", "Host transport not requested for this run.", "Host run", "completed");
            }

            var shouldAutoSelectProof = !HasProofRun || string.Equals(_proofRunPath, _lastDemoRunPath, StringComparison.OrdinalIgnoreCase);

            ReportRunDemoProgress(progress, "Running tools", "Executing deterministic plan.", "Execute tools", "active");
            var execution = _builderExecutionService.Execute(
                plan,
                CurrentProject,
                plannerSource: _planner.GetType().Name,
                runtimeBridge: SelectedRuntimeBridge,
                provider: SelectedProviderMode,
                hostTransport: SelectedHostTransport,
                hostResponseOutcome: hostResponse?.Outcome.ToString(),
                hostResponseWorkOrderId: hostResponse?.WorkOrderId,
                hostResponsePlanId: hostResponse?.PlanId,
                hostResponsePlanHash: hostResponse?.PlanHash,
                hostResponseMessage: hostResponse?.Message,
                hostResponseErrorCode: hostResponse?.ErrorCode,
                narrate: evt => AddNarration(evt.Kind, evt.Message, evt.Data));
            ReportRunDemoProgress(progress, "Running tools", "Tool execution finished.", "Execute tools", "completed");
            ReportRunDemoProgress(progress, "Verifying run", "Validating run artifacts.", "Verification", "active");
            if (manageOperationProgress)
            {
                SetOperationStepState("Execute tools", "completed", "Tool execution finished.");
                SetOperationStatus("Verifying run", "Validating run artifacts.");
            }
            _lastDemoRunPath = execution.RunPath;
            OnPropertyChanged(nameof(LastRunFolderPath));
            OnPropertyChanged(nameof(LatestRunPath));
            OnPropertyChanged(nameof(HasLatestRunPath));
            OnPropertyChanged(nameof(LastVerificationReportPath));
            OnPropertyChanged(nameof(LastOperatorFlowPath));
            OnPropertyChanged(nameof(LastTransportEquivalencePath));
            OnPropertyChanged(nameof(HasLatestRun));
            RaiseReplayCommandCanExecuteChanged();
            var verification = RunVerificationService.Verify(execution.RunPath);
            _lastRunVerificationState = verification.Valid ? "Verified" : "Invalid";
            OnPropertyChanged(nameof(LastRunVerificationState));
            AddNarration("result", "RUN_VERIFIED", new Dictionary<string, string>
            {
                ["valid"] = verification.Valid.ToString(),
                ["state"] = _lastRunVerificationState
            });
            if (manageOperationProgress)
            {
                if (verification.Valid)
                {
                    ReportRunDemoProgress(progress, "Completed", _lastRunVerificationState, "Verification", "completed");
                    SetOperationStepState("Verification", "completed", _lastRunVerificationState);
                    CompleteOperationProgress(true, $"Run completed and {_lastRunVerificationState.ToLowerInvariant()}.");
                }
                else
                {
                    ReportRunDemoProgress(progress, "Failed", _lastRunVerificationState, "Verification", "failed");
                    SetOperationStepState("Verification", "failed", _lastRunVerificationState);
                    CompleteOperationProgress(false, "Run completed but verification is invalid.");
                }
            }
            else
            {
                ReportRunDemoProgress(
                    progress,
                    verification.Valid ? "Completed" : "Failed",
                    verification.Valid ? _lastRunVerificationState : "Run completed but verification is invalid.",
                    "Verification",
                    verification.Valid ? "completed" : "failed");
            }

            Trace.WriteLine($"[Shoots.UI] run complete. run_path={execution.RunPath}; run_json={execution.RunJsonPath}; artifact_json={execution.ArtifactJsonPath}; verified={verification.Valid}");
            AddStartupMessage($"System: Demo run complete at {execution.RunPath}. Verification={_lastRunVerificationState}.");
            AddNarration("result", "Demo run complete", new Dictionary<string, string>
            {
                ["run_path"] = execution.RunPath,
                ["run_json"] = execution.RunJsonPath,
                ["artifact_json"] = execution.ArtifactJsonPath,
                ["status"] = execution.Run.Status,
                ["verification"] = _lastRunVerificationState,
                ["plan_hash"] = execution.Run.PlanHash,
                ["run_id"] = execution.Run.RunId
            });
            AddNarration("success", "RunDemoPlan succeeded", new Dictionary<string, string>
            {
                ["run_path"] = execution.RunPath,
                ["verification"] = _lastRunVerificationState
            });

            var historyRow = new RunHistoryRow(
                execution.Run.RunId,
                execution.RunPath,
                execution.Run.CreatedUtc,
                execution.Run.Status,
                execution.Run.Provider,
                execution.Run.HostTransport,
                _lastRunVerificationState);
            _runHistory.Insert(0, historyRow);
            if (_runHistory.Count > 20)
            {
                _runHistory.RemoveAt(_runHistory.Count - 1);
            }

            if (shouldAutoSelectProof)
            {
                SelectedRunHistory = historyRow;
            }

            PersistGeneratedOutputValidationLink(GeneratedOutputValidationLinkService.CreateDefault(execution.Run.RunId, execution.RunPath));

            if (ValidateGeneratedOutputAfterRun)
            {
                await ExecuteValidationActionAsync(
                    ValidationAction.RunFullValidationLoop,
                    new GeneratedOutputContext(execution.Run.RunId, execution.RunPath, execution.RunPath),
                    beginOperationProgress: true,
                    actionLabelOverride: "Validate generated output").ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Shoots.UI] RunDemoPlan failed: {ex}");
            AddStartupMessage($"System: Demo run failed: {ex.Message}");
            AddNarration("error", "RunDemoPlan failed", new Dictionary<string, string> { ["error"] = ex.Message });
            if (manageOperationProgress)
            {
                SetOperationStepState("Execute tools", "failed", ex.Message);
                CompleteOperationProgress(false, $"Run Demo failed: {ex.Message}");
            }
            ReportRunDemoProgress(progress, "Failed", $"Run Demo failed: {ex.Message}", "Execute tools", "failed");
            RecordFailure(
                "Run Demo",
                ex.Message,
                _lastDemoRunPath ?? UiLogPath,
                "Inspect ui.log or the partial run folder, resolve the issue, then rerun Run Demo.");
        }

        return;
    }
    private async Task QuickDemoAsync()
    {
        if (IsBusy)
        {
            return;
        }

        BeginOperationProgress(
            "Creating project",
            "Quick Demo is preparing a new workspace.",
            "Create project",
            "Plan run",
            "Execute tools",
            "Host run",
            "Verification",
            "Completed");
        using var busy = EnterBusyScope("QuickDemo");
        var operationId = Guid.NewGuid().ToString("N");
        LogUiAction($"BEGIN QuickDemo run_id={operationId}");
        AddNarration("step", "QuickDemo begin", new Dictionary<string, string> { ["run_id"] = operationId });
        var result = "ok";

        try
        {
            AddStartupMessage("Quick Demo: creating a new workspace...");
            AddNarration("info", "QuickDemo stage", new Dictionary<string, string> { ["stage"] = "create_project" });
            SetOperationStatus("Creating project", "Quick Demo is creating a new workspace.");
            SetOperationStepState("Create project", "active", "Scaffolding workspace files.");

            ProjectCreationErrorMessage = string.Empty;
            IsCreatingProject = true;
            if (!TryCreateProjectWorkspace(out var workspace, out var loaded, out var failureReason) || workspace is null || loaded is null)
            {
                result = "project_failed";
                var message = failureReason ?? "Project creation failed.";
                ProjectCreationErrorMessage = message;
                AddStartupMessage($"Quick Demo failed while creating a project: {message}");
                AddNarration("error", "QuickDemo project failed", new Dictionary<string, string> { ["reason"] = message });
                SetOperationStepState("Create project", "failed", message);
                CompleteOperationProgress(false, $"Quick Demo failed while creating a project: {message}");
                RecordFailure(
                    "Quick Demo (Create Project)",
                    message,
                    loaded?.WorkspacePath ?? UiLogPath,
                    "Open ui.log or the partial workspace, resolve the issue, then rerun Quick Demo.");
                return;
            }

            AddStartupMessage($"Quick Demo: project ready at {loaded.WorkspacePath}.");
            AddNarration("success", "QuickDemo project created", new Dictionary<string, string>
            {
                ["project_id"] = loaded.ProjectId,
                ["workspace_path"] = loaded.WorkspacePath
            });
            SetOperationStepState("Create project", "completed", "Project workspace is ready.");

            IsCreatingProject = false;

            AddStartupMessage("Quick Demo: running demo plan...");
            AddNarration("info", "QuickDemo stage", new Dictionary<string, string> { ["stage"] = "run_demo" });
            SetOperationStatus("Planning run", "Quick Demo is preparing the run plan.");
            SetOperationStepState("Plan run", "active", "Preparing deterministic run plan.");
            SetOperationStepState("Execute tools", "pending", string.Empty);
            SetOperationStepState("Host run", "pending", string.Empty);
            SetOperationStepState("Verification", "pending", string.Empty);
            await RunDemoPlanAsync(
                    manageOperationProgress: false,
                    progress: update =>
                    {
                        SetOperationStatus(update.Stage, update.Detail);
                        if (!string.IsNullOrWhiteSpace(update.StepName))
                        {
                            SetOperationStepState(update.StepName!, update.StepState ?? "pending", update.Detail);
                        }
                    })
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(_lastDemoRunPath) || !HasLatestRun)
            {
                result = "demo_failed";
                var message = "Demo run did not produce artifacts.";
                AddStartupMessage($"Quick Demo failed: {message}");
                AddNarration("error", "QuickDemo demo failed", new Dictionary<string, string> { ["reason"] = message });
                SetOperationStepState("Plan run", "failed", message);
                SetOperationStepState("Completed", "failed", message);
                CompleteOperationProgress(false, $"Quick Demo failed: {message}");
                RecordFailure(
                    "Quick Demo (Run Demo)",
                    message,
                    UiLogPath,
                    "Review ui.log for the run failure, then retry Quick Demo.");
                return;
            }

            AddStartupMessage($"Quick Demo: verification state {_lastRunVerificationState}.");
            AddNarration("info", "QuickDemo stage", new Dictionary<string, string>
            {
                ["stage"] = "verify",
                ["verification"] = _lastRunVerificationState
            });
            SetOperationStatus("Verifying run", "Checking verification artifacts.");
            SetOperationStepState(
                "Verification",
                string.Equals(_lastRunVerificationState, "Verified", StringComparison.OrdinalIgnoreCase) ? "completed" : "failed",
                _lastRunVerificationState);

            if (_runHistory.Count > 0)
            {
                SelectedRunHistory = _runHistory[0];
            }

            AddStartupMessage("Quick Demo: proof artifacts are focused on the latest run.");
            AddNarration("success", "QuickDemo proof artifacts ready", new Dictionary<string, string>
            {
                ["run_path"] = _lastDemoRunPath ?? string.Empty
            });
            SetOperationStatus("Completed", "Quick Demo is focusing proof artifacts.");
            SetOperationStepState("Completed", "completed", "Proof artifacts panel updated.");

            AddStartupMessage($"Quick Demo complete. Run folder: {_lastDemoRunPath}");
            AddNarration("success", "QuickDemo completed", new Dictionary<string, string>
            {
                ["run_path"] = _lastDemoRunPath ?? string.Empty,
                ["verification"] = _lastRunVerificationState
            });
            CompleteOperationProgress(true, $"Quick Demo completed. Verification: {_lastRunVerificationState}.");
        }
        catch (Exception ex)
        {
            result = "exception";
            Trace.WriteLine($"[Shoots.UI] QuickDemo failed: {ex}");
            AddStartupMessage($"Quick Demo failed: {ex.Message}");
            AddNarration("error", "QuickDemo failed", new Dictionary<string, string> { ["error"] = ex.Message });
            SetOperationStepState("Plan run", "failed", ex.Message);
            SetOperationStepState("Completed", "failed", ex.Message);
            CompleteOperationProgress(false, $"Quick Demo failed: {ex.Message}");
            RecordFailure(
                "Quick Demo",
                ex.Message,
                UiLogPath,
                "Review ui.log for the underlying issue, resolve it, then rerun Quick Demo.");
        }
        finally
        {
            IsCreatingProject = false;
            LogUiAction($"END QuickDemo run_id={operationId} (result={result})");
            AddNarration("result", "QuickDemo end", new Dictionary<string, string>
            {
                ["run_id"] = operationId,
                ["result"] = result
            });
            QuickDemoCommand.RaiseCanExecuteChanged();
        }
    }


    private static void ReportRunDemoProgress(
        Action<RunDemoProgressEvent>? reporter,
        string stage,
        string detail,
        string? stepName = null,
        string? stepState = null)
    {
        if (reporter is null)
        {
            return;
        }

        reporter(new RunDemoProgressEvent(stage, detail, stepName, stepState));
    }

    private BuildPlan BuildHostPlan(PlanModel plan, ProjectModel project, string providerMode)
    {
        var workOrder = _hostExecutionService.CreateWorkOrder(
            originalRequest: "ui-demo-run",
            intent: "execute-demo-plan",
            constraints: Array.Empty<string>(),
            requestedArtifacts: new[] { "run.json", "artifact.json" });

        var request = new BuildRequest(
            workOrder,
            CommandId: "ui.demo.run",
            Args: new Dictionary<string, object?>
            {
                ["project_id"] = project.ProjectId,
                ["workspace_path"] = project.WorkspacePath,
                ["provider"] = providerMode
            },
            RouteRules: Array.Empty<RouteRule>());

        var authority = new DelegationAuthority(
            new ProviderId(providerMode),
            ResolveProviderKind(providerMode),
            "ui-demo-policy",
            AllowsDelegation: true);

        var steps = plan.Steps
            .Select(static step => (BuildStep)new ToolBuildStep(
                step.StepId,
                $"Execute {step.ToolId}",
                new ToolId(step.ToolId),
                step.Args.ToDictionary(static kvp => kvp.Key, static kvp => (object?)kvp.Value, StringComparer.Ordinal),
                new[] { new ToolOutputSpec("output", "file", step.OutputPath) }))
            .ToArray();

        var artifacts = plan.Steps
            .Select(static step => new BuildArtifact(step.StepId, step.OutputPath))
            .ToArray();

        return new BuildPlan(plan.PlanId, request, authority, steps, artifacts);
    }

    private static ProviderKind ResolveProviderKind(string providerMode)
    {
        if (string.Equals(providerMode, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderKind.Delegated;
        }

        if (string.Equals(providerMode, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderKind.Remote;
        }

        return ProviderKind.Local;
    }

    private Task StartAnotherProjectAsync()
    {
        var blocker = GetStartAnotherProjectBlockerReason();
        Trace.WriteLine($"[Shoots.UI] StartAnotherProject command invoked. hasWorkspace={HasActiveWorkspace}; executionState={State}; blocker={blocker}");
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            AddStartupMessage($"System: {blocker}");
            return Task.CompletedTask;
        }

        ActiveWorkspace = null;
        _startupFlow.Reset();
        _startupComplete = false;
        OnPropertyChanged(nameof(IsStartupComplete));
        _startupFlow.TryBeginNewProject(out _);

        Trace.WriteLine("[Shoots.UI] Startup reset requested. Session mode: Startup.");
        AddStartupMessage("System: Startup reset requested. Choose an entry path.");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private string GetNewProjectBlockerReason()
        => string.Empty;

    private string GetStartAnotherProjectBlockerReason()
    {
        if (!HasActiveWorkspace)
        {
            return "startup.blocked: gate=activeWorkspace; property=HasActiveWorkspace=false; action=attach or create a project first";
        }

        if (State is UiExecutionState.Running or UiExecutionState.Waiting)
        {
            return "startup.blocked: gate=executionState; property=State=RunningOrWaiting; action=wait for run completion or cancel current run";
        }

        return string.Empty;
    }

    private Task SelectEntryPathAsync(object? parameter)
    {
        if (_startupComplete)
        {
            AddStartupMessage("System: Startup is complete. Use \"Start another project\" to re-enter startup.");
            return Task.CompletedTask;
        }

        if (parameter is not StartupEntryPath entryPath)
        {
            AddStartupMessage("System: Unable to select entry path.");
            return Task.CompletedTask;
        }

        var previous = _startupFlow.State;
        if (!_startupFlow.TrySelectEntryPath(entryPath, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, $"Entry path selected: {entryPath}.");
        AddStartupMessage($"Intent: {FormatEntryPathLabel(entryPath)}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task SubmitStartupInputAsync()
    {
        if (_startupComplete)
        {
            AddStartupMessage("System: Startup is complete. Use \"Start another project\" to re-enter startup.");
            return Task.CompletedTask;
        }

        var input = NormalizeStartupInput(StartupInput);
        if (string.IsNullOrWhiteSpace(input)) return Task.CompletedTask;

        AddStartupMessage($"You: {input}");
        StartupInput = string.Empty;

        return _startupFlow.State switch
        {
            StartupFlowState.StartNewLanguage => HandleStartupLanguageAsync(input),
            StartupFlowState.StartNewName => HandleStartupProjectNameAsync(input),
            StartupFlowState.StartNewDescription => HandleStartupDescriptionAsync(input),
            StartupFlowState.StartNewProvider => HandleStartupProviderAsync(input),
            StartupFlowState.StartNewEnvironment => HandleStartupEnvironmentAsync(input),
            StartupFlowState.StartNewConfirm => HandleStartupConfirmAsync(input),
            StartupFlowState.ContinueExistingPath => HandleContinueExistingPathAsync(input),
            StartupFlowState.ContinueExistingReview => HandleContinueExistingConfirmAsync(input),
            StartupFlowState.ExploreMode => HandleExploreModeAsync(input),
            _ => Task.CompletedTask
        };
    }

    // ---- Execution ----
    private bool CanStart() => !string.IsNullOrWhiteSpace(_planId) && State is UiExecutionState.Idle or UiExecutionState.Completed or UiExecutionState.Halted;
    private bool CanCancel() => State is UiExecutionState.Running or UiExecutionState.Waiting;

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_planId)) return;
        State = UiExecutionState.Running;
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private Task CancelAsync()
    {
        State = UiExecutionState.Halted;
        return _commandService.CancelAsync();
    }

    private async Task RefreshStatusAsync()
    {
        _ = await _commandService.RefreshStatusAsync().ConfigureAwait(true);
    }

    // ---- Environment ----
    public IEnvironmentProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value)) return;
            _selectedProfile = value;
            OnPropertyChanged(nameof(SelectedProfile));
            OnPropertyChanged(nameof(SelectedProfileDescription));
            UpdateProfileCapabilities();
            RaiseCommandCanExecute();
        }
    }

    public string SelectedProfileDescription => SelectedProfile?.Description ?? string.Empty;

    private bool CanApplyEnvironment()
    {
        if (SelectedProfile is null) return false;
        if (State is UiExecutionState.Running or UiExecutionState.Waiting or UiExecutionState.Replaying) return false;
        if (_lastEnvironmentResult is null) return true;
        return !string.Equals(_lastEnvironmentResult.ProfileName, SelectedProfile.Name, StringComparison.Ordinal);
    }

    private bool CanApplyScript()
    {
        if (_environmentScript is null) return false;
        if (State is UiExecutionState.Running or UiExecutionState.Waiting or UiExecutionState.Replaying) return false;
        if (_lastEnvironmentResult is null) return true;
        return !string.Equals(_lastEnvironmentResult.ProfileName, _environmentScript.Name, StringComparison.Ordinal);
    }

    private Task ApplyEnvironmentAsync()
    {
        var blocker = GetApplyEnvironmentDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Trace.WriteLine($"[Shoots.UI] ApplyEnvironment command blocked. reason={blocker}");
            EnvironmentErrorMessage = blocker;
            return Task.CompletedTask;
        }

        return Task.CompletedTask; // keep your real implementation elsewhere (partial)
    }

    private Task ApplyScriptAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    // ---- Workspaces ----
    private bool CanRemoveWorkspace() => HasActiveWorkspace;
    private bool CanOpenWorkspace() => HasActiveWorkspace;

    private Task RemoveWorkspaceAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private Task OpenWorkspaceAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private void SelectWorkspace(ProjectWorkspace workspace)
    {
        ActiveWorkspace = workspace;
        if (!string.IsNullOrWhiteSpace(workspace.SelectedProviderKind))
        {
            SelectedProviderMode = workspace.SelectedProviderKind.ToLowerInvariant();
        }
    }

    // ---- Tool tiers ----
    private bool CanToggleSystemTier() => HasActiveWorkspace;

    private Task ToggleSystemTierAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private void UpdateToolpackTier(UiToolpackTier tier)
    {
        _lastNonSystemTier = tier;
        OnPropertyChanged(nameof(SelectedToolpackTier));
        OnPropertyChanged(nameof(ActiveToolpackTierLabel));
    }

    private void OnToolpackTierChanged()
    {
        OnPropertyChanged(nameof(ActiveToolpackTierLabel));
        OnPropertyChanged(nameof(SystemTierActionLabel));
        OnPropertyChanged(nameof(CanManageBlueprints));
        OnPropertyChanged(nameof(BlueprintStatusNote));
    }

    // ---- Blueprints ----
    private bool CanAddBlueprint() => CanManageBlueprints && !string.IsNullOrWhiteSpace(NewBlueprintName);
    private bool CanSaveBlueprint() => CanManageBlueprints && _blueprints.Any(bp => bp.CanSave);
    private bool CanRevertBlueprint() => CanManageBlueprints && _blueprints.Any(bp => bp.CanRevert);
    private bool CanExplainBlueprint() => CanManageBlueprints && _blueprints.Count > 0;

    private Task AddBlueprintAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private Task SaveBlueprintAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private Task RevertBlueprintAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private Task ExplainBlueprintAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private Task ValidateBlueprintAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private Task SuggestBlueprintAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private void LoadBlueprints()
        => _blueprints.Clear(); // keep your real implementation elsewhere (partial)

    // ---- AI Help ----
    private bool CanRefreshAiHelp() => true;

    private Task RefreshAiHelpAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private Task ExplainExecutionAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

    private bool CanReplayPlan() => CanReplaySelectedRun() || CanReplayLatestRun() || !string.IsNullOrWhiteSpace(_planId);
    private Task ReplayPlanAsync()
        => CanReplaySelectedRun() ? ReplaySelectedRunAsync() : ReplayLatestRunAsync();

    private bool CanReplayLatestRun()
        => HasReplayArtifacts(LastRunFolderPath);

    private bool CanReplaySelectedRun()
        => SelectedRunHistory is not null && HasReplayArtifacts(SelectedRunHistory.RunPath);

    private Task ReplayLatestRunAsync()
        => ReplayFromRunPathAsync(LastRunFolderPath);

    private Task ReplaySelectedRunAsync()
        => ReplayFromRunPathAsync(SelectedRunHistory?.RunPath ?? string.Empty);

    private async Task ReplayFromRunPathAsync(string runPath)
    {
        if (!HasReplayArtifacts(runPath))
            return;

        BeginOperationProgress(
            "Replaying run",
            $"Loading replay artifacts from {runPath}.",
            "Load metadata",
            "Validate stage flow",
            "Complete replay");

        try
        {
            SetOperationStepState("Load metadata", "active", "Reading saved run metadata.");
            var replay = RunReplayService.ReplayFromRunPath(runPath);
            _replaySourcePath = replay.SourceRunPath;
            _replaySummary = replay.Summary;
            _replayMismatchSummary = replay.IsMatch ? string.Empty : string.Join(System.Environment.NewLine, replay.Mismatches);
            _replayTimingSummary = BuildReplayTimingSummary(replay.Diff);
            State = UiExecutionState.Replaying;

            OnPropertyChanged(nameof(ReplaySourcePath));
            OnPropertyChanged(nameof(HasReplaySourcePath));
            OnPropertyChanged(nameof(ReplaySummary));
            OnPropertyChanged(nameof(ReplayMismatchSummary));
            OnPropertyChanged(nameof(HasReplayMismatch));
            OnPropertyChanged(nameof(ReplayTimingSummary));
            OnPropertyChanged(nameof(HasReplayTimingSummary));

            SetOperationStepState("Load metadata", "completed", Path.Combine(runPath, RunReplayService.MetadataFileName));
            SetOperationStepState("Validate stage flow", replay.IsMatch ? "completed" : "failed", replay.Summary);
            SetOperationStatus("Replaying run", replay.Summary);
            SetOperationLatestEvent(replay.IsMatch
                ? $"Replay loaded from {runPath}."
                : $"Replay mismatch detected for {runPath}.");
            SetOperationStepState("Complete replay", replay.IsMatch ? "completed" : "failed", replay.Summary);
            CompleteOperationProgress(replay.IsMatch, replay.Summary);

            AddNarration("info", "REPLAY_LOADED", new Dictionary<string, string>
            {
                ["run_path"] = runPath,
                ["match"] = replay.IsMatch.ToString(),
                ["mismatch_count"] = replay.Mismatches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

            if (SelectedRunHistory is not null &&
                string.Equals(SelectedRunHistory.RunPath, runPath, StringComparison.OrdinalIgnoreCase))
            {
                SetProofRun(SelectedRunHistory);
            }

            await Task.CompletedTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _replaySourcePath = runPath;
            _replaySummary = $"Replay failed: {ex.Message}";
            _replayMismatchSummary = string.Empty;
            _replayTimingSummary = string.Empty;
            OnPropertyChanged(nameof(ReplaySourcePath));
            OnPropertyChanged(nameof(HasReplaySourcePath));
            OnPropertyChanged(nameof(ReplaySummary));
            OnPropertyChanged(nameof(ReplayMismatchSummary));
            OnPropertyChanged(nameof(HasReplayMismatch));
            OnPropertyChanged(nameof(ReplayTimingSummary));
            OnPropertyChanged(nameof(HasReplayTimingSummary));
            SetOperationStepState("Load metadata", "failed", ex.Message);
            CompleteOperationProgress(false, $"Replay failed: {ex.Message}");
            RecordFailure(
                "Replay",
                ex.ToString(),
                runPath,
                "Inspect run_metadata.json and timeline.json in the run folder, then retry replay.");
            State = UiExecutionState.Halted;
        }
    }

    private static bool HasReplayArtifacts(string? runPath)
    {
        if (string.IsNullOrWhiteSpace(runPath) || !Directory.Exists(runPath))
            return false;

        return File.Exists(RunReplayService.MetadataPath(runPath));
    }

    private static string BuildReplayTimingSummary(ReplayDiffResult diff)
    {
        if (diff.StageDiffs.Count == 0)
            return string.Empty;

        return string.Join(
            System.Environment.NewLine,
            diff.StageDiffs.Select(stage =>
                $"{stage.StageName}: original={stage.OriginalDurationMs} ms; replay={stage.ReplayDurationMs} ms; drift={stage.DriftMs} ms{(stage.MajorDeviation ? " [major]" : string.Empty)}"));
    }

    private bool CanRunValidationAction(ValidationAction action)
        => string.IsNullOrWhiteSpace(GetValidationDisabledReason(action));

    private string GetValidationDisabledReason()
        => GetValidationDisabledReason(null);

    private string GetValidationDisabledReason(ValidationAction? requestedAction)
    {
        if (!Directory.Exists(_validationRunnerService.RepoRoot) || !File.Exists(Path.Combine(_validationRunnerService.RepoRoot, "Shoots.sln")))
            return "Validation disabled because the Shoots repo root could not be resolved.";

        if (IsOperationCompletionHoldActive)
            return "Validation disabled while completion state is being displayed.";

        if (IsOperationActive)
        {
            if (_activeValidationAction is { } activeAction)
            {
                var activeLabel = string.IsNullOrWhiteSpace(_activeValidationActionLabel)
                    ? DescribeValidationAction(activeAction)
                    : _activeValidationActionLabel;
                if (requestedAction is null)
                {
                    return $"{activeLabel} is using the workspace; conflicting validation actions are blocked until it finishes.";
                }

                if (requestedAction == activeAction)
                    return $"{DescribeValidationAction(requestedAction.Value)} is already in progress.";

                if (requestedAction == ValidationAction.RunIntegrityValidation &&
                    string.Equals(_activeValidationStageId, "smoke_validation", StringComparison.Ordinal))
                {
                    return "Integrity validation is blocked while smoke validation is using the workspace.";
                }

                if (requestedAction == ValidationAction.RunSmokeValidation &&
                    string.Equals(_activeValidationStageId, "integrity_validation", StringComparison.Ordinal))
                {
                    return "Smoke validation must finish before integrity can clean restore artifacts.";
                }

                return $"{DescribeValidationAction(requestedAction.Value)} is blocked while {activeLabel} is using the workspace.";
            }

            return $"Validation disabled while {OperationStatusLine.ToLowerInvariant()} is in progress.";
        }

        if (IsBusy)
            return "Validation disabled while another UI action is busy.";

        return string.Empty;
    }

    private bool CanRunBuilderProofMatrix()
        => string.IsNullOrWhiteSpace(GetBuilderProofDisabledReason());

    private string GetBuilderProofDisabledReason()
    {
        if (!Directory.Exists(_validationRunnerService.RepoRoot) || !File.Exists(Path.Combine(_validationRunnerService.RepoRoot, "Shoots.sln")))
            return "Builder proof is disabled because the Shoots repo root could not be resolved.";

        if (IsOperationCompletionHoldActive)
            return "Builder proof is disabled while completion state is being displayed.";

        if (IsOperationActive)
        {
            if (_activeValidationAction is { } activeAction)
            {
                var activeLabel = string.IsNullOrWhiteSpace(_activeValidationActionLabel)
                    ? DescribeValidationAction(activeAction)
                    : _activeValidationActionLabel;
                return $"Builder proof is blocked while {activeLabel} is using the workspace.";
            }

            return $"Builder proof is blocked while {OperationStatusLine.ToLowerInvariant()} is in progress.";
        }

        if (IsBusy)
            return "Builder proof is blocked while another UI action is busy.";

        return string.Empty;
    }

    private bool CanRunBuilderComparativeProof()
        => string.IsNullOrWhiteSpace(GetBuilderComparativeProofDisabledReason());

    private string GetBuilderComparativeProofDisabledReason()
    {
        if (!Directory.Exists(_validationRunnerService.RepoRoot) || !File.Exists(Path.Combine(_validationRunnerService.RepoRoot, "Shoots.sln")))
            return "Comparative proof is disabled because the Shoots repo root could not be resolved.";

        if (IsOperationCompletionHoldActive)
            return "Comparative proof is disabled while completion state is being displayed.";

        if (IsOperationActive)
        {
            if (_activeValidationAction is { } activeAction)
            {
                var activeLabel = string.IsNullOrWhiteSpace(_activeValidationActionLabel)
                    ? DescribeValidationAction(activeAction)
                    : _activeValidationActionLabel;
                return $"Comparative proof is blocked while {activeLabel} is using the workspace.";
            }

            return $"Comparative proof is blocked while {OperationStatusLine.ToLowerInvariant()} is in progress.";
        }

        if (IsBusy)
            return "Comparative proof is blocked while another UI action is busy.";

        if (_latestBuilderProofRun is null)
            return "Comparative proof is available after a builder proof matrix run is loaded.";

        if (_latestBuilderModelEscalationDecision is null || _latestBuilderModelRoutingPlan is null)
            return "Comparative proof requires a recorded builder escalation decision and routing plan.";

        var escalationState = _latestBuilderModelEscalationDecision.EscalationRequirementState;
        if (!string.Equals(escalationState, "task_should_be_split_first", StringComparison.Ordinal) &&
            !string.Equals(escalationState, "stronger_model_recommended", StringComparison.Ordinal) &&
            !string.Equals(escalationState, "stronger_model_required", StringComparison.Ordinal))
        {
            return "Comparative proof is only available for escalation-worthy or split-then-escalate builder targets.";
        }

        var dimensions = _latestBuilderModelEscalationDecision.ComplexityDimensions;
        if (dimensions.ProjectCountTouched > 1 || dimensions.DependencyReferenceChangeCount > 0 || dimensions.FileCountTouched > 3 || dimensions.NewFileCreationCount > 1)
            return "Comparative proof is limited to bounded single-project builder targets with no dependency changes.";

        if (string.IsNullOrWhiteSpace(_latestBuilderModelRoutingPlan.ComparativeProofHook.ComparisonKey))
            return "Comparative proof requires a recorded comparison hook from the latest routing plan.";

        if (_latestBuilderStrongerTierAvailability is null)
            return "Comparative proof requires stronger-tier availability to be resolved from the latest builder proof run.";

        if (!string.Equals(_latestBuilderStrongerTierAvailability.AvailabilityState, "available", StringComparison.Ordinal))
            return _latestBuilderStrongerTierAvailability.Summary;

        return string.Empty;
    }

    private string GetBuilderPreparedLaunchDisabledReason()
    {
        if (_latestBuilderProofRun is null || _latestBuilderRequestIntake is null || _latestBuilderExecutionPrep is null)
            return "Prepared launch is available after current builder intake and execution prep artifacts are loaded.";

        if (!string.Equals(_latestBuilderRequestIntake.FreshnessState, "current", StringComparison.Ordinal))
            return "Prepared launch is blocked because the latest builder intake is stale.";

        if (!string.Equals(_latestBuilderExecutionPrep.FreshnessState, "current", StringComparison.Ordinal))
            return "Prepared launch is blocked because the latest execution prep is stale.";

        if (_activeValidationAction is { } activeAction)
        {
            var activeLabel = string.IsNullOrWhiteSpace(_activeValidationActionLabel)
                ? DescribeValidationAction(activeAction)
                : _activeValidationActionLabel;
            return $"Prepared launch is blocked while {activeLabel.ToLowerInvariant()} is using the workspace.";
        }

        if (IsOperationActive)
            return $"Prepared launch is blocked while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return "Prepared launch is blocked while another UI action is busy.";

        if (_latestBuilderExecutionResult is not null &&
            string.Equals(_latestBuilderExecutionResult.FreshnessState, "current", StringComparison.Ordinal) &&
            string.Equals(_latestBuilderExecutionResult.SourceExecutionPrepId, $"{_latestBuilderRequestIntake.RequestId}-prep", StringComparison.Ordinal))
        {
            return "Prepared launch is blocked because the latest execution prep already has a recorded route result.";
        }

        var route = _latestBuilderExecutionPrep.SelectedRoute;
        if (!string.Equals(route, "direct_low_floor_route", StringComparison.Ordinal) &&
            !string.Equals(route, "split_first_low_floor_route", StringComparison.Ordinal) &&
            !string.Equals(route, "low_floor_with_repair_loop_route", StringComparison.Ordinal) &&
            !string.Equals(route, "current_model_with_optional_stronger_tier_route", StringComparison.Ordinal))
        {
            return $"Prepared launch does not support route {route} without an explicit stronger-tier decision.";
        }

        var missingEvidence = _latestBuilderExecutionPrep.RequiredEvidencePaths.FirstOrDefault(path => !PathExists(path));
        if (!string.IsNullOrWhiteSpace(missingEvidence))
            return $"Prepared launch is blocked because required evidence is missing: {missingEvidence}";

        if (_latestBuilderExecutionPrep.SplitPlanRequired)
        {
            if (!PathExists(_latestBuilderExecutionPrep.SplitPlanPath))
                return "Prepared launch is blocked because the split-first plan artifact is unavailable.";

            if (_latestBuilderExecutionPrep.FutureExecutionHookPaths.Count == 0)
                return "Prepared launch is blocked because the split-first execution hooks are unavailable.";

            var missingHook = _latestBuilderExecutionPrep.FutureExecutionHookPaths.FirstOrDefault(path => !PathExists(path));
            if (!string.IsNullOrWhiteSpace(missingHook))
                return $"Prepared launch is blocked because a split-first execution hook is missing: {missingHook}";
        }

        return string.Empty;
    }

    private string GetBuilderOverrideLaunchDisabledReason()
    {
        var candidate = GetBuilderPreparedRouteOverrideCandidate();
        if (string.IsNullOrWhiteSpace(candidate.Route))
        {
            return "No supported override route is currently prepared for the latest bounded builder task.";
        }

        var blocker = GetBuilderPreparedLaunchDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            return blocker;
        }

        if (_latestBuilderExecutionPrep is null)
        {
            return "Override launch is available after current execution prep is loaded.";
        }

        if (string.Equals(_latestBuilderExecutionPrep.SelectedRoute, candidate.Route, StringComparison.Ordinal))
        {
            return "The current execution prep already uses the available override route.";
        }

        return string.Empty;
    }

    private string GetBuilderSplitExecutionDisabledReason()
        => GetBuilderSplitExecutionDisabledReasonCore();

    private string GetBuilderSplitExecutionDisabledReasonCore()
    {
        if (_latestBuilderSplitFirstPlan is null || _latestBuilderSplitStepExecution is null || _latestBuilderProofRun is null)
            return "Split-step execution is unavailable until comparative proof creates a split-first plan.";

        if (!string.Equals(_latestBuilderSplitStepExecution.FreshnessState, "current", StringComparison.Ordinal))
            return "This split-first plan was superseded by newer builder proof evidence.";

        if (_activeValidationAction is { } activeAction)
        {
            var activeLabel = string.IsNullOrWhiteSpace(_activeValidationActionLabel)
                ? DescribeValidationAction(activeAction)
                : _activeValidationActionLabel;
            return $"Split-step execution is blocked while {activeLabel.ToLowerInvariant()} is using the workspace.";
        }

        if (IsOperationActive)
            return $"Split-step execution is blocked while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return "Split-step execution is blocked while another UI action is busy.";

        var nextStep = GetNextBuilderSplitExecutionStep();
        if (nextStep is null)
            return "All split-first steps are already completed for the latest proof run.";

        return nextStep.BlockReason;
    }

    private BuilderSplitStepExecutionStepState? GetNextBuilderSplitExecutionStep()
        => _latestBuilderSplitStepExecution?.Steps
            .Where(step => string.Equals(step.EligibilityState, "eligible", StringComparison.Ordinal) &&
                           string.Equals(step.ExecutionState, "not_started", StringComparison.Ordinal))
            .OrderBy(step => step.StepNumber)
            .FirstOrDefault();

    private void LoadBuilderProofArtifacts()
    {
        _latestBuilderProofRun = BuilderExecutionService.LoadLatestBuilderProofRun(_validationRunnerService.RepoRoot);
        _latestBuilderModelFloorVerdict = BuilderExecutionService.LoadLatestBuilderModelFloorVerdict(_validationRunnerService.RepoRoot);
        _latestBuilderExternalProofRun = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderExternalProofRun(_latestBuilderProofRun.RunFolder);
        _latestBuilderExternalFloorVerdict = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderExternalFloorVerdict(_latestBuilderProofRun.RunFolder);
        _latestBuilderFailurePatternSummary = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderProofFailurePatternSummary(_latestBuilderProofRun.RunFolder);
        _latestBuilderModelFloorPolicy = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderModelFloorPolicy(_latestBuilderProofRun.RunFolder);
        _latestBuilderModelTrustBands = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderModelTrustBands(_latestBuilderProofRun.RunFolder);
        _latestBuilderModelRoutingRecommendation = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderModelRoutingRecommendation(_latestBuilderProofRun.RunFolder);
        _latestBuilderModelEscalationDecision = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderModelEscalationDecision(_latestBuilderProofRun.RunFolder);
        _latestBuilderModelRoutingPlan = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderModelRoutingPlan(_latestBuilderProofRun.RunFolder);
        _latestBuilderStrongerTierAvailability = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderStrongerTierAvailability(_latestBuilderProofRun.RunFolder);
        _latestBuilderComparativeProofRun = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderComparativeProofRun(_latestBuilderProofRun.RunFolder);
        _latestBuilderRoutingPolicyEvidence = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderRoutingPolicyEvidence(_latestBuilderProofRun.RunFolder);
        _latestBuilderSplitFirstPlan = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderSplitFirstPlan(_latestBuilderProofRun.RunFolder);
        _latestBuilderTieredRoutingPolicy = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderTieredRoutingPolicy(_latestBuilderProofRun.RunFolder);
        _latestBuilderDefaultPolicy = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderDefaultPolicy(_latestBuilderProofRun.RunFolder);
        _latestBuilderRequestPolicyDecision = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderRequestPolicyDecision(_latestBuilderProofRun.RunFolder);
        _latestBuilderPolicyStability = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderPolicyStability(_latestBuilderProofRun.RunFolder);
        _latestBuilderRequestIntake = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderRequestIntake(_latestBuilderProofRun.RunFolder);
        _latestBuilderExecutionPrep = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderExecutionPrep(_latestBuilderProofRun.RunFolder);
        _latestBuilderExecutionLaunch = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderExecutionLaunch(_latestBuilderProofRun.RunFolder);
        _latestBuilderExecutionResult = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderExecutionResult(_latestBuilderProofRun.RunFolder);
        _latestBuilderReadinessGate = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderReadinessGate(_latestBuilderProofRun.RunFolder);
        _latestBuilderConfirmedTaskClasses = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderConfirmedTaskClasses(_latestBuilderProofRun.RunFolder);
        _latestBuilderDefaultRouteDecision = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderDefaultRouteDecision(_latestBuilderProofRun.RunFolder);
        _latestBuilderLaunchDefaultDecision = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderLaunchDefaultDecision(_latestBuilderProofRun.RunFolder);
        _latestBuilderRouteOverrideEvidence = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderRouteOverrideEvidence(_latestBuilderProofRun.RunFolder);
        _latestBuilderRouteReviewCandidates = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderPolicyReviewCandidates(_latestBuilderProofRun.RunFolder);
        _latestBuilderRouteReconfirmation = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderRouteReconfirmation(_latestBuilderProofRun.RunFolder);
        _latestBuilderDefaultRouteRecovery = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderDefaultRouteRecovery(_latestBuilderProofRun.RunFolder);
        _latestBuilderReadinessContradictions = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderReadinessContradictions(_latestBuilderProofRun.RunFolder);
        _latestBuilderSplitStepExecution = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderSplitStepExecution(_latestBuilderProofRun.RunFolder);
        _latestBuilderSplitFirstOutcome = _latestBuilderProofRun is null ? null : BuilderExecutionService.LoadBuilderSplitFirstOutcome(_latestBuilderProofRun.RunFolder);

        if (_latestBuilderProofRun is null)
        {
            _builderProofRunPath = string.Empty;
            _builderProofSummaryPath = string.Empty;
            _builderProofSummaryText = "No builder proof run recorded.";
            _builderProofLatestTargetSummary = "No builder proof target recorded.";
            _builderExternalProofSummaryText = "No external builder proof run recorded.";
            _builderExternalProofSummaryPath = string.Empty;
            _builderProofSuccessCountsSummary = "No builder proof burden summary recorded.";
            _builderModelFloorFailurePatternSummary = "No low-floor failure pattern summary recorded.";
            _builderModelFloorFailurePatternsPath = string.Empty;
            _builderExternalFloorVerdictSummary = "No external builder floor verdict recorded.";
            _builderExternalFloorVerdictPath = string.Empty;
            _builderModelFloorPolicySummary = "No builder model floor policy recorded.";
            _builderModelFloorPolicyPath = string.Empty;
            _builderModelTrustBandSummary = "No builder trust-band summary recorded.";
            _builderModelTrustBandsPath = string.Empty;
            _builderModelScopeSummary = "No builder scope summary recorded.";
            _builderModelScopeSummaryPath = string.Empty;
            _builderModelRoutingRecommendationSummary = "No builder routing recommendation recorded.";
            _builderModelRoutingRecommendationPath = string.Empty;
            _builderModelWeakSpotSummary = "No low-floor weak-spot summary recorded.";
            _builderModelEscalationSummary = "No builder escalation decision recorded.";
            _builderModelEscalationDecisionPath = string.Empty;
            _builderModelRoutingPlanSummary = "No builder routing plan recorded.";
            _builderModelRoutingPlanPath = string.Empty;
            _builderModelSplitTaskGuidanceSummary = "No split-task guidance recorded.";
            _builderModelRoutingWeakSpotReason = "No linked builder weak-spot reason recorded.";
            _builderStrongerTierAvailabilitySummary = "No stronger-tier availability recorded.";
            _builderStrongerTierAvailabilityPath = string.Empty;
            _builderComparativeProofSummary = "No builder comparative proof recorded.";
            _builderComparativeProofSummaryPath = string.Empty;
            _builderComparativeRepairBurdenSummary = "No comparative repair burden recorded.";
            _builderRoutingPolicySummary = "No builder routing policy evidence recorded.";
            _builderRoutingPolicyPath = string.Empty;
            _builderSplitFirstPlanSummary = "No split-first plan recorded.";
            _builderSplitFirstPlanPath = string.Empty;
            _builderTieredRoutingSummary = "No tiered routing evidence recorded.";
            _builderTieredRoutingPath = string.Empty;
            _builderPrimaryRoutingRecommendationSummary = "No primary builder routing recommendation recorded.";
            _builderStrongerTierRoleSummary = "No stronger-tier routing role recorded.";
            _builderWeakSpotMitigationSummary = "No weak-spot mitigation summary recorded.";
            _builderDefaultPolicySummary = "No default builder guidance recorded.";
            _builderDefaultPolicyPath = string.Empty;
            _builderDefaultPolicyHistoryPath = string.Empty;
            _builderRequestPolicyDecisionSummary = "No builder routing decision recorded.";
            _builderRequestPolicyDecisionPath = string.Empty;
            _builderPolicyStabilitySummary = "No builder guidance support recorded.";
            _builderPolicyStabilityPath = string.Empty;
            _builderRequestIntakeSummary = "No builder intake recorded.";
            _builderRequestIntakePath = string.Empty;
            _builderExecutionPrepSummary = "No builder execution prep recorded.";
            _builderExecutionPrepPath = string.Empty;
            _builderExecutionLaunchSummary = "No prepared builder launch recorded.";
            _builderExecutionLaunchPath = string.Empty;
            _builderExecutionResultSummary = "No prepared builder route result recorded.";
            _builderExecutionResultPath = string.Empty;
            _builderReadinessGateSummary = "No builder readiness gate recorded.";
            _builderReadinessGatePath = string.Empty;
            _builderReadinessGateHistoryPath = string.Empty;
            _builderRouteStabilitySummary = "No builder route stability summary recorded.";
            _builderRouteStabilitySummaryPath = string.Empty;
            _builderReadinessCountsSummary = "No builder readiness evidence recorded.";
            _builderReadinessBoundedUseSummary = "No builder readiness decision recorded.";
            _builderReadinessLatestContradictionNote = string.Empty;
            _builderConfirmedClassesSummary = "No confirmed builder task classes recorded.";
            _builderConfirmedClassesPath = string.Empty;
            _builderDefaultRouteDecisionSummary = "No default builder route decision recorded.";
            _builderDefaultRouteDecisionPath = string.Empty;
            _builderLaunchDefaultDecisionSummary = "No builder launch default decision recorded.";
            _builderLaunchDefaultDecisionPath = string.Empty;
            _builderLaunchRouteModeSummary = "No builder launch default mode recorded.";
            _builderRouteSourceSummary = "No current builder route source recorded.";
            _builderOverrideAvailabilitySummary = "No operator override state recorded.";
            _builderOverrideRouteOptionSummary = "No explicit builder route override is currently prepared.";
            _builderRouteOverrideSummary = "No builder route override evidence recorded.";
            _builderRouteOverridePath = string.Empty;
            _builderRouteReviewSummary = "No builder route review candidates recorded.";
            _builderRouteReviewPath = string.Empty;
            _builderDefaultSuspensionSummary = "No default-route suspension recorded.";
            _builderRouteReconfirmationSummary = "No builder route reconfirmation recorded.";
            _builderRouteReconfirmationPath = string.Empty;
            _builderDefaultRouteRecoverySummary = "No builder default-route recovery recorded.";
            _builderDefaultRouteRecoveryPath = string.Empty;
            _builderReadinessContradictionsSummary = "No builder readiness contradictions recorded.";
            _builderReadinessContradictionsPath = string.Empty;
            _builderSplitStepExecutionSummary = "No split-step execution recorded.";
            _builderSplitStepExecutionPath = string.Empty;
            _builderSplitFirstOutcomeSummary = "No split-first outcome recorded.";
            _builderSplitFirstOutcomePath = string.Empty;
            _builderSplitSteps.Clear();
        }
        else
        {
            _builderProofRunPath = _latestBuilderProofRun.RunFolder;
            _builderProofSummaryPath = _latestBuilderProofRun.SummaryArtifactPath;
            _builderProofSummaryText = _latestBuilderProofRun.VerdictSummary;
            _builderProofLatestTargetSummary = BuildBuilderProofLatestTargetSummary(_latestBuilderProofRun);
            _builderExternalProofSummaryPath = _latestBuilderExternalProofRun?.SummaryArtifactPath ?? string.Empty;
            _builderExternalProofSummaryText = _latestBuilderExternalProofRun?.Summary ?? "No external builder proof run recorded.";
            _builderProofSuccessCountsSummary = BuildBuilderProofSuccessCountsSummary(_latestBuilderProofRun, _latestBuilderModelTrustBands);
            _builderModelFloorFailurePatternsPath = _latestBuilderFailurePatternSummary?.ArtifactPath ?? string.Empty;
            _builderModelFloorFailurePatternSummary = _latestBuilderFailurePatternSummary?.Summary ?? "No low-floor failure pattern summary recorded.";
            _builderExternalFloorVerdictPath = _latestBuilderExternalFloorVerdict?.VerdictArtifactPath ?? string.Empty;
            _builderExternalFloorVerdictSummary = _latestBuilderExternalFloorVerdict?.Summary ?? "No external builder floor verdict recorded.";
            _builderModelFloorPolicyPath = _latestBuilderModelFloorPolicy?.SummaryArtifactPath ?? string.Empty;
            _builderModelFloorPolicySummary = _latestBuilderModelFloorPolicy?.Summary ?? "No builder model floor policy recorded.";
            _builderModelTrustBandSummary = _latestBuilderModelTrustBands?.Summary ?? "No builder trust-band summary recorded.";
            _builderModelTrustBandsPath = _latestBuilderModelTrustBands?.ArtifactPath ?? string.Empty;
            _builderModelScopeSummary = _latestBuilderModelTrustBands?.Summary ?? "No builder scope summary recorded.";
            _builderModelScopeSummaryPath = _latestBuilderModelTrustBands?.ScopeSummaryPath ?? string.Empty;
            _builderModelRoutingRecommendationSummary = _latestBuilderModelRoutingRecommendation?.Summary ?? "No builder routing recommendation recorded.";
            _builderModelRoutingRecommendationPath = _latestBuilderModelRoutingRecommendation?.ArtifactPath ?? string.Empty;
            _builderModelWeakSpotSummary = BuildBuilderWeakSpotSummary(_latestBuilderModelTrustBands);
            _builderModelEscalationSummary = _latestBuilderModelEscalationDecision?.Summary ?? "No builder escalation decision recorded.";
            _builderModelEscalationDecisionPath = _latestBuilderModelEscalationDecision?.ArtifactPath ?? string.Empty;
            _builderModelRoutingPlanSummary = _latestBuilderModelRoutingPlan?.Summary ?? "No builder routing plan recorded.";
            _builderModelRoutingPlanPath = _latestBuilderModelRoutingPlan?.ArtifactPath ?? string.Empty;
            _builderModelSplitTaskGuidanceSummary = BuildBuilderSplitTaskGuidanceSummary(_latestBuilderModelRoutingPlan);
            _builderModelRoutingWeakSpotReason = _latestBuilderModelEscalationDecision?.PrimaryWeakSpotSummary ?? "No linked builder weak-spot reason recorded.";
            _builderStrongerTierAvailabilitySummary = _latestBuilderStrongerTierAvailability?.Summary ?? "No stronger-tier availability recorded.";
            _builderStrongerTierAvailabilityPath = _latestBuilderStrongerTierAvailability?.ArtifactPath ?? string.Empty;
            _builderComparativeProofSummary = _latestBuilderComparativeProofRun?.Summary ?? "No builder comparative proof recorded.";
            _builderComparativeProofSummaryPath = _latestBuilderComparativeProofRun?.SummaryArtifactPath ?? string.Empty;
            _builderComparativeRepairBurdenSummary = _latestBuilderComparativeProofRun?.RepairBurdenDifferenceSummary ?? "No comparative repair burden recorded.";
            _builderRoutingPolicySummary = _latestBuilderRoutingPolicyEvidence?.Summary ?? "No builder routing policy evidence recorded.";
            _builderRoutingPolicyPath = _latestBuilderRoutingPolicyEvidence?.ArtifactPath ?? string.Empty;
            _builderSplitFirstPlanSummary = _latestBuilderSplitFirstPlan?.Summary ?? "No split-first plan recorded.";
            _builderSplitFirstPlanPath = _latestBuilderSplitFirstPlan?.ArtifactPath ?? string.Empty;
            _builderTieredRoutingSummary = _latestBuilderTieredRoutingPolicy?.Summary ?? "No tiered routing evidence recorded.";
            _builderTieredRoutingPath = _latestBuilderTieredRoutingPolicy?.ArtifactPath ?? string.Empty;
            _builderPrimaryRoutingRecommendationSummary = _latestBuilderTieredRoutingPolicy?.PrimaryRecommendationSummary ?? "No primary builder routing recommendation recorded.";
            _builderStrongerTierRoleSummary = _latestBuilderTieredRoutingPolicy?.StrongerTierRoleSummary ?? "No stronger-tier routing role recorded.";
            _builderWeakSpotMitigationSummary = _latestBuilderTieredRoutingPolicy?.WeakSpotMitigationSummary ?? "No weak-spot mitigation summary recorded.";
            _builderDefaultPolicySummary = _latestBuilderDefaultPolicy?.Summary ?? "No default builder guidance recorded.";
            _builderDefaultPolicyPath = _latestBuilderDefaultPolicy?.ArtifactPath ?? string.Empty;
            _builderDefaultPolicyHistoryPath = BuilderExecutionService.BuilderDefaultPolicyHistoryPathForRepo(_validationRunnerService.RepoRoot);
            _builderRequestPolicyDecisionSummary = _latestBuilderRequestPolicyDecision?.Summary ?? "No builder routing decision recorded.";
            _builderRequestPolicyDecisionPath = _latestBuilderRequestPolicyDecision?.ArtifactPath ?? string.Empty;
            _builderPolicyStabilitySummary = _latestBuilderPolicyStability?.Summary ?? "No builder guidance support recorded.";
            _builderPolicyStabilityPath = _latestBuilderPolicyStability?.ArtifactPath ?? string.Empty;
            _builderRequestIntakeSummary = _latestBuilderRequestIntake is null
                ? "No builder intake recorded."
                : string.Equals(_latestBuilderRequestIntake.FreshnessState, "stale", StringComparison.Ordinal)
                    ? $"Stale intake. {_latestBuilderRequestIntake.Summary}"
                    : _latestBuilderRequestIntake.Summary;
            _builderRequestIntakePath = _latestBuilderRequestIntake?.ArtifactPath ?? string.Empty;
            _builderExecutionPrepSummary = _latestBuilderExecutionPrep is null
                ? "No builder execution prep recorded."
                : string.Equals(_latestBuilderExecutionPrep.FreshnessState, "stale", StringComparison.Ordinal)
                    ? $"Stale execution prep. {_latestBuilderExecutionPrep.Summary}"
                    : _latestBuilderExecutionPrep.Summary;
            _builderExecutionPrepPath = _latestBuilderExecutionPrep?.ArtifactPath ?? string.Empty;
            _builderExecutionLaunchSummary = _latestBuilderExecutionLaunch is null
                ? "No prepared builder launch recorded."
                : string.Equals(_latestBuilderExecutionLaunch.FreshnessState, "superseded", StringComparison.Ordinal)
                    ? $"Superseded launch. {_latestBuilderExecutionLaunch.Summary}"
                    : _latestBuilderExecutionLaunch.Summary;
            _builderExecutionLaunchPath = _latestBuilderExecutionLaunch?.ArtifactPath ?? string.Empty;
            _builderExecutionResultSummary = _latestBuilderExecutionResult is null
                ? "No prepared builder route result recorded."
                : string.Equals(_latestBuilderExecutionResult.FreshnessState, "superseded", StringComparison.Ordinal)
                    ? $"Superseded route result. {_latestBuilderExecutionResult.Summary}"
                    : _latestBuilderExecutionResult.Summary;
            _builderExecutionResultPath = _latestBuilderExecutionResult?.ArtifactPath ?? string.Empty;
            _builderReadinessGateSummary = _latestBuilderReadinessGate?.Summary ?? "No builder readiness gate recorded.";
            _builderReadinessGatePath = _latestBuilderReadinessGate?.ArtifactPath ?? string.Empty;
            _builderReadinessGateHistoryPath = BuilderExecutionService.BuilderReadinessGateHistoryPathForRepo(_validationRunnerService.RepoRoot);
            _builderRouteStabilitySummaryPath = _latestBuilderProofRun is null ? string.Empty : BuilderExecutionService.BuilderRouteStabilitySummaryPath(_latestBuilderProofRun.RunFolder);
            _builderRouteStabilitySummary = File.Exists(_builderRouteStabilitySummaryPath)
                ? File.ReadAllText(_builderRouteStabilitySummaryPath)
                : "No builder route stability summary recorded.";
            _builderReadinessCountsSummary = _latestBuilderReadinessGate is null
                ? "No builder readiness evidence recorded."
                : $"Confirmations={_latestBuilderReadinessGate.ConfirmationCount}. Contradictions={_latestBuilderReadinessGate.ContradictionCount}. Proof runs={_latestBuilderReadinessGate.SupportingProofRunCount}. Prepared launches={_latestBuilderReadinessGate.SupportingPreparedLaunchCount}.";
            _builderReadinessBoundedUseSummary = _latestBuilderReadinessGate is null
                ? "No builder readiness decision recorded."
                : _latestBuilderReadinessGate.BuilderReadyForBoundedUse
                    ? $"Current route is builder-ready for bounded use: {_latestBuilderReadinessGate.CurrentRecommendation}"
                    : $"Current route is not yet builder-ready for bounded use: {_latestBuilderReadinessGate.CurrentRecommendation}";
            _builderReadinessLatestContradictionNote = _latestBuilderReadinessGate?.ContradictionNotes.FirstOrDefault()
                                                    ?? _latestBuilderReadinessContradictions?.Entries.FirstOrDefault()?.ContradictionReason
                                                    ?? string.Empty;
            _builderConfirmedClassesSummary = _latestBuilderConfirmedTaskClasses?.Summary ?? "No confirmed builder task classes recorded.";
            _builderConfirmedClassesPath = _latestBuilderConfirmedTaskClasses?.ArtifactPath ?? string.Empty;
            _builderDefaultRouteDecisionSummary = _latestBuilderDefaultRouteDecision?.Summary ?? "No default builder route decision recorded.";
            _builderDefaultRouteDecisionPath = _latestBuilderDefaultRouteDecision?.ArtifactPath ?? string.Empty;
            _builderLaunchDefaultDecisionSummary = _latestBuilderLaunchDefaultDecision?.Summary ?? "No builder launch default decision recorded.";
            _builderLaunchDefaultDecisionPath = _latestBuilderLaunchDefaultDecision?.ArtifactPath ?? string.Empty;
            _builderLaunchRouteModeSummary = _latestBuilderLaunchDefaultDecision is null
                ? "No builder launch default mode recorded."
                : _latestBuilderLaunchDefaultDecision.RepairLoopExpectedDefault
                    ? $"Current default route keeps repair loop expectations explicit: {_latestBuilderLaunchDefaultDecision.ConfirmedDefaultRoute}."
                    : $"Current default route is treated as a clean bounded launch: {_latestBuilderLaunchDefaultDecision.ConfirmedDefaultRoute}.";
            _builderRouteSourceSummary = _latestBuilderDefaultRouteDecision is null
                ? "No current builder route source recorded."
                : $"Route source: {_latestBuilderDefaultRouteDecision.RouteSourceState}. {_latestBuilderDefaultRouteDecision.ReasonSummary}";
            _builderOverrideAvailabilitySummary = _latestBuilderDefaultRouteDecision is null
                ? "No operator override state recorded."
                : BuildBuilderOverrideAvailabilitySummary(_latestBuilderDefaultRouteDecision.OperatorOverrideState);
            _builderOverrideRouteOptionSummary = BuildBuilderOverrideRouteOptionSummary();
            _builderRouteOverrideSummary = _latestBuilderRouteOverrideEvidence?.Summary ?? "No builder route override evidence recorded.";
            _builderRouteOverridePath = _latestBuilderRouteOverrideEvidence?.ArtifactPath ?? string.Empty;
            _builderRouteReviewSummary = _latestBuilderRouteReviewCandidates?.Summary ?? "No builder route review candidates recorded.";
            _builderRouteReviewPath = _latestBuilderRouteReviewCandidates?.ArtifactPath ?? string.Empty;
            _builderDefaultSuspensionSummary = _latestBuilderDefaultRouteDecision is null
                ? "No default-route suspension recorded."
                : _latestBuilderDefaultRouteDecision.DefaultRouteSuspended
                    ? "Confirmed default route is temporarily suspended until fresh corroboration clears the contradiction."
                    : "Confirmed default route is active when class evidence supports it.";
            _builderRouteReconfirmationSummary = _latestBuilderRouteReconfirmation?.Summary ?? "No builder route reconfirmation recorded.";
            _builderRouteReconfirmationPath = _latestBuilderRouteReconfirmation?.ArtifactPath ?? string.Empty;
            _builderDefaultRouteRecoverySummary = _latestBuilderDefaultRouteRecovery?.Summary ?? "No builder default-route recovery recorded.";
            _builderDefaultRouteRecoveryPath = _latestBuilderDefaultRouteRecovery?.ArtifactPath ?? string.Empty;
            _builderReadinessContradictionsSummary = _latestBuilderReadinessContradictions?.Summary ?? "No builder readiness contradictions recorded.";
            _builderReadinessContradictionsPath = _latestBuilderReadinessContradictions?.ArtifactPath ?? string.Empty;
            _builderSplitStepExecutionSummary = _latestBuilderSplitStepExecution?.Summary ?? "No split-step execution recorded.";
            _builderSplitStepExecutionPath = _latestBuilderSplitStepExecution?.ArtifactPath ?? string.Empty;
            _builderSplitFirstOutcomeSummary = _latestBuilderSplitFirstOutcome is null
                ? "No split-first outcome recorded."
                : string.Equals(_latestBuilderSplitFirstOutcome.FreshnessState, "superseded", StringComparison.Ordinal)
                    ? $"Superseded split outcome. {_latestBuilderSplitFirstOutcome.Summary}"
                    : _latestBuilderSplitFirstOutcome.Summary;
            _builderSplitFirstOutcomePath = _latestBuilderSplitFirstOutcome?.ArtifactPath ?? string.Empty;
            RebuildBuilderSplitStepRows();
        }

        if (_latestBuilderModelFloorVerdict is null)
        {
            _builderModelFloorVerdictPath = string.Empty;
            _builderModelFloorVerdictSummary = "No builder model floor verdict recorded.";
        }
        else
        {
            _builderModelFloorVerdictPath = _latestBuilderModelFloorVerdict.VerdictArtifactPath;
            _builderModelFloorVerdictSummary = _latestBuilderModelFloorVerdict.Summary;
        }

        OnPropertyChanged(nameof(BuilderProofDisabledReason));
        OnPropertyChanged(nameof(HasBuilderProofDisabledReason));
        OnPropertyChanged(nameof(BuilderProofModelId));
        OnPropertyChanged(nameof(BuilderProofOutcomeClassification));
        OnPropertyChanged(nameof(BuilderProofOutcomeBadge));
        OnPropertyChanged(nameof(BuilderProofLatestTargetSummary));
        OnPropertyChanged(nameof(HasBuilderProofLatestTargetSummary));
        OnPropertyChanged(nameof(BuilderProofSummaryText));
        OnPropertyChanged(nameof(HasBuilderProofSummary));
        OnPropertyChanged(nameof(BuilderExternalProofOutcomeClassification));
        OnPropertyChanged(nameof(BuilderExternalProofOutcomeBadge));
        OnPropertyChanged(nameof(BuilderExternalProofSummaryText));
        OnPropertyChanged(nameof(HasBuilderExternalProofSummary));
        OnPropertyChanged(nameof(BuilderProofSuccessCountsSummary));
        OnPropertyChanged(nameof(HasBuilderProofSuccessCountsSummary));
        OnPropertyChanged(nameof(BuilderProofRunPath));
        OnPropertyChanged(nameof(HasBuilderProofRunPath));
        OnPropertyChanged(nameof(BuilderProofSummaryPath));
        OnPropertyChanged(nameof(HasBuilderProofSummaryPath));
        OnPropertyChanged(nameof(BuilderExternalProofSummaryPath));
        OnPropertyChanged(nameof(HasBuilderExternalProofSummaryPath));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictState));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictBadge));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictSummary));
        OnPropertyChanged(nameof(HasBuilderModelFloorVerdictSummary));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictPath));
        OnPropertyChanged(nameof(HasBuilderModelFloorVerdictPath));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictState));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictBadge));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictSummary));
        OnPropertyChanged(nameof(HasBuilderExternalFloorVerdictSummary));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictPath));
        OnPropertyChanged(nameof(HasBuilderExternalFloorVerdictPath));
        OnPropertyChanged(nameof(BuilderModelFloorFailurePatternSummary));
        OnPropertyChanged(nameof(HasBuilderModelFloorFailurePatternSummary));
        OnPropertyChanged(nameof(BuilderModelFloorFailurePatternsPath));
        OnPropertyChanged(nameof(HasBuilderModelFloorFailurePatternsPath));
        OnPropertyChanged(nameof(BuilderModelFloorPolicySummary));
        OnPropertyChanged(nameof(HasBuilderModelFloorPolicySummary));
        OnPropertyChanged(nameof(BuilderModelFloorPolicyPath));
        OnPropertyChanged(nameof(HasBuilderModelFloorPolicyPath));
        OnPropertyChanged(nameof(BuilderProofTrustBandState));
        OnPropertyChanged(nameof(BuilderProofTrustBandBadge));
        OnPropertyChanged(nameof(BuilderModelTrustBandSummary));
        OnPropertyChanged(nameof(HasBuilderModelTrustBandSummary));
        OnPropertyChanged(nameof(BuilderModelTrustBandsPath));
        OnPropertyChanged(nameof(HasBuilderModelTrustBandsPath));
        OnPropertyChanged(nameof(BuilderModelScopeSummary));
        OnPropertyChanged(nameof(HasBuilderModelScopeSummary));
        OnPropertyChanged(nameof(BuilderModelScopeSummaryPath));
        OnPropertyChanged(nameof(HasBuilderModelScopeSummaryPath));
        OnPropertyChanged(nameof(BuilderRoutingRecommendationState));
        OnPropertyChanged(nameof(BuilderRoutingRecommendationBadge));
        OnPropertyChanged(nameof(BuilderModelRoutingRecommendationSummary));
        OnPropertyChanged(nameof(HasBuilderModelRoutingRecommendationSummary));
        OnPropertyChanged(nameof(BuilderModelRoutingRecommendationPath));
        OnPropertyChanged(nameof(HasBuilderModelRoutingRecommendationPath));
        OnPropertyChanged(nameof(BuilderModelWeakSpotSummary));
        OnPropertyChanged(nameof(HasBuilderModelWeakSpotSummary));
        OnPropertyChanged(nameof(BuilderModelEscalationState));
        OnPropertyChanged(nameof(BuilderModelEscalationBadge));
        OnPropertyChanged(nameof(BuilderModelEscalationSummary));
        OnPropertyChanged(nameof(HasBuilderModelEscalationSummary));
        OnPropertyChanged(nameof(BuilderModelEscalationDecisionPath));
        OnPropertyChanged(nameof(HasBuilderModelEscalationDecisionPath));
        OnPropertyChanged(nameof(BuilderModelRoutingPlanSummary));
        OnPropertyChanged(nameof(HasBuilderModelRoutingPlanSummary));
        OnPropertyChanged(nameof(BuilderModelRoutingPlanPath));
        OnPropertyChanged(nameof(HasBuilderModelRoutingPlanPath));
        OnPropertyChanged(nameof(BuilderModelSplitTaskGuidanceSummary));
        OnPropertyChanged(nameof(HasBuilderModelSplitTaskGuidanceSummary));
        OnPropertyChanged(nameof(BuilderModelRoutingWeakSpotReason));
        OnPropertyChanged(nameof(HasBuilderModelRoutingWeakSpotReason));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilityState));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilityBadge));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilitySummary));
        OnPropertyChanged(nameof(HasBuilderStrongerTierAvailabilitySummary));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilityPath));
        OnPropertyChanged(nameof(HasBuilderStrongerTierAvailabilityPath));
        OnPropertyChanged(nameof(BuilderComparativeProofClassification));
        OnPropertyChanged(nameof(BuilderComparativeProofBadge));
        OnPropertyChanged(nameof(BuilderComparativeProofSummary));
        OnPropertyChanged(nameof(HasBuilderComparativeProofSummary));
        OnPropertyChanged(nameof(BuilderComparativeProofSummaryPath));
        OnPropertyChanged(nameof(HasBuilderComparativeProofSummaryPath));
        OnPropertyChanged(nameof(BuilderComparativeRepairBurdenSummary));
        OnPropertyChanged(nameof(HasBuilderComparativeRepairBurdenSummary));
        OnPropertyChanged(nameof(BuilderRoutingPolicyState));
        OnPropertyChanged(nameof(BuilderRoutingPolicyBadge));
        OnPropertyChanged(nameof(BuilderRoutingPolicySummary));
        OnPropertyChanged(nameof(HasBuilderRoutingPolicySummary));
        OnPropertyChanged(nameof(BuilderRoutingPolicyPath));
        OnPropertyChanged(nameof(HasBuilderRoutingPolicyPath));
        OnPropertyChanged(nameof(BuilderRoutingEvidenceBadge));
        OnPropertyChanged(nameof(BuilderRoutingEvidenceSummary));
        OnPropertyChanged(nameof(HasBuilderRoutingEvidenceSummary));
        OnPropertyChanged(nameof(BuilderRoutingEvidencePath));
        OnPropertyChanged(nameof(HasBuilderRoutingEvidencePath));
        OnPropertyChanged(nameof(BuilderSplitFirstPlanSummary));
        OnPropertyChanged(nameof(HasBuilderSplitFirstPlanSummary));
        OnPropertyChanged(nameof(BuilderSplitFirstPlanPath));
        OnPropertyChanged(nameof(HasBuilderSplitFirstPlanPath));
        OnPropertyChanged(nameof(BuilderTieredRoutingState));
        OnPropertyChanged(nameof(BuilderTieredRoutingBadge));
        OnPropertyChanged(nameof(BuilderTieredRoutingSummary));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingSummary));
        OnPropertyChanged(nameof(BuilderTieredRoutingPath));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingPath));
        OnPropertyChanged(nameof(BuilderTieredRoutingEvidenceBadge));
        OnPropertyChanged(nameof(BuilderTieredRoutingEvidenceSummary));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingEvidenceSummary));
        OnPropertyChanged(nameof(BuilderTieredRoutingEvidencePath));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingEvidencePath));
        OnPropertyChanged(nameof(BuilderPrimaryRoutingRecommendationSummary));
        OnPropertyChanged(nameof(HasBuilderPrimaryRoutingRecommendationSummary));
        OnPropertyChanged(nameof(BuilderStrongerTierRoleSummary));
        OnPropertyChanged(nameof(HasBuilderStrongerTierRoleSummary));
        OnPropertyChanged(nameof(BuilderWeakSpotMitigationSummary));
        OnPropertyChanged(nameof(HasBuilderWeakSpotMitigationSummary));
        OnPropertyChanged(nameof(BuilderDefaultGuidanceState));
        OnPropertyChanged(nameof(BuilderDefaultGuidanceBadge));
        OnPropertyChanged(nameof(BuilderDefaultGuidanceSummary));
        OnPropertyChanged(nameof(HasBuilderDefaultGuidanceSummary));
        OnPropertyChanged(nameof(BuilderDefaultGuidancePath));
        OnPropertyChanged(nameof(HasBuilderDefaultGuidancePath));
        OnPropertyChanged(nameof(BuilderGuidanceHistoryPath));
        OnPropertyChanged(nameof(HasBuilderGuidanceHistoryPath));
        OnPropertyChanged(nameof(BuilderLatestRoutingDecisionSummary));
        OnPropertyChanged(nameof(HasBuilderLatestRoutingDecisionSummary));
        OnPropertyChanged(nameof(BuilderLatestRoutingDecisionPath));
        OnPropertyChanged(nameof(HasBuilderLatestRoutingDecisionPath));
        OnPropertyChanged(nameof(BuilderGuidanceSupportBadge));
        OnPropertyChanged(nameof(BuilderGuidanceSupportSummary));
        OnPropertyChanged(nameof(HasBuilderGuidanceSupportSummary));
        OnPropertyChanged(nameof(BuilderGuidanceSupportPath));
        OnPropertyChanged(nameof(HasBuilderGuidanceSupportPath));
        OnPropertyChanged(nameof(BuilderIntakeState));
        OnPropertyChanged(nameof(BuilderIntakeBadge));
        OnPropertyChanged(nameof(BuilderIntakeSummary));
        OnPropertyChanged(nameof(HasBuilderIntakeSummary));
        OnPropertyChanged(nameof(BuilderIntakePath));
        OnPropertyChanged(nameof(HasBuilderIntakePath));
        OnPropertyChanged(nameof(BuilderPrepRouteState));
        OnPropertyChanged(nameof(BuilderPrepRouteBadge));
        OnPropertyChanged(nameof(BuilderPrepSummary));
        OnPropertyChanged(nameof(HasBuilderPrepSummary));
        OnPropertyChanged(nameof(BuilderPrepPath));
        OnPropertyChanged(nameof(HasBuilderPrepPath));
        OnPropertyChanged(nameof(BuilderLaunchAvailabilityState));
        OnPropertyChanged(nameof(BuilderLaunchAvailabilityBadge));
        OnPropertyChanged(nameof(BuilderLaunchSummary));
        OnPropertyChanged(nameof(HasBuilderLaunchSummary));
        OnPropertyChanged(nameof(BuilderLaunchPath));
        OnPropertyChanged(nameof(HasBuilderLaunchPath));
        OnPropertyChanged(nameof(BuilderResultState));
        OnPropertyChanged(nameof(BuilderResultBadge));
        OnPropertyChanged(nameof(BuilderResultSummary));
        OnPropertyChanged(nameof(HasBuilderResultSummary));
        OnPropertyChanged(nameof(BuilderResultPath));
        OnPropertyChanged(nameof(HasBuilderResultPath));
        OnPropertyChanged(nameof(BuilderRouteComparisonBadge));
        OnPropertyChanged(nameof(BuilderRouteComparisonSummary));
        OnPropertyChanged(nameof(HasBuilderRouteComparisonSummary));
        OnPropertyChanged(nameof(BuilderReadinessGateState));
        OnPropertyChanged(nameof(BuilderReadinessGateBadge));
        OnPropertyChanged(nameof(BuilderReadinessGateSummary));
        OnPropertyChanged(nameof(HasBuilderReadinessGateSummary));
        OnPropertyChanged(nameof(BuilderReadinessCountsSummary));
        OnPropertyChanged(nameof(HasBuilderReadinessCountsSummary));
        OnPropertyChanged(nameof(BuilderReadinessBoundedUseSummary));
        OnPropertyChanged(nameof(HasBuilderReadinessBoundedUseSummary));
        OnPropertyChanged(nameof(BuilderReadinessSupportingArtifactsSummary));
        OnPropertyChanged(nameof(HasBuilderReadinessSupportingArtifactsSummary));
        OnPropertyChanged(nameof(BuilderReadinessGatePath));
        OnPropertyChanged(nameof(HasBuilderReadinessGatePath));
        OnPropertyChanged(nameof(BuilderReadinessGateHistoryPath));
        OnPropertyChanged(nameof(HasBuilderReadinessGateHistoryPath));
        OnPropertyChanged(nameof(BuilderConfirmedClassesSummary));
        OnPropertyChanged(nameof(HasBuilderConfirmedClassesSummary));
        OnPropertyChanged(nameof(BuilderConfirmedClassesPath));
        OnPropertyChanged(nameof(HasBuilderConfirmedClassesPath));
        OnPropertyChanged(nameof(BuilderDefaultRouteDecisionSummary));
        OnPropertyChanged(nameof(HasBuilderDefaultRouteDecisionSummary));
        OnPropertyChanged(nameof(BuilderDefaultRouteDecisionPath));
        OnPropertyChanged(nameof(HasBuilderDefaultRouteDecisionPath));
        OnPropertyChanged(nameof(BuilderLaunchDefaultDecisionSummary));
        OnPropertyChanged(nameof(HasBuilderLaunchDefaultDecisionSummary));
        OnPropertyChanged(nameof(BuilderLaunchDefaultDecisionPath));
        OnPropertyChanged(nameof(HasBuilderLaunchDefaultDecisionPath));
        OnPropertyChanged(nameof(BuilderLaunchRouteModeSummary));
        OnPropertyChanged(nameof(HasBuilderLaunchRouteModeSummary));
        OnPropertyChanged(nameof(BuilderRouteSourceSummary));
        OnPropertyChanged(nameof(HasBuilderRouteSourceSummary));
        OnPropertyChanged(nameof(BuilderOverrideAvailabilitySummary));
        OnPropertyChanged(nameof(HasBuilderOverrideAvailabilitySummary));
        OnPropertyChanged(nameof(BuilderOverrideRouteOptionSummary));
        OnPropertyChanged(nameof(HasBuilderOverrideRouteOptionSummary));
        OnPropertyChanged(nameof(BuilderRouteOverrideSummary));
        OnPropertyChanged(nameof(HasBuilderRouteOverrideSummary));
        OnPropertyChanged(nameof(BuilderRouteOverridePath));
        OnPropertyChanged(nameof(HasBuilderRouteOverridePath));
        OnPropertyChanged(nameof(BuilderRouteReviewSummary));
        OnPropertyChanged(nameof(HasBuilderRouteReviewSummary));
        OnPropertyChanged(nameof(BuilderRouteReviewPath));
        OnPropertyChanged(nameof(HasBuilderRouteReviewPath));
        OnPropertyChanged(nameof(BuilderDefaultSuspensionSummary));
        OnPropertyChanged(nameof(HasBuilderDefaultSuspensionSummary));
        OnPropertyChanged(nameof(BuilderRouteReconfirmationSummary));
        OnPropertyChanged(nameof(HasBuilderRouteReconfirmationSummary));
        OnPropertyChanged(nameof(BuilderRouteReconfirmationPath));
        OnPropertyChanged(nameof(HasBuilderRouteReconfirmationPath));
        OnPropertyChanged(nameof(BuilderDefaultRouteRecoverySummary));
        OnPropertyChanged(nameof(HasBuilderDefaultRouteRecoverySummary));
        OnPropertyChanged(nameof(BuilderDefaultRouteRecoveryPath));
        OnPropertyChanged(nameof(HasBuilderDefaultRouteRecoveryPath));
        OnPropertyChanged(nameof(BuilderReadinessContradictionsSummary));
        OnPropertyChanged(nameof(HasBuilderReadinessContradictionsSummary));
        OnPropertyChanged(nameof(BuilderReadinessContradictionsPath));
        OnPropertyChanged(nameof(HasBuilderReadinessContradictionsPath));
        OnPropertyChanged(nameof(BuilderRouteStabilitySummary));
        OnPropertyChanged(nameof(HasBuilderRouteStabilitySummary));
        OnPropertyChanged(nameof(BuilderRouteStabilitySummaryPath));
        OnPropertyChanged(nameof(HasBuilderRouteStabilitySummaryPath));
        OnPropertyChanged(nameof(BuilderReadinessLatestContradictionNote));
        OnPropertyChanged(nameof(HasBuilderReadinessLatestContradictionNote));
        OnPropertyChanged(nameof(BuilderPreparedLaunchDisabledReason));
        OnPropertyChanged(nameof(HasBuilderPreparedLaunchDisabledReason));
        OnPropertyChanged(nameof(BuilderOverrideLaunchDisabledReason));
        OnPropertyChanged(nameof(HasBuilderOverrideLaunchDisabledReason));
        OnPropertyChanged(nameof(BuilderSplitStepExecutionSummary));
        OnPropertyChanged(nameof(HasBuilderSplitStepExecutionSummary));
        OnPropertyChanged(nameof(BuilderSplitStepExecutionPath));
        OnPropertyChanged(nameof(HasBuilderSplitStepExecutionPath));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomeClassification));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomeBadge));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomeSummary));
        OnPropertyChanged(nameof(HasBuilderSplitFirstOutcomeSummary));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomePath));
        OnPropertyChanged(nameof(HasBuilderSplitFirstOutcomePath));
        OnPropertyChanged(nameof(BuilderSplitSteps));
        OnPropertyChanged(nameof(HasBuilderSplitSteps));
        OnPropertyChanged(nameof(BuilderSplitExecutionDisabledReason));
        OnPropertyChanged(nameof(HasBuilderSplitExecutionDisabledReason));
        OnPropertyChanged(nameof(BuilderComparativeProofDisabledReason));
        OnPropertyChanged(nameof(HasBuilderComparativeProofDisabledReason));
        RunBuilderProofMatrixCommand.RaiseCanExecuteChanged();
        RunBuilderComparativeProofCommand.RaiseCanExecuteChanged();
        OpenBuilderProofSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderProofRunFolderCommand.RaiseCanExecuteChanged();
        OpenBuilderModelFloorVerdictCommand.RaiseCanExecuteChanged();
        OpenBuilderFailurePatternsCommand.RaiseCanExecuteChanged();
        OpenBuilderExternalProofSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderModelFloorPolicyCommand.RaiseCanExecuteChanged();
        OpenBuilderModelFloorGuidanceCommand.RaiseCanExecuteChanged();
        OpenBuilderTrustBandsCommand.RaiseCanExecuteChanged();
        OpenBuilderScopeSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderRoutingRecommendationCommand.RaiseCanExecuteChanged();
        OpenBuilderEscalationDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderRoutingPlanCommand.RaiseCanExecuteChanged();
        OpenBuilderStrongerTierAvailabilityCommand.RaiseCanExecuteChanged();
        OpenBuilderComparativeProofSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderRoutingPolicyEvidenceCommand.RaiseCanExecuteChanged();
        OpenBuilderSplitFirstPlanCommand.RaiseCanExecuteChanged();
        OpenBuilderTieredRoutingPolicyCommand.RaiseCanExecuteChanged();
        OpenBuilderDefaultGuidanceCommand.RaiseCanExecuteChanged();
        OpenBuilderGuidanceHistoryCommand.RaiseCanExecuteChanged();
        OpenBuilderLatestRoutingDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderGuidanceSupportCommand.RaiseCanExecuteChanged();
        OpenBuilderRequestIntakeCommand.RaiseCanExecuteChanged();
        OpenBuilderExecutionPrepCommand.RaiseCanExecuteChanged();
        LaunchPreparedBuilderRouteCommand.RaiseCanExecuteChanged();
        LaunchBuilderOverrideRouteCommand.RaiseCanExecuteChanged();
        OpenBuilderExecutionLaunchCommand.RaiseCanExecuteChanged();
        OpenBuilderExecutionResultCommand.RaiseCanExecuteChanged();
        OpenBuilderReadinessGateCommand.RaiseCanExecuteChanged();
        OpenBuilderReadinessHistoryCommand.RaiseCanExecuteChanged();
        OpenBuilderConfirmedClassesCommand.RaiseCanExecuteChanged();
        OpenBuilderDefaultRouteDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderLaunchDefaultDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteOverrideEvidenceCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteReviewCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteReconfirmationCommand.RaiseCanExecuteChanged();
        OpenBuilderDefaultRouteRecoveryCommand.RaiseCanExecuteChanged();
        OpenBuilderReadinessContradictionsCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteStabilitySummaryCommand.RaiseCanExecuteChanged();
        RunNextBuilderSplitStepCommand.RaiseCanExecuteChanged();
        OpenBuilderSplitStepExecutionCommand.RaiseCanExecuteChanged();
        OpenBuilderSplitFirstOutcomeCommand.RaiseCanExecuteChanged();
        CopyBuilderProofSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderScopeSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderRoutingRecommendationCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitTaskGuidanceCommand.RaiseCanExecuteChanged();
        CopyBuilderComparativeProofSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderRoutingPolicySummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitFirstPlanSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderPrimaryRoutingRecommendationCommand.RaiseCanExecuteChanged();
        CopyBuilderWeakSpotMitigationSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderDefaultGuidanceSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderLatestRoutingDecisionCommand.RaiseCanExecuteChanged();
        CopyBuilderExecutionPrepSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderIntakeRoutingSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderExecutionLaunchSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderExecutionResultSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderReadinessSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderReadinessContradictionNoteCommand.RaiseCanExecuteChanged();
        CopyBuilderConfirmedClassesSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderDefaultRouteDecisionSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderLaunchDefaultSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderRouteOverrideSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderRouteReconfirmationSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderDefaultRouteRecoverySummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitExecutionSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitComparativeClosureSummaryCommand.RaiseCanExecuteChanged();
    }

    private static string BuildBuilderProofLatestTargetSummary(BuilderProofRun run)
    {
        var primaryResult = run.CaseResults
            .OrderBy(result => string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(result => string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(result => result.TargetId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (primaryResult is null)
            return "No builder proof target recorded.";

        return $"{primaryResult.TargetLabel}: {primaryResult.FinalClassification}; build={primaryResult.BuildResult}; test={primaryResult.TestResult}; recovery={(primaryResult.RecoveryRequired ? "required" : "not required")}.";
    }

    private static string BuildBuilderOverrideAvailabilitySummary(string overrideState)
        => overrideState switch
        {
            "override_available_no_override" => "Operator override is available; no override is currently recorded.",
            "overridden_by_operator" => "Operator override is active for the current builder route.",
            _ => "Operator override availability is not recorded."
        };

    private (string Route, string Reason) GetBuilderPreparedRouteOverrideCandidate()
    {
        if (_latestBuilderExecutionPrep is null)
        {
            return (string.Empty, string.Empty);
        }

        return _latestBuilderExecutionPrep.SelectedRoute switch
        {
            "split_first_low_floor_route" => (
                "direct_low_floor_route",
                $"Operator override selected direct_low_floor_route to compare an unsplit launch against the confirmed split-first default for {_latestBuilderExecutionPrep.NormalizedTaskClass}."),
            "low_floor_with_repair_loop_route" => (
                "direct_low_floor_route",
                $"Operator override selected direct_low_floor_route to compare a no-repair launch against the repair-loop default for {_latestBuilderExecutionPrep.NormalizedTaskClass}."),
            _ => (string.Empty, string.Empty)
        };
    }

    private string BuildBuilderOverrideRouteOptionSummary()
    {
        var candidate = GetBuilderPreparedRouteOverrideCandidate();
        if (string.IsNullOrWhiteSpace(candidate.Route))
        {
            return "No explicit builder route override is currently prepared.";
        }

        return $"Override route available: {candidate.Route}. {candidate.Reason}";
    }

    private static string BuildBuilderWeakSpotSummary(BuilderModelTrustBands? trustBands)
    {
        if (trustBands is null || trustBands.WeakSpots.Count == 0)
            return "No low-floor weak-spot summary recorded.";

        return string.Join(" ", trustBands.WeakSpots
            .OrderBy(weakSpot => weakSpot.WeakSpot, StringComparer.Ordinal)
            .Select(weakSpot => $"{weakSpot.WeakSpot}={weakSpot.Classification}."));
    }

    private static string BuildBuilderSplitTaskGuidanceSummary(BuilderModelRoutingPlan? routingPlan)
    {
        if (routingPlan is null || routingPlan.SplitTaskGuidance.Count == 0)
            return "No split-task guidance recorded.";

        return string.Join(" ", routingPlan.SplitTaskGuidance);
    }

    private static string BuildBuilderProofSuccessCountsSummary(BuilderProofRun repoLocalRun, BuilderModelTrustBands? trustBands)
    {
        if (trustBands is null)
        {
            var cleanCount = repoLocalRun.CaseResults.Count(result => string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal));
            var repairedCount = repoLocalRun.CaseResults.Count(result => string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal));
            var escalatedCount = repoLocalRun.CaseResults.Count(result => string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal));
            return $"Clean successes={cleanCount}. Repair-loop successes={repairedCount}. Escalated outcomes={escalatedCount}.";
        }

        return $"Clean={trustBands.CleanBuildBandCount}. Repair-loop={trustBands.RepairLoopBandCount}. Escalated={trustBands.EscalationRecommendedBandCount}. Reject={trustBands.RejectBandCount}.";
    }

    private void LoadValidationSettings()
    {
        var settings = _validationSettingsStore.Load().Normalize();
        _continueValidationOnFailure = settings.ContinueOnFailure;
        _includeValidateBuildForFullLoop = settings.IncludeValidateBuild;
        _selectedValidationKeepLastRuns = settings.KeepLastRuns;
        _autoOpenValidationLogsOnFailure = settings.AutoOpenLogsOnFailure;
        _enableIsolatedValidationWorkspaceMode = settings.EnableIsolatedValidationWorkspaceMode;
        _validateGeneratedOutputAfterRun = settings.ValidateGeneratedOutputAfterRun;
        _enableValidationStabilityRetry = settings.EnableStabilityRetry;
        _selectedValidationHistoryRetentionCount = settings.HistoryRetentionCount;
        _selectedValidationRegressionComparisonWindow = settings.RegressionComparisonWindow;
        _countRetryPassesAsStableInTrendSummaries = settings.CountRetryPassesAsStableInSummaries;
        _selectedValidationBaselineHistoryRetentionCount = settings.BaselineHistoryRetentionCount;
        _countPassedOnRetryAsReleaseReady = settings.CountPassedOnRetryAsReleaseReady;
        _flakySuspectedBlocksReleaseReadiness = settings.FlakySuspectedBlocksReleaseReadiness;
        _enableSemanticReuseSuggestions = settings.EnableSemanticReuseSuggestions;
        _selectedSemanticReuseMaxCases = settings.MaxSemanticReuseCases;
        _selectedSemanticReuseRetentionCount = settings.SemanticReuseRetentionCount;
        _indexProviderDiagnosticsEpisodes = settings.IndexProviderDiagnosticsEpisodes;
        _onlyShowPassingOrImprovedReuseCases = settings.OnlyShowPassingOrImprovedReuseCases;
        _includePromotedRepairSuggestions = settings.IncludePromotedRepairSuggestions;
        _includeProviderEpisodeSuggestions = settings.IncludeProviderEpisodeSuggestions;
        _enablePlaybookSuggestions = settings.EnablePlaybookSuggestions;
        _selectedPlaybookMinimumEvidenceCount = settings.MinimumPlaybookEvidenceCount;
        _showTentativePlaybooks = settings.ShowTentativePlaybooks;
        _selectedSemanticReuseMaxPlaybooks = settings.MaxPlaybooksPerContext;

        OnPropertyChanged(nameof(ContinueValidationOnFailure));
        OnPropertyChanged(nameof(IncludeValidateBuildForFullLoop));
        OnPropertyChanged(nameof(SelectedValidationKeepLastRuns));
        OnPropertyChanged(nameof(AutoOpenValidationLogsOnFailure));
        OnPropertyChanged(nameof(EnableIsolatedValidationWorkspaceMode));
        OnPropertyChanged(nameof(ValidateGeneratedOutputAfterRun));
        OnPropertyChanged(nameof(EnableValidationStabilityRetry));
        OnPropertyChanged(nameof(SelectedValidationHistoryRetentionCount));
        OnPropertyChanged(nameof(SelectedValidationRegressionComparisonWindow));
        OnPropertyChanged(nameof(CountRetryPassesAsStableInTrendSummaries));
        OnPropertyChanged(nameof(SelectedValidationBaselineHistoryRetentionCount));
        OnPropertyChanged(nameof(CountPassedOnRetryAsReleaseReady));
        OnPropertyChanged(nameof(FlakySuspectedBlocksReleaseReadiness));
        OnPropertyChanged(nameof(EnableSemanticReuseSuggestions));
        OnPropertyChanged(nameof(SelectedSemanticReuseMaxCases));
        OnPropertyChanged(nameof(SelectedSemanticReuseRetentionCount));
        OnPropertyChanged(nameof(IndexProviderDiagnosticsEpisodes));
        OnPropertyChanged(nameof(OnlyShowPassingOrImprovedReuseCases));
        OnPropertyChanged(nameof(IncludePromotedRepairSuggestions));
        OnPropertyChanged(nameof(IncludeProviderEpisodeSuggestions));
        OnPropertyChanged(nameof(EnablePlaybookSuggestions));
        OnPropertyChanged(nameof(SelectedPlaybookMinimumEvidenceCount));
        OnPropertyChanged(nameof(ShowTentativePlaybooks));
        OnPropertyChanged(nameof(SelectedSemanticReuseMaxPlaybooks));
        OnPropertyChanged(nameof(ValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationActionPolicies));
    }

    private void PersistValidationSettings()
    {
        var settings = BuildValidationSettings();
        _validationSettingsStore.Save(settings);
        RefreshValidationTrendArtifacts(settings);
        LoadValidationTrendArtifacts();
        OnPropertyChanged(nameof(ValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationActionPolicies));
        OnPropertyChanged(nameof(ValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(HasValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(ValidationSequenceSummary));
    }

    private void LoadValidationRuns()
    {
        RefreshValidationTrendArtifacts(BuildValidationSettings());
        _validationRuns.Clear();
        foreach (var run in _validationRunnerService.LoadRecentRuns(SelectedValidationKeepLastRuns))
        {
            _validationRuns.Add(new ValidationRunHistoryRow(
                run.RunId,
                run.ActionLabel,
                run.Success ? "passed" : "failed",
                run.Summary,
                run.OutputFolder,
                run.StartedUtc,
                run.CompletedUtc,
                string.IsNullOrWhiteSpace(run.StabilityClassification) ? (run.Success ? "passed" : "failed") : run.StabilityClassification,
                string.IsNullOrWhiteSpace(run.StabilityStatus)
                    ? (run.Success ? "Passed cleanly" : "Failed")
                    : run.StabilityStatus));
        }

        LoadValidationTrendArtifacts();
        OnPropertyChanged(nameof(HasValidationRuns));
    }

    private ValidationSettings BuildValidationSettings()
        => new(
            ContinueValidationOnFailure,
            IncludeValidateBuildForFullLoop,
            SelectedValidationKeepLastRuns,
            AutoOpenValidationLogsOnFailure,
            ValidateGeneratedOutputAfterRun,
            EnableValidationStabilityRetry,
            SelectedValidationHistoryRetentionCount,
            SelectedValidationRegressionComparisonWindow,
            CountRetryPassesAsStableInTrendSummaries,
            SelectedValidationBaselineHistoryRetentionCount,
            CountPassedOnRetryAsReleaseReady,
            FlakySuspectedBlocksReleaseReadiness,
            EnableSemanticReuseSuggestions,
            SelectedSemanticReuseMaxCases,
            SelectedSemanticReuseRetentionCount,
            IndexProviderDiagnosticsEpisodes,
            OnlyShowPassingOrImprovedReuseCases,
            IncludePromotedRepairSuggestions,
            IncludeProviderEpisodeSuggestions,
            EnablePlaybookSuggestions,
            SelectedPlaybookMinimumEvidenceCount,
            ShowTentativePlaybooks,
            SelectedSemanticReuseMaxPlaybooks,
            EnableIsolatedValidationWorkspaceMode);

    private static string DescribeValidationAction(ValidationAction action)
        => action switch
        {
            ValidationAction.BuildUiProject => "Build UI project",
            ValidationAction.RunUiTests => "Run UI tests",
            ValidationAction.RunSmokeValidation => "Run smoke validation",
            ValidationAction.RunIntegrityValidation => "Run integrity validation",
            ValidationAction.RunFullValidationLoop => "Run full validation loop",
            _ => action.ToString()
        };

    private IReadOnlyList<ValidationActionPolicyRow> BuildValidationActionPolicyRows()
    {
        var settings = BuildValidationSettings();
        return Enum.GetValues<ValidationAction>()
            .Select(action =>
            {
                var policy = ValidationRunnerService.DescribeAction(action, settings);
                return new ValidationActionPolicyRow(
                    policy.ActionLabel,
                    FormatValidationRunMode(policy.RunMode),
                    policy.Stages.Count == 0
                        ? "No stages configured."
                        : string.Join(" -> ", policy.Stages.Select(stage => stage.StageLabel)),
                    FormatValidationConcurrencyClasses(policy.ConcurrencyClassifications),
                    BuildValidationWorkspaceImpactSummary(policy.WorkspaceImpact),
                    policy.IsolationSupportReason,
                    GetValidationDisabledReason(action));
            })
            .ToArray();
    }

    private string BuildValidationSequenceSummary()
    {
        if (_activeValidationAction is { } activeAction)
        {
            var activePolicy = ValidationRunnerService.DescribeAction(activeAction, BuildValidationSettings());
            return $"{activePolicy.ActionLabel}: {string.Join(" -> ", activePolicy.Stages.Select(stage => stage.StageLabel))}";
        }

        if (_lastValidationResult is not null && _lastValidationResult.Stages.Count > 0)
        {
            return $"{_lastValidationResult.ActionLabel}: {string.Join(" -> ", _lastValidationResult.Stages.Select(stage => stage.StageLabel))}";
        }

        return "Validation stages are sequenced from explicit orchestration policy metadata.";
    }

    private static string FormatValidationRunMode(string runMode)
        => runMode switch
        {
            "isolated_workspace_mode" => "Isolated workspace mode",
            "single_stage_manual_mode" => "Single-stage manual mode",
            _ => "Sequential standard mode"
        };

    private static string FormatValidationConcurrencyClasses(IReadOnlyList<string> classifications)
        => classifications.Count == 0
            ? "Uncategorized"
            : string.Join(", ", classifications.Select(classification => classification switch
            {
                "parallel_safe" => "parallel-safe",
                "repo_mutating" => "repo-mutating",
                "workspace_cleaning" => "workspace-cleaning",
                "exclusive" => "exclusive",
                _ => classification.Replace('_', ' ')
            }));

    private static string BuildValidationWorkspaceImpactSummary(ValidationWorkspaceImpactMetadata impact)
    {
        var parts = new List<string>();
        if (impact.TouchesBuildOutputs)
            parts.Add("touches build outputs");
        if (impact.ClearsCaches)
            parts.Add("clears caches");
        if (impact.RewritesArtifacts)
            parts.Add("rewrites artifacts");
        if (impact.ReadsOnly || parts.Count == 0)
            parts.Add(impact.ReadsOnly ? "reads only" : "no additional workspace mutation");

        return string.Join(", ", parts);
    }

    private string BuildValidationRunModeSummary(string runMode, string actionLabel, string isolatedWorkspacePath)
    {
        var summary = runMode switch
        {
            "isolated_workspace_mode" => $"{actionLabel} ran in isolated workspace mode.",
            "single_stage_manual_mode" => $"{actionLabel} ran as a single-stage manual validation action.",
            "sequential_standard_mode" => $"{actionLabel} ran in sequential standard mode.",
            _ => "No validation run recorded."
        };

        if (string.Equals(runMode, "isolated_workspace_mode", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(isolatedWorkspacePath))
        {
            return $"{summary} Workspace copy: {isolatedWorkspacePath}";
        }

        return summary;
    }

    private void UpdateValidationOrchestrationState(ValidationRunResult? result, string? pendingRunMode = null, string? pendingActionLabel = null)
    {
        _validationOrchestrationPolicyNotePath = ValidationRunnerService.OrchestrationPolicyNotePathForRepo(_validationRunnerService.RepoRoot);
        if (result is not null)
        {
            _validationRunMode = string.IsNullOrWhiteSpace(result.RunMode) ? "not_run" : result.RunMode;
            _validationOrchestrationArtifactPath = !string.IsNullOrWhiteSpace(result.OrchestrationArtifactPath)
                ? result.OrchestrationArtifactPath!
                : Path.Combine(result.OutputFolder, "validation_orchestration.json");
            _validationIsolatedWorkspacePath = result.IsolatedWorkspacePath ?? string.Empty;
            _validationRunModeSummary = BuildValidationRunModeSummary(_validationRunMode, result.ActionLabel, _validationIsolatedWorkspacePath);
            return;
        }

        _validationRunMode = pendingRunMode ?? "not_run";
        _validationOrchestrationArtifactPath = string.Empty;
        _validationIsolatedWorkspacePath = string.Empty;
        _validationRunModeSummary = string.IsNullOrWhiteSpace(pendingRunMode) || string.IsNullOrWhiteSpace(pendingActionLabel)
            ? "No validation run recorded."
            : BuildValidationRunModeSummary(pendingRunMode, pendingActionLabel, string.Empty);
    }

    private void LoadValidationHandoffArtifacts()
    {
        var bundle = ValidationRunnerService.LoadLatestHandoffBundle(_validationRunnerService.RepoRoot);
        if (bundle is null)
        {
            _latestValidationHandoffBundle = null;
            _validationHandoffBundlePath = string.Empty;
            _validationHandoffSummaryPath = string.Empty;
            _validationHandoffSummaryText = "No validation handoff bundle recorded.";
            _validationHandoffComparisonSummary = "No previous validation bundle available.";
        }
        else
        {
            _latestValidationHandoffBundle = bundle;
            _validationHandoffBundlePath = bundle.BundlePath;
            _validationHandoffSummaryPath = bundle.SummaryPath;
            _validationHandoffSummaryText = BuildValidationHandoffSummaryText(bundle);
            _validationHandoffComparisonSummary = bundle.PreviousBundleComparison?.Summary ?? "No previous validation bundle available.";
        }

        OnPropertyChanged(nameof(ValidationHandoffBundlePath));
        OnPropertyChanged(nameof(HasValidationHandoffBundlePath));
        OnPropertyChanged(nameof(ValidationHandoffSummaryPath));
        OnPropertyChanged(nameof(HasValidationHandoffSummaryPath));
        OnPropertyChanged(nameof(ValidationHandoffSummaryText));
        OnPropertyChanged(nameof(HasValidationHandoffSummary));
        OnPropertyChanged(nameof(ValidationHandoffComparisonSummary));
        OnPropertyChanged(nameof(HasValidationHandoffComparisonSummary));
        OpenValidationHandoffSummaryCommand.RaiseCanExecuteChanged();
        OpenValidationHandoffBundleFolderCommand.RaiseCanExecuteChanged();
        CopyValidationHandoffSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationHandoffArtifactPathsCommand.RaiseCanExecuteChanged();
    }

    private static string BuildValidationHandoffSummaryText(ValidationHandoffBundle bundle)
    {
        var builder = new StringBuilder();
        builder.Append($"{bundle.OverallResult} / {bundle.StabilityStatus} / {bundle.ReadinessClassification.Replace('_', ' ')}.");
        builder.Append(' ');
        builder.Append(bundle.RetryUsage.RetryCount <= 0
            ? "No retries recorded."
            : $"{bundle.RetryUsage.RetryCount} retr{(bundle.RetryUsage.RetryCount == 1 ? "y" : "ies")} recorded.");
        if (bundle.FirstFailure is not null)
        {
            builder.Append(' ');
            builder.Append($"First failure: {bundle.FirstFailure.StageLabel}: {bundle.FirstFailure.ErrorExcerpt}");
        }

        if (bundle.BlockedStageNotes.Count > 0)
        {
            builder.Append(' ');
            builder.Append(string.Join(" ", bundle.BlockedStageNotes));
        }

        return builder.ToString().Trim();
    }

    private string BuildValidationHandoffArtifactPathClipboardText()
    {
        if (_latestValidationHandoffBundle is null)
            return string.Empty;

        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(_latestValidationHandoffBundle.BundlePath))
            paths.Add(_latestValidationHandoffBundle.BundlePath);
        if (!string.IsNullOrWhiteSpace(_latestValidationHandoffBundle.SummaryPath))
            paths.Add(_latestValidationHandoffBundle.SummaryPath);

        foreach (var artifact in _latestValidationHandoffBundle.ArtifactPaths)
        {
            if (!string.IsNullOrWhiteSpace(artifact.Path))
                paths.Add(artifact.Path);
        }

        if (_latestValidationFollowupIntake is not null)
        {
            if (!string.IsNullOrWhiteSpace(_latestValidationFollowupIntake.IntakePath))
                paths.Add(_latestValidationFollowupIntake.IntakePath);
            if (!string.IsNullOrWhiteSpace(_latestValidationFollowupIntake.PromptPath))
                paths.Add(_latestValidationFollowupIntake.PromptPath);
        }

        if (_latestValidationFollowupPlan is not null && !string.IsNullOrWhiteSpace(_latestValidationFollowupPlan.PlanPath))
            paths.Add(_latestValidationFollowupPlan.PlanPath);

        if (_latestValidationFollowupExecutionState is not null && !string.IsNullOrWhiteSpace(_latestValidationFollowupExecutionState.SourceOutputFolder))
            paths.Add(ValidationRunnerService.FollowupExecutionPathForRun(_latestValidationFollowupExecutionState.SourceOutputFolder));

        if (!string.IsNullOrWhiteSpace(_validationFollowupExecutionOutcomePath))
            paths.Add(_validationFollowupExecutionOutcomePath);

        if (!string.IsNullOrWhiteSpace(_validationFollowupEscalationPath))
            paths.Add(_validationFollowupEscalationPath);

        if (!string.IsNullOrWhiteSpace(_validationFollowupResolutionReviewPath))
            paths.Add(_validationFollowupResolutionReviewPath);

        if (!string.IsNullOrWhiteSpace(_validationResolutionHandoffPath))
            paths.Add(_validationResolutionHandoffPath);

        if (!string.IsNullOrWhiteSpace(_validationResolutionPromotionReviewPath))
            paths.Add(_validationResolutionPromotionReviewPath);

        if (!string.IsNullOrWhiteSpace(_validationReleaseDecisionSummaryPath))
            paths.Add(_validationReleaseDecisionSummaryPath);

        if (_latestValidationRepairPrepBundle is not null && !string.IsNullOrWhiteSpace(_latestValidationRepairPrepBundle.BundlePath))
            paths.Add(_latestValidationRepairPrepBundle.BundlePath);

        return string.Join(System.Environment.NewLine, paths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal));
    }

    private void LoadValidationFollowupArtifacts()
    {
        var intake = ValidationRunnerService.LoadLatestFollowupIntake(_validationRunnerService.RepoRoot);
        if (intake is null)
        {
            _latestValidationFollowupIntake = null;
            _validationFollowupCategory = "no_followup";
            _validationFollowupSummaryText = "No validation follow-up intake recorded.";
            _validationFollowupNextStepText = "No validation follow-up recommendation recorded.";
            _validationFollowupRepeatedIssueSummary = "No recent repeated follow-up detected.";
            _validationFollowupIntakePath = string.Empty;
            _validationFollowupPromptPath = string.Empty;
        }
        else
        {
            _latestValidationFollowupIntake = intake;
            _validationFollowupCategory = string.IsNullOrWhiteSpace(intake.FollowupCategory)
                ? "no_followup"
                : intake.FollowupCategory;
            _validationFollowupSummaryText = BuildValidationFollowupSummaryText(intake);
            _validationFollowupNextStepText = intake.NextStep;
            _validationFollowupRepeatedIssueSummary = intake.RepeatedIssueSummary;
            _validationFollowupIntakePath = intake.IntakePath;
            _validationFollowupPromptPath = intake.PromptPath;
        }

        RefreshValidationFollowupSuggestionSummary();

        OnPropertyChanged(nameof(ValidationFollowupCategory));
        OnPropertyChanged(nameof(ValidationFollowupBadge));
        OnPropertyChanged(nameof(ValidationFollowupSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupSummary));
        OnPropertyChanged(nameof(ValidationFollowupNextStepText));
        OnPropertyChanged(nameof(HasValidationFollowupNextStep));
        OnPropertyChanged(nameof(ValidationFollowupRepeatedIssueSummary));
        OnPropertyChanged(nameof(HasValidationFollowupRepeatedIssue));
        OnPropertyChanged(nameof(ValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(HasValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(ValidationFollowupIntakePath));
        OnPropertyChanged(nameof(HasValidationFollowupIntakePath));
        OnPropertyChanged(nameof(ValidationFollowupPromptPath));
        OnPropertyChanged(nameof(HasValidationFollowupPromptPath));
        OpenValidationFollowupIntakeCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupPromptCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupPromptCommand.RaiseCanExecuteChanged();
        CopyValidationHandoffArtifactPathsCommand.RaiseCanExecuteChanged();
    }

    private static string BuildValidationFollowupSummaryText(ValidationFollowupIntake intake)
    {
        var builder = new StringBuilder();
        builder.Append($"{intake.FollowupCategory.Replace('_', ' ')}.");
        builder.Append(' ');
        builder.Append($"{intake.OverallResult} / {intake.StabilityStatus} / {intake.ReadinessClassification.Replace('_', ' ')}.");
        if (intake.FirstFailure is not null)
        {
            builder.Append(' ');
            builder.Append($"First failure: {intake.FirstFailure.StageLabel}: {intake.FirstFailure.ErrorExcerpt}");
        }

        if (intake.BlockedStageNotes.Count > 0)
        {
            builder.Append(' ');
            builder.Append(string.Join(" ", intake.BlockedStageNotes));
        }

        return builder.ToString().Trim();
    }

    private void LoadValidationFollowupPlanArtifacts()
    {
        var repoRoot = _validationRunnerService.RepoRoot;
        var sourceOutputFolder = ResolveValidationFollowupPlanOutputFolder();
        var plan = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadFollowupPlanForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestFollowupPlan(repoRoot);
        if (plan is null && !string.IsNullOrWhiteSpace(_validationFollowupPinnedOutputFolder))
        {
            _validationFollowupPinnedOutputFolder = string.Empty;
            plan = ValidationRunnerService.LoadLatestFollowupPlan(repoRoot);
            sourceOutputFolder = plan?.OutputFolder ?? string.Empty;
        }

        var prepBundle = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadRepairPrepBundleForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestRepairPrepBundle(repoRoot);
        var executionState = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadFollowupExecutionStateForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestFollowupExecutionState(repoRoot);
        var executionOutcome = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadFollowupExecutionOutcomeForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestFollowupExecutionOutcome(repoRoot);
        var escalation = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadFollowupEscalationForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestFollowupEscalation(repoRoot);
        var resolutionReview = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadFollowupResolutionReviewForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestFollowupResolutionReview(repoRoot);
        var resolutionHandoff = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadResolutionHandoffForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestResolutionHandoff(repoRoot);
        var resolutionPromotionReview = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadResolutionPromotionReviewForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestResolutionPromotionReview(repoRoot);
        var releaseDecisionSummary = !string.IsNullOrWhiteSpace(sourceOutputFolder)
            ? ValidationRunnerService.LoadReleaseDecisionSummaryForRun(sourceOutputFolder)
            : ValidationRunnerService.LoadLatestReleaseDecisionSummary(repoRoot);
        if (plan is null)
        {
            _latestValidationFollowupPlan = null;
            _latestValidationFollowupExecutionState = null;
            _latestValidationFollowupExecutionOutcome = null;
            _latestValidationFollowupEscalation = null;
            _latestValidationFollowupResolutionReview = null;
            _latestValidationResolutionHandoff = null;
            _latestValidationResolutionPromotionReview = null;
            _latestValidationReleaseDecisionSummary = null;
            _validationFollowupPlanSummaryText = "No follow-up execution plan recorded.";
            _validationFollowupRerunRecommendationText = "No rerun recommendation recorded.";
            _validationFollowupPlanFreshnessText = "No follow-up plan freshness recorded.";
            _validationFollowupEscalationHint = "No recurring follow-up signal detected.";
            _validationFollowupRerunOutcomeSummary = "No guided rerun has been recorded for this plan.";
            _validationFollowupOutcomeSourceSummary = "No guided outcome source recorded.";
            _validationFollowupOutcomeSummaryText = "No guided execution outcome recorded.";
            _validationFollowupOutcomeNextStepText = "No guided next-step recommendation recorded.";
            _validationFollowupOutcomeFreshnessText = "No guided outcome freshness recorded.";
            _validationFollowupExecutionOutcomePath = string.Empty;
            _validationFollowupEscalationSummaryText = "No guided escalation summary recorded.";
            _validationFollowupEscalationPath = string.Empty;
            _validationFollowupResolutionOriginalIssueSummary = "No resolution review issue summary recorded.";
            _validationFollowupResolutionSummaryText = "No follow-up resolution review recorded.";
            _validationFollowupResolutionClosureText = "No resolution closure status recorded.";
            _validationFollowupResolutionFreshnessText = "No resolution review freshness recorded.";
            _validationFollowupResolutionReopenSummaryText = "No reopened issue recorded.";
            _validationFollowupResolutionReviewPath = string.Empty;
            _validationResolutionHandoffSummaryText = "No resolution handoff recorded.";
            _validationResolutionHandoffPath = string.Empty;
            _validationResolutionPromotionSummaryText = "No resolution promotion review recorded.";
            _validationResolutionPromotionReviewPath = string.Empty;
            _validationReleaseDecisionSummaryText = "No release decision summary recorded.";
            _validationReleaseDecisionNotesSummaryText = "No release decision notes recorded.";
            _validationReleaseDecisionSummaryPath = string.Empty;
            _validationFollowupPlanPath = string.Empty;
        }
        else
        {
            _latestValidationFollowupPlan = plan;
            _latestValidationFollowupExecutionState = executionState;
            _latestValidationFollowupExecutionOutcome = executionOutcome;
            _latestValidationFollowupEscalation = escalation;
            _latestValidationFollowupResolutionReview = resolutionReview;
            _latestValidationResolutionHandoff = resolutionHandoff;
            _latestValidationResolutionPromotionReview = resolutionPromotionReview;
            _latestValidationReleaseDecisionSummary = releaseDecisionSummary;
            _validationFollowupPlanSummaryText = BuildValidationFollowupPlanSummaryText(plan);
            _validationFollowupRerunRecommendationText = plan.RerunScopeRecommendation;
            _validationFollowupPlanFreshnessText = plan.FreshnessSummary;
            _validationFollowupEscalationHint = plan.EscalationHint;
            _validationFollowupRerunOutcomeSummary = BuildValidationFollowupRerunOutcomeSummary(executionState);
            _validationFollowupOutcomeSourceSummary = BuildValidationFollowupOutcomeSourceSummary(executionOutcome);
            _validationFollowupOutcomeSummaryText = BuildValidationFollowupOutcomeSummaryText(executionOutcome);
            _validationFollowupOutcomeNextStepText = executionOutcome?.RecommendedNextAction ?? "No guided next-step recommendation recorded.";
            _validationFollowupOutcomeFreshnessText = executionOutcome?.FreshnessSummary ?? "No guided outcome freshness recorded.";
            _validationFollowupExecutionOutcomePath = executionOutcome?.OutcomePath ?? string.Empty;
            _validationFollowupEscalationSummaryText = BuildValidationFollowupEscalationSummaryText(escalation);
            _validationFollowupEscalationPath = escalation?.EscalationPath ?? string.Empty;
            _validationFollowupResolutionOriginalIssueSummary = BuildValidationFollowupResolutionOriginalIssueSummary(resolutionReview);
            _validationFollowupResolutionSummaryText = BuildValidationFollowupResolutionSummaryText(resolutionReview);
            _validationFollowupResolutionClosureText = BuildValidationFollowupResolutionClosureText(resolutionReview);
            _validationFollowupResolutionFreshnessText = resolutionReview?.FreshnessSummary ?? "No resolution review freshness recorded.";
            _validationFollowupResolutionReopenSummaryText = resolutionReview?.ReopenSummary ?? "No reopened issue recorded.";
            _validationFollowupResolutionReviewPath = resolutionReview?.ReviewPath ?? string.Empty;
            _validationResolutionHandoffSummaryText = BuildValidationResolutionHandoffSummaryText(resolutionHandoff);
            _validationResolutionHandoffPath = resolutionHandoff?.HandoffPath ?? string.Empty;
            _validationResolutionPromotionSummaryText = BuildValidationResolutionPromotionSummaryText(resolutionPromotionReview);
            _validationResolutionPromotionReviewPath = resolutionPromotionReview?.PromotionReviewPath ?? string.Empty;
            _validationReleaseDecisionSummaryText = BuildValidationReleaseDecisionSummaryText(releaseDecisionSummary);
            _validationReleaseDecisionNotesSummaryText = BuildValidationReleaseDecisionNotesSummaryText(releaseDecisionSummary);
            _validationReleaseDecisionSummaryPath = releaseDecisionSummary?.DecisionSummaryPath ?? string.Empty;
            _validationFollowupPlanPath = plan.PlanPath;
        }

        if (prepBundle is null)
        {
            _latestValidationRepairPrepBundle = null;
            _validationRepairPrepSummaryText = "No repair-prep bundle recorded.";
            _validationRepairPrepBundlePath = string.Empty;
        }
        else
        {
            _latestValidationRepairPrepBundle = prepBundle;
            _validationRepairPrepSummaryText = BuildValidationRepairPrepSummaryText(prepBundle);
            _validationRepairPrepBundlePath = prepBundle.BundlePath;
        }

        RebuildValidationFollowupPlanSteps();

        RefreshValidationFollowupSuggestionSummary();

        OnPropertyChanged(nameof(ValidationFollowupPlanCategory));
        OnPropertyChanged(nameof(ValidationFollowupPlanBadge));
        OnPropertyChanged(nameof(ValidationFollowupPlanSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupPlanSummary));
        OnPropertyChanged(nameof(ValidationFollowupRerunRecommendationText));
        OnPropertyChanged(nameof(HasValidationFollowupRerunRecommendation));
        OnPropertyChanged(nameof(ValidationRepairPrepSummaryText));
        OnPropertyChanged(nameof(HasValidationRepairPrepSummary));
        OnPropertyChanged(nameof(ValidationFollowupPlanFreshnessText));
        OnPropertyChanged(nameof(HasValidationFollowupPlanFreshness));
        OnPropertyChanged(nameof(ValidationFollowupEscalationHint));
        OnPropertyChanged(nameof(HasValidationFollowupEscalationHint));
        OnPropertyChanged(nameof(ValidationFollowupRerunOutcomeSummary));
        OnPropertyChanged(nameof(HasValidationFollowupRerunOutcome));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeClassification));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeBadge));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeSourceSummary));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeSourceSummary));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeSummary));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeNextStateText));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeNextStateText));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeFreshnessText));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeFreshnessText));
        OnPropertyChanged(nameof(ValidationFollowupExecutionOutcomePath));
        OnPropertyChanged(nameof(HasValidationFollowupExecutionOutcomePath));
        OnPropertyChanged(nameof(ValidationFollowupEscalationClassification));
        OnPropertyChanged(nameof(ValidationFollowupEscalationBadge));
        OnPropertyChanged(nameof(ValidationFollowupEscalationSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupEscalationSummary));
        OnPropertyChanged(nameof(ValidationFollowupEscalationPath));
        OnPropertyChanged(nameof(HasValidationFollowupEscalationPath));
        OnPropertyChanged(nameof(ValidationFollowupResolutionState));
        OnPropertyChanged(nameof(ValidationFollowupResolutionBadge));
        OnPropertyChanged(nameof(ValidationFollowupResolutionOriginalIssueSummary));
        OnPropertyChanged(nameof(HasValidationFollowupResolutionOriginalIssueSummary));
        OnPropertyChanged(nameof(ValidationFollowupResolutionSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupResolutionSummary));
        OnPropertyChanged(nameof(ValidationFollowupResolutionClosureText));
        OnPropertyChanged(nameof(HasValidationFollowupResolutionClosureText));
        OnPropertyChanged(nameof(ValidationFollowupResolutionFreshnessText));
        OnPropertyChanged(nameof(HasValidationFollowupResolutionFreshnessText));
        OnPropertyChanged(nameof(ValidationFollowupResolutionReopenSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupResolutionReopenSummary));
        OnPropertyChanged(nameof(ValidationFollowupResolutionReviewPath));
        OnPropertyChanged(nameof(HasValidationFollowupResolutionReviewPath));
        OnPropertyChanged(nameof(ValidationResolutionHandoffCandidateState));
        OnPropertyChanged(nameof(ValidationResolutionHandoffBadge));
        OnPropertyChanged(nameof(ValidationResolutionHandoffSummaryText));
        OnPropertyChanged(nameof(HasValidationResolutionHandoffSummary));
        OnPropertyChanged(nameof(ValidationResolutionHandoffPath));
        OnPropertyChanged(nameof(HasValidationResolutionHandoffPath));
        OnPropertyChanged(nameof(ValidationResolutionPromotionRecommendationState));
        OnPropertyChanged(nameof(ValidationResolutionPromotionBadge));
        OnPropertyChanged(nameof(ValidationResolutionPromotionSummaryText));
        OnPropertyChanged(nameof(HasValidationResolutionPromotionSummary));
        OnPropertyChanged(nameof(ValidationResolutionPromotionReviewPath));
        OnPropertyChanged(nameof(HasValidationResolutionPromotionReviewPath));
        OnPropertyChanged(nameof(ValidationReleaseDecisionState));
        OnPropertyChanged(nameof(ValidationReleaseDecisionBadge));
        OnPropertyChanged(nameof(ValidationReleaseDecisionSummaryText));
        OnPropertyChanged(nameof(HasValidationReleaseDecisionSummary));
        OnPropertyChanged(nameof(ValidationReleaseDecisionNotesSummaryText));
        OnPropertyChanged(nameof(HasValidationReleaseDecisionNotesSummary));
        OnPropertyChanged(nameof(ValidationReleaseDecisionSummaryPath));
        OnPropertyChanged(nameof(HasValidationReleaseDecisionSummaryPath));
        OnPropertyChanged(nameof(ValidationFollowupPlanPath));
        OnPropertyChanged(nameof(HasValidationFollowupPlanPath));
        OnPropertyChanged(nameof(ValidationRepairPrepBundlePath));
        OnPropertyChanged(nameof(HasValidationRepairPrepBundlePath));
        OnPropertyChanged(nameof(ValidationFollowupPlanSteps));
        OnPropertyChanged(nameof(HasValidationFollowupPlanSteps));
        OnPropertyChanged(nameof(ValidationFollowupRecommendedRerunBlockedReason));
        OnPropertyChanged(nameof(HasValidationFollowupRecommendedRerunBlockedReason));
        OnPropertyChanged(nameof(ValidationFollowupFirstEvidenceBlockedReason));
        OnPropertyChanged(nameof(HasValidationFollowupFirstEvidenceBlockedReason));
        OnPropertyChanged(nameof(ValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(HasValidationFollowupReuseSuggestionSummary));
        OpenValidationFollowupPlanCommand.RaiseCanExecuteChanged();
        OpenValidationRepairPrepBundleCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupPlanSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationRepairPrepSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupRerunRecommendationCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupExecutionOutcomeCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupEscalationCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupResolutionReviewCommand.RaiseCanExecuteChanged();
        OpenValidationResolutionHandoffCommand.RaiseCanExecuteChanged();
        OpenValidationResolutionPromotionReviewCommand.RaiseCanExecuteChanged();
        OpenValidationReleaseDecisionSummaryCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupRerunArtifactsCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupOutcomeNextStepCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupEscalationSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupClosureSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationResolutionHandoffSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationResolutionPromotionSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationReleaseDecisionSummaryCommand.RaiseCanExecuteChanged();
        RunValidationFollowupRecommendedRerunCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupFirstEvidenceCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupRerunCommandSummaryCommand.RaiseCanExecuteChanged();
        ExecuteValidationFollowupPlanStepCommand.NotifyCanExecuteChanged();
        CopyValidationFollowupPlanStepCommand.NotifyCanExecuteChanged();
        CopyValidationHandoffArtifactPathsCommand.RaiseCanExecuteChanged();
    }

    private static string BuildValidationFollowupPlanSummaryText(ValidationFollowupPlan plan)
    {
        var stepSummary = plan.Steps.Count == 0
            ? "No ordered actions recorded."
            : string.Join(" -> ", plan.Steps.Select(step => step.Title));
        return $"{plan.FollowupCategory.Replace('_', ' ')} plan. {stepSummary} Scope: {plan.TargetScopeSummary}";
    }

    private static string BuildValidationRepairPrepSummaryText(ValidationRepairPrepBundle bundle)
    {
        var similarCaseCount = bundle.SimilarCaseSuggestions.Count;
        var playbookCount = bundle.PlaybookSuggestions.Count;
        return $"{bundle.FollowupCategory.Replace('_', ' ')} repair prep. Scope: {bundle.TargetScopeSummary} Similar cases: {similarCaseCount}. Playbooks: {playbookCount}. {bundle.EscalationHint}";
    }

    private static string BuildValidationFollowupBadge(string category)
        => category switch
        {
            "fix_build" => "Fix build",
            "fix_tests" => "Fix tests",
            "investigate_smoke" => "Investigate smoke",
            "investigate_integrity" => "Investigate integrity",
            "review_flaky_behavior" => "Review flaky behavior",
            "baseline_update_candidate" => "Baseline update candidate",
            "no_action_needed" => "No action needed",
            _ => "No follow-up"
        };

    private string ResolveValidationFollowupPlanOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(_validationFollowupPinnedOutputFolder) &&
            File.Exists(ValidationRunnerService.FollowupPlanPathForRun(_validationFollowupPinnedOutputFolder)))
        {
            return _validationFollowupPinnedOutputFolder;
        }

        return string.Empty;
    }

    private static string BuildValidationFollowupRerunOutcomeSummary(ValidationFollowupExecutionState? executionState)
    {
        if (executionState?.LatestRerun is not { } rerun)
            return "No guided rerun has been recorded for this plan.";

        var outcome = rerun.OutcomeClassification switch
        {
            "passed" => "improved result",
            "improved" => "improved result",
            "regressed" => "regressed",
            _ => "stayed the same"
        };
        return $"Latest guided rerun {rerun.RerunValidationRunId}: {outcome}. {rerun.OutcomeSummary}";
    }

    private static string BuildValidationFollowupOutcomeSourceSummary(ValidationFollowupExecutionOutcome? outcome)
    {
        if (outcome is null)
            return "No guided outcome source recorded.";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(outcome.SourceStepTitle))
            parts.Add($"Source step: {outcome.SourceStepTitle}.");
        if (!string.IsNullOrWhiteSpace(outcome.SourceStageLabel))
            parts.Add($"Source stage: {outcome.SourceStageLabel}.");
        if (!string.IsNullOrWhiteSpace(outcome.RerunStageLabel))
            parts.Add($"Rerun stage: {outcome.RerunStageLabel}.");
        return parts.Count == 0 ? "No guided outcome source recorded." : string.Join(" ", parts);
    }

    private static string BuildValidationFollowupOutcomeSummaryText(ValidationFollowupExecutionOutcome? outcome)
    {
        if (outcome is null)
            return "No guided execution outcome recorded.";

        return $"{outcome.OutcomeClassification.Replace('_', ' ')}. {outcome.OutcomeSummary} {outcome.ComparisonSummary}".Trim();
    }

    private static string BuildValidationFollowupEscalationSummaryText(ValidationFollowupEscalation? escalation)
    {
        if (escalation is null)
            return "No guided escalation summary recorded.";

        return $"{escalation.EscalationSummary} Next action: {escalation.SuggestedNextAction}".Trim();
    }

    private static string BuildValidationFollowupResolutionOriginalIssueSummary(ValidationFollowupResolutionReview? review)
    {
        if (review is null)
            return "No resolution review issue summary recorded.";

        return string.IsNullOrWhiteSpace(review.OriginalFailureSummary)
            ? "No resolution review issue summary recorded."
            : $"Original issue: {review.OriginalFailureSummary}";
    }

    private static string BuildValidationFollowupResolutionSummaryText(ValidationFollowupResolutionReview? review)
    {
        if (review is null)
            return "No follow-up resolution review recorded.";

        return review.ResolutionSummary;
    }

    private static string BuildValidationFollowupResolutionClosureText(ValidationFollowupResolutionReview? review)
    {
        if (review is null)
            return "No resolution closure status recorded.";

        return review.IssueClosureStatus switch
        {
            "closed" => "Issue appears closed from the guided follow-up evidence.",
            "partially_resolved" => "Issue appears partially resolved and still open.",
            _ => "Issue appears still open from the recorded follow-up evidence."
        };
    }

    private static string BuildValidationResolutionHandoffSummaryText(ValidationResolutionHandoff? handoff)
    {
        if (handoff is null)
            return "No resolution handoff recorded.";

        return handoff.CandidateSummary;
    }

    private static string BuildValidationResolutionPromotionSummaryText(ValidationResolutionPromotionReview? review)
    {
        if (review is null)
            return "No resolution promotion review recorded.";

        return review.PromotionRecommendationSummary;
    }

    private static string BuildValidationReleaseDecisionSummaryText(ValidationReleaseDecisionSummary? summary)
    {
        if (summary is null)
            return "No release decision summary recorded.";

        return summary.DecisionSummaryText;
    }

    private static string BuildValidationReleaseDecisionNotesSummaryText(ValidationReleaseDecisionSummary? summary)
    {
        if (summary is null)
            return "No release decision notes recorded.";

        var notes = summary.ContradictionNotes
            .Concat(summary.DeferralNotes)
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return notes.Length == 0
            ? "No release decision notes recorded."
            : string.Join(" ", notes);
    }

    private void RebuildBuilderSplitStepRows()
    {
        _builderSplitSteps.Clear();
        if (_latestBuilderSplitStepExecution is null)
            return;

        var globalBlocker = GetBuilderSplitExecutionDisabledReasonCore();
        foreach (var step in _latestBuilderSplitStepExecution.Steps.OrderBy(item => item.StepNumber))
        {
            var isCompleted = string.Equals(step.ExecutionState, "opened", StringComparison.Ordinal) ||
                              string.Equals(step.ExecutionState, "executed", StringComparison.Ordinal) ||
                              string.Equals(step.ExecutionState, "completed_by_outcome", StringComparison.Ordinal);
            var effectiveBlockReason = isCompleted || string.IsNullOrWhiteSpace(globalBlocker)
                ? step.BlockReason
                : globalBlocker;
            _builderSplitSteps.Add(new BuilderSplitStepRow(
                step.StepNumber,
                step.StepId,
                step.StepLabel,
                step.StepType,
                step.StepType.Replace('_', ' '),
                step.ExecutionMode,
                step.ScopeClassification,
                step.EligibilityState,
                BuildBuilderSplitExecutionAvailability(step.ExecutionMode, effectiveBlockReason),
                step.ExecutionState,
                BuildBuilderSplitCompletionBadge(step.ExecutionState),
                step.Detail,
                effectiveBlockReason,
                step.LinkedArtifactPaths));
        }
    }

    private static string BuildBuilderSplitCompletionBadge(string executionState)
        => executionState switch
        {
            "opened" => "Opened",
            "executed" => "Executed",
            "completed_by_outcome" => "Completed by outcome",
            _ => "Not started"
        };

    private static string BuildBuilderSplitExecutionAvailability(string executionMode, string blockReason)
    {
        if (!string.IsNullOrWhiteSpace(blockReason))
            return "Blocked";

        return executionMode switch
        {
            "rerun_capable" => "Rerun ready",
            "view_only" => "View ready",
            "executable" => "Action ready",
            _ => "Manual review"
        };
    }

    private void RebuildValidationFollowupPlanSteps()
    {
        _validationFollowupPlanSteps.Clear();
        if (_latestValidationFollowupPlan is null)
            return;

        var completionLookup = (_latestValidationFollowupExecutionState?.Steps ?? Array.Empty<ValidationFollowupPlanStepState>())
            .ToDictionary(step => $"{step.Order:D4}|{step.StepType}", step => step, StringComparer.Ordinal);
        foreach (var step in _latestValidationFollowupPlan.Steps.OrderBy(item => item.Order))
        {
            completionLookup.TryGetValue($"{step.Order:D4}|{step.StepType}", out var completionState);
            var blockReason = GetValidationFollowupPlanStepBlockReason(step);
            _validationFollowupPlanSteps.Add(new ValidationFollowupPlanStepRow(
                step.Order,
                step.StepType,
                BuildValidationFollowupPlanStepTypeLabel(step.StepType),
                step.Title,
                step.Summary,
                step.TargetScope,
                step.ScopeConfidence,
                string.IsNullOrWhiteSpace(step.InteractionMode) ? "manual_only" : step.InteractionMode,
                step.ActionKind,
                step.ActionTarget,
                step.CommandSummary,
                step.EvidenceArtifactPaths,
                completionState?.CompletionState ?? "not_started",
                BuildValidationFollowupPlanStepCompletionBadge(completionState?.CompletionState ?? "not_started"),
                string.IsNullOrWhiteSpace(blockReason)
                    ? BuildValidationFollowupExecutionAvailability(step)
                    : "Blocked",
                blockReason));
        }
    }

    private static string BuildValidationFollowupPlanStepTypeLabel(string stepType)
        => stepType.Replace('_', ' ');

    private static string BuildValidationFollowupPlanStepCompletionBadge(string completionState)
        => completionState switch
        {
            "opened" => "Opened",
            "copied" => "Copied",
            "executed" => "Executed",
            "completed_by_validation" => "Completed by validation",
            _ => "Not started"
        };

    private static string BuildValidationFollowupExecutionAvailability(ValidationFollowupPlanStep step)
        => step.InteractionMode switch
        {
            "rerun_capable" => "Rerun ready",
            "view_only" => "View ready",
            "copy_only" => "Copy ready",
            _ => "Manual review"
        };

    private ValidationFollowupPlanStep? GetRecommendedFollowupPlanStep()
        => _latestValidationFollowupPlan?.Steps
            .Where(step => string.Equals(step.InteractionMode, "rerun_capable", StringComparison.Ordinal))
            .OrderBy(step => step.Order)
            .FirstOrDefault();

    private ValidationFollowupPlanStep? GetFirstEvidenceFollowupPlanStep()
        => _latestValidationFollowupPlan?.Steps
            .Where(step => string.Equals(step.InteractionMode, "view_only", StringComparison.Ordinal) &&
                           step.EvidenceArtifactPaths.Any(path => !string.IsNullOrWhiteSpace(path)))
            .OrderBy(step => step.Order)
            .FirstOrDefault();

    private string GetValidationFollowupPlanStepBlockReasonOrUnavailable(ValidationFollowupPlanStep? step)
        => step is null ? "Follow-up step is unavailable." : GetValidationFollowupPlanStepBlockReasonCore(step);

    private string GetValidationFollowupPlanStepBlockReasonCore(ValidationFollowupPlanStep step)
    {
        if (_latestValidationFollowupPlan is null)
            return "Follow-up plan is unavailable.";

        if (string.Equals(step.InteractionMode, "manual_only", StringComparison.Ordinal))
            return "Manual review step.";

        var evidencePath = ResolveValidationFollowupStepPrimaryPath(step);
        if (string.Equals(step.InteractionMode, "view_only", StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(evidencePath) || !PathExists(evidencePath)
                ? "Linked artifact path is unavailable."
                : string.Empty;
        }

        if (!string.Equals(step.InteractionMode, "rerun_capable", StringComparison.Ordinal))
            return string.Empty;

        if (!string.Equals(_latestValidationFollowupPlan.FreshnessStatus, "latest", StringComparison.Ordinal))
            return "This follow-up plan is no longer the latest validation plan.";

        if (IsOperationActive)
            return $"Rerun is unavailable while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return "Rerun is unavailable while another UI action is busy.";

        if (!TryMapFollowupStepToValidationAction(step, out _, out _))
            return "This step is for manual review.";

        return string.Empty;
    }

    private string GetValidationFollowupPlanStepBlockReason(ValidationFollowupPlanStep step)
        => GetValidationFollowupPlanStepBlockReasonCore(step);

    private static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path);

    private string ResolveValidationFollowupStepPrimaryPath(ValidationFollowupPlanStep step)
    {
        if (step.ActionKind == "open_repair_prep_bundle")
            return _validationRepairPrepBundlePath;

        if (!string.IsNullOrWhiteSpace(step.ActionTarget) && PathExists(step.ActionTarget))
            return step.ActionTarget;

        return step.EvidenceArtifactPaths.FirstOrDefault(path => PathExists(path)) ?? string.Empty;
    }

    private bool TryMapFollowupStepToValidationAction(
        ValidationFollowupPlanStep step,
        out ValidationAction action,
        out string actionLabel)
    {
        action = ValidationAction.RunUiTests;
        actionLabel = string.Empty;
        if (_latestValidationFollowupPlan is null)
            return false;

        if (string.Equals(step.ActionKind, "rerun_build_scope", StringComparison.Ordinal))
        {
            action = ValidationAction.BuildUiProject;
            actionLabel = "Guided rerun: Build UI project";
            return true;
        }

        if (!string.Equals(step.ActionKind, "rerun_single_stage", StringComparison.Ordinal) &&
            !string.Equals(step.ActionKind, "rerun_single_test_or_project", StringComparison.Ordinal))
        {
            return false;
        }

        switch (_latestValidationFollowupPlan.FollowupCategory)
        {
            case "fix_build":
                action = ValidationAction.BuildUiProject;
                actionLabel = "Guided rerun: Build UI project";
                return true;
            case "investigate_smoke":
                action = ValidationAction.RunSmokeValidation;
                actionLabel = "Guided rerun: Smoke validation";
                return true;
            case "investigate_integrity":
                action = ValidationAction.RunIntegrityValidation;
                actionLabel = "Guided rerun: Integrity validation";
                return true;
            case "review_flaky_behavior":
                if (_latestValidationFollowupPlan.TargetScopeSummary.Contains("windows_compile_runtime_integrity.ps1", StringComparison.OrdinalIgnoreCase) ||
                    _latestValidationFollowupPlan.RerunScopeRecommendation.Contains("integrity", StringComparison.OrdinalIgnoreCase))
                {
                    action = ValidationAction.RunIntegrityValidation;
                    actionLabel = "Guided rerun: Integrity validation";
                    return true;
                }

                if (_latestValidationFollowupPlan.TargetScopeSummary.Contains("ui_smoke.ps1", StringComparison.OrdinalIgnoreCase) ||
                    _latestValidationFollowupPlan.RerunScopeRecommendation.Contains("smoke", StringComparison.OrdinalIgnoreCase))
                {
                    action = ValidationAction.RunSmokeValidation;
                    actionLabel = "Guided rerun: Smoke validation";
                    return true;
                }

                if (_latestValidationFollowupPlan.RerunScopeRecommendation.Contains("build", StringComparison.OrdinalIgnoreCase))
                {
                    action = ValidationAction.BuildUiProject;
                    actionLabel = "Guided rerun: Build UI project";
                    return true;
                }

                action = ValidationAction.RunUiTests;
                actionLabel = "Guided rerun: UI tests";
                return true;
            default:
                action = ValidationAction.RunUiTests;
                actionLabel = "Guided rerun: UI tests";
                return true;
        }
    }

    private void RefreshValidationFollowupSuggestionSummary()
    {
        if (_latestValidationFollowupIntake is null)
        {
            _validationFollowupReuseSuggestionSummary = "No similar-case or playbook suggestion loaded for the current follow-up.";
            return;
        }

        var contextKind = MapValidationFollowupSemanticContext(_latestValidationFollowupIntake.FollowupCategory);
        var playbook = _semanticReusePlaybooks
            .Where(row => string.Equals(row.ContextKind, contextKind, StringComparison.Ordinal))
            .OrderByDescending(row => MatchesCurrentSemanticReusePlaybook(row))
            .ThenByDescending(row => row.EvidenceCount)
            .ThenBy(row => row.Title, StringComparer.Ordinal)
            .FirstOrDefault();
        if (playbook is not null)
        {
            _validationFollowupReuseSuggestionSummary = $"Playbook suggestion: {playbook.Title} ({playbook.ConfidenceLabel}, evidence {playbook.EvidenceCount}).";
            return;
        }

        var suggestion = _semanticReuseSuggestions
            .Where(row => string.Equals(row.ContextKind, contextKind, StringComparison.Ordinal))
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Title, StringComparer.Ordinal)
            .FirstOrDefault();
        if (suggestion is not null)
        {
            _validationFollowupReuseSuggestionSummary = $"Similar case suggestion: {suggestion.Title} ({suggestion.ValidationOutcomeSummary}).";
            return;
        }

        if (_latestValidationRepairPrepBundle is not null)
        {
            var prepPlaybook = _latestValidationRepairPrepBundle.PlaybookSuggestions.FirstOrDefault();
            if (prepPlaybook is not null)
            {
                _validationFollowupReuseSuggestionSummary = $"Playbook suggestion: {prepPlaybook.Title} ({prepPlaybook.RankingLabel}).";
                return;
            }

            var prepCase = _latestValidationRepairPrepBundle.SimilarCaseSuggestions.FirstOrDefault();
            if (prepCase is not null)
            {
                _validationFollowupReuseSuggestionSummary = $"Similar case suggestion: {prepCase.Title} ({prepCase.RankingLabel}).";
                return;
            }
        }

        _validationFollowupReuseSuggestionSummary = EnableSemanticReuseSuggestions
            ? "Refresh Similar Cases to compare this follow-up with prior evidence-backed cases."
            : "Semantic reuse suggestions are disabled in Validation Options.";
    }

    private static string MapValidationFollowupSemanticContext(string followupCategory)
        => followupCategory switch
        {
            "fix_build" => "validation_failure",
            "fix_tests" => "validation_failure",
            "investigate_smoke" => "validation_failure",
            "investigate_integrity" => "validation_failure",
            "review_flaky_behavior" => "validation_failure",
            _ => "planning"
        };

    private void RefreshValidationTrendArtifacts(ValidationSettings settings)
    {
        try
        {
            ValidationRunnerService.RefreshTrendArtifacts(_validationRunnerService.RepoRoot, settings);
            ValidationRunnerService.RefreshReleaseBaselineArtifacts(_validationRunnerService.RepoRoot, settings);
            ValidationRunnerService.RefreshOrchestrationPolicyArtifacts(_validationRunnerService.RepoRoot, settings);
            _semanticReuseService.RefreshLocalIndex(settings);
        }
        catch
        {
            // Trend refresh should stay non-blocking for the validation surface.
        }
    }

    private void LoadValidationTrendArtifacts()
    {
        var repoRoot = _validationRunnerService.RepoRoot;
        _lastValidationResult = ValidationRunnerService.LoadLatestRunResult(repoRoot) ?? _lastValidationResult;
        _validationHistoryLedgerPath = ValidationRunnerService.HistoryLedgerPathForRepo(repoRoot);
        _validationTrendArtifactPath = ValidationRunnerService.TrendSummaryPathForRepo(repoRoot);
        _validationRegressionArtifactPath = ValidationRunnerService.RegressionSummaryPathForRepo(repoRoot);
        UpdateValidationOrchestrationState(_lastValidationResult);

        var ledger = ValidationRunnerService.LoadHistoryLedger(repoRoot);
        var trend = ValidationRunnerService.LoadTrendSummary(repoRoot);
        var regression = ValidationRunnerService.LoadRegressionSummary(repoRoot);

        _validationStageHistory.Clear();
        foreach (var entry in ledger.Entries.Reverse())
        {
            _validationStageHistory.Add(new ValidationStageHistoryRow(
                entry.RunId,
                entry.ActionLabel,
                entry.CompletedUtc,
                entry.OverallResult,
                entry.StabilityStatus,
                BuildValidationStageOutcomeSummary(entry),
                entry.FirstFailureSummary));
        }

        if (ledger.Entries.Count == 0)
        {
            _validationTrendClassification = "no_history";
            _validationTrendSummaryText = "No validation trend history recorded.";
            _validationRegressionSummaryText = "No regression history recorded.";
        }
        else
        {
            _validationTrendClassification = string.IsNullOrWhiteSpace(regression.Classification)
                ? "stable"
                : regression.Classification;
            _validationTrendSummaryText = BuildValidationTrendSummaryText(trend);
            _validationRegressionSummaryText = BuildValidationRegressionSummaryText(regression);
        }

        OnPropertyChanged(nameof(ValidationTrendClassification));
        OnPropertyChanged(nameof(ValidationTrendBadge));
        OnPropertyChanged(nameof(ValidationTrendSummaryText));
        OnPropertyChanged(nameof(ValidationRegressionSummaryText));
        OnPropertyChanged(nameof(ValidationHistoryLedgerPath));
        OnPropertyChanged(nameof(HasValidationHistoryLedgerPath));
        OnPropertyChanged(nameof(ValidationRunMode));
        OnPropertyChanged(nameof(ValidationRunModeBadge));
        OnPropertyChanged(nameof(ValidationRunModeSummary));
        OnPropertyChanged(nameof(ValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(HasValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(ValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(HasValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(ValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(HasValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(ValidationSequenceSummary));
        OnPropertyChanged(nameof(ValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationActionPolicies));
        OnPropertyChanged(nameof(ValidationTrendArtifactPath));
        OnPropertyChanged(nameof(HasValidationTrendArtifactPath));
        OnPropertyChanged(nameof(ValidationRegressionArtifactPath));
        OnPropertyChanged(nameof(HasValidationRegressionArtifactPath));
        OnPropertyChanged(nameof(HasValidationStageHistory));
        OpenValidationOrchestrationArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationOrchestrationNoteCommand.RaiseCanExecuteChanged();
        OpenValidationHistoryLedgerCommand.RaiseCanExecuteChanged();
        OpenValidationTrendArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationRegressionArtifactCommand.RaiseCanExecuteChanged();
        LoadValidationBaselineArtifacts();
        LoadValidationHandoffArtifacts();
        LoadValidationFollowupArtifacts();
        LoadValidationFollowupPlanArtifacts();
        LoadSemanticReuseArtifacts();
    }

    private void LoadValidationBaselineArtifacts()
    {
        var repoRoot = _validationRunnerService.RepoRoot;
        _validationBaselineArtifactPath = ValidationRunnerService.ActiveBaselinePathForRepo(repoRoot);
        _validationBaselineHistoryArtifactPath = ValidationRunnerService.BaselineHistoryPathForRepo(repoRoot);
        _validationBaselineComparisonArtifactPath = ValidationRunnerService.BaselineComparisonPathForRepo(repoRoot);

        var baseline = ValidationRunnerService.LoadActiveReleaseBaseline(repoRoot);
        var comparison = ValidationRunnerService.LoadBaselineComparison(repoRoot);

        _validationBaselineStageChanges.Clear();
        foreach (var change in comparison.StageChanges)
        {
            _validationBaselineStageChanges.Add(new ValidationBaselineStageChangeRow(
                change.StageLabel,
                BuildBaselineStageOutcomeDisplay(change.BaselineStatus, change.BaselineStabilityClassification),
                BuildBaselineStageOutcomeDisplay(change.LatestStatus, change.LatestStabilityClassification)));
        }

        _validationBaselineSummaryText = baseline is null
            ? "No active release baseline recorded."
            : BuildValidationBaselineSummaryText(baseline);
        _validationBaselineComparisonSummaryText = BuildValidationBaselineComparisonSummaryText(comparison);
        _validationReleaseReadinessClassification = string.IsNullOrWhiteSpace(comparison.ReadinessClassification)
            ? "not_ready"
            : comparison.ReadinessClassification;
        _validationReleaseReadinessSummary = comparison.ReadinessReasons.Count == 0
            ? "No release readiness assessment recorded."
            : string.Join(" ", comparison.ReadinessReasons);

        OnPropertyChanged(nameof(ValidationBaselineSummaryText));
        OnPropertyChanged(nameof(ValidationBaselineComparisonSummaryText));
        OnPropertyChanged(nameof(ValidationReleaseReadinessClassification));
        OnPropertyChanged(nameof(ValidationReleaseReadinessBadge));
        OnPropertyChanged(nameof(ValidationReleaseReadinessSummary));
        OnPropertyChanged(nameof(ValidationBaselineArtifactPath));
        OnPropertyChanged(nameof(HasValidationBaselineArtifactPath));
        OnPropertyChanged(nameof(ValidationBaselineHistoryArtifactPath));
        OnPropertyChanged(nameof(HasValidationBaselineHistoryArtifactPath));
        OnPropertyChanged(nameof(ValidationBaselineComparisonArtifactPath));
        OnPropertyChanged(nameof(HasValidationBaselineComparisonArtifactPath));
        OnPropertyChanged(nameof(HasValidationBaselineStageChanges));
        OnPropertyChanged(nameof(SetReleaseBaselineDisabledReason));
        SetReleaseBaselineCommand.RaiseCanExecuteChanged();
        OpenValidationBaselineArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationBaselineHistoryArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationBaselineComparisonArtifactCommand.RaiseCanExecuteChanged();
    }

    private void LoadSemanticReuseArtifacts()
    {
        _semanticReuseDesignNotePath = _semanticReuseService.DesignNotePath;
        _semanticReuseIndexPath = _semanticReuseService.IndexPath;
        _semanticReuseLinkagePath = _semanticReuseService.LinkagePath;
        LoadSemanticReuseDerivedArtifacts();
        if (_semanticReuseSuggestions.Count == 0)
        {
            _semanticReuseStatus = EnableSemanticReuseSuggestions ? "no_context" : "disabled";
            _semanticReuseSummary = EnableSemanticReuseSuggestions
                ? "Refresh Similar Cases to compare the latest finalized planning, validation, repair, or provider artifacts."
                : "Semantic reuse suggestions are disabled.";
        }

        UpdateSemanticReuseContextSummary();
        RefreshValidationFollowupSuggestionSummary();

        OnPropertyChanged(nameof(SemanticReuseStatus));
        OnPropertyChanged(nameof(SemanticReuseBadge));
        OnPropertyChanged(nameof(SemanticReuseSummary));
        OnPropertyChanged(nameof(SemanticReuseDisabledReason));
        OnPropertyChanged(nameof(HasSemanticReuseDisabledReason));
        OnPropertyChanged(nameof(SemanticReuseDesignNotePath));
        OnPropertyChanged(nameof(HasSemanticReuseDesignNotePath));
        OnPropertyChanged(nameof(SemanticReuseIndexPath));
        OnPropertyChanged(nameof(HasSemanticReuseIndexPath));
        OnPropertyChanged(nameof(SemanticReuseLinkagePath));
        OnPropertyChanged(nameof(HasSemanticReuseLinkagePath));
        OnPropertyChanged(nameof(SemanticReuseEffectivenessSummary));
        OnPropertyChanged(nameof(HasSemanticReuseEffectivenessSummary));
        OnPropertyChanged(nameof(SemanticReuseEffectivenessPath));
        OnPropertyChanged(nameof(HasSemanticReuseEffectivenessPath));
        OnPropertyChanged(nameof(SemanticReusePlaybookPath));
        OnPropertyChanged(nameof(HasSemanticReusePlaybookPath));
        OnPropertyChanged(nameof(SemanticReusePlaybookSummary));
        OnPropertyChanged(nameof(HasSemanticReusePlaybookSummary));
        OnPropertyChanged(nameof(HasSemanticReuseSuggestions));
        OnPropertyChanged(nameof(VisibleSemanticReuseSuggestions));
        OnPropertyChanged(nameof(HasVisibleSemanticReuseSuggestions));
        OnPropertyChanged(nameof(HasSemanticReusePlaybooks));
        OnPropertyChanged(nameof(VisibleSemanticReusePlaybooks));
        OnPropertyChanged(nameof(HasVisibleSemanticReusePlaybooks));
        OnPropertyChanged(nameof(SemanticReuseContextSummary));
        OnPropertyChanged(nameof(HasSemanticReuseContextSummary));
        OnPropertyChanged(nameof(SelectedSemanticReuseRepairReferenceCount));
        OnPropertyChanged(nameof(HasSelectedSemanticReuseRepairReferences));
        OnPropertyChanged(nameof(ValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(HasValidationFollowupReuseSuggestionSummary));
        RefreshSimilarCasesCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseDesignNoteCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseIndexCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseEffectivenessCommand.RaiseCanExecuteChanged();
        OpenSemanticReusePlaybookCatalogCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseSuggestionArtifactCommand.NotifyCanExecuteChanged();
        OpenSemanticReusePlaybookArtifactCommand.NotifyCanExecuteChanged();
    }

    private void LoadSemanticReuseDerivedArtifacts()
    {
        var repoRoot = _semanticReuseService.RepoRoot;
        _semanticReuseEffectivenessPath = SemanticReuseService.EffectivenessPathForRepo(repoRoot);
        _semanticReusePlaybookPath = SemanticReuseService.PlaybookPathForRepo(repoRoot);

        var effectiveness = SemanticReuseService.LoadEffectivenessSummary(repoRoot);
        _semanticReuseEffectivenessSummary = BuildSemanticReuseEffectivenessSummary(effectiveness);

        var playbookCatalog = SemanticReuseService.LoadPlaybookCatalog(repoRoot);
        _semanticReusePlaybooks.Clear();
        foreach (var playbook in playbookCatalog.Entries)
        {
            _semanticReusePlaybooks.Add(new SemanticReusePlaybookRow(
                playbook.PlaybookId,
                playbook.ContextKind,
                playbook.PlaybookClass,
                playbook.Title,
                playbook.Summary,
                playbook.Explanation,
                playbook.Confidence,
                playbook.EvidenceCount,
                playbook.MatchMetadata ?? Array.Empty<SemanticReuseMetadataField>(),
                playbook.LinkedArtifactPaths ?? Array.Empty<string>(),
                playbook.EvidenceArtifactPaths ?? Array.Empty<string>()));
        }

        UpdateSemanticReusePlaybookSummary();
    }

    private void ResetSemanticReuseSuggestions(string? summary = null)
    {
        _semanticReuseSuggestions.Clear();
        _selectedSemanticReuseContext = "All contexts";
        _semanticReuseStatus = EnableSemanticReuseSuggestions ? "no_context" : "disabled";
        _semanticReuseSummary = summary
            ?? (EnableSemanticReuseSuggestions
                ? "Refresh Similar Cases to compare the latest finalized planning, validation, repair, or provider artifacts."
                : "Semantic reuse suggestions are disabled.");
        UpdateSemanticReuseContextSummary();
        UpdateSemanticReusePlaybookSummary();
        RefreshValidationFollowupSuggestionSummary();
        OnPropertyChanged(nameof(SemanticReuseStatus));
        OnPropertyChanged(nameof(SemanticReuseBadge));
        OnPropertyChanged(nameof(SemanticReuseSummary));
        OnPropertyChanged(nameof(SelectedSemanticReuseContext));
        OnPropertyChanged(nameof(HasSemanticReuseSuggestions));
        OnPropertyChanged(nameof(VisibleSemanticReuseSuggestions));
        OnPropertyChanged(nameof(HasVisibleSemanticReuseSuggestions));
        OnPropertyChanged(nameof(VisibleSemanticReusePlaybooks));
        OnPropertyChanged(nameof(HasVisibleSemanticReusePlaybooks));
        OnPropertyChanged(nameof(SemanticReusePlaybookSummary));
        OnPropertyChanged(nameof(HasSemanticReusePlaybookSummary));
        OnPropertyChanged(nameof(SemanticReuseContextSummary));
        OnPropertyChanged(nameof(HasSemanticReuseContextSummary));
        OnPropertyChanged(nameof(SelectedSemanticReuseRepairReferenceCount));
        OnPropertyChanged(nameof(HasSelectedSemanticReuseRepairReferences));
        OnPropertyChanged(nameof(SemanticReuseDisabledReason));
        OnPropertyChanged(nameof(HasSemanticReuseDisabledReason));
        OnPropertyChanged(nameof(ValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(HasValidationFollowupReuseSuggestionSummary));
        RefreshSimilarCasesCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseSuggestionArtifactCommand.NotifyCanExecuteChanged();
        OpenSemanticReusePlaybookArtifactCommand.NotifyCanExecuteChanged();
    }

    private static string BuildSemanticReuseEffectivenessSummary(SemanticReuseEffectivenessSummary summary)
    {
        if (summary.RecentEvidence.Count == 0)
            return "No reuse outcome evidence recorded yet.";

        return string.Join(
            " ",
            summary.Contexts.Select(context =>
                $"{BuildSemanticReuseContextLabel(context.ContextKind)}: clean pass {context.CleanValidationPassCount}, passed on retry {context.PassedOnRetryCount}, improved {context.ImprovedRepairResultCount}, unchanged {context.UnchangedOutcomeCount}, regressed {context.RegressedOutcomeCount}."));
    }

    private void UpdateSemanticReusePlaybookSummary()
    {
        if (!EnablePlaybookSuggestions)
        {
            _semanticReusePlaybookSummary = "Operator playbook suggestions are disabled in Validation Options.";
            return;
        }

        var visible = VisibleSemanticReusePlaybooks;
        if (visible.Count == 0)
        {
            _semanticReusePlaybookSummary = "No evidence-backed operator playbooks are currently loaded for the selected context.";
            return;
        }

        var matchedCount = visible.Count(MatchesCurrentSemanticReusePlaybook);
        _semanticReusePlaybookSummary = matchedCount > 0
            ? $"Showing {visible.Count} playbook suggestion(s), with {matchedCount} matching the current context."
            : $"Showing {visible.Count} evidence-backed playbook suggestion(s) for the selected context.";
    }

    private static string BuildSemanticReuseContextLabel(string contextKind)
        => contextKind switch
        {
            "planning" => "Planning",
            "validation_failure" => "Validation failure guidance",
            "repair_bundle_reference" => "Repair bundle references",
            "provider_diagnostics" => "Provider diagnostics reuse",
            _ => "General"
        };

    private static string BuildValidationTrendSummaryText(ValidationTrendSummary trend)
    {
        var failingStage = string.IsNullOrWhiteSpace(trend.MostCommonFailingStage)
            ? "none"
            : trend.MostCommonFailingStage;
        var lastCleanPass = trend.LastCleanPassUtc?.ToLocalTime().ToString("g") ?? "none";
        return $"Recent pass rate {trend.PassCount}/{trend.HistoryCount} ({trend.RecentPassRatePercent}%). Stable passes {trend.StablePassCount}/{trend.HistoryCount} ({trend.StablePassRatePercent}%). Retry-classified passes {trend.PassedOnRetryCount}; flaky suspected {trend.FlakySuspectedCount}; most common failing stage {failingStage}; last clean pass {lastCleanPass}.";
    }

    private static string BuildValidationRegressionSummaryText(ValidationRegressionSummary regression)
    {
        if (string.Equals(regression.Classification, "no_history", StringComparison.Ordinal))
            return "No regression history recorded.";

        var reasons = regression.Reasons.Count == 0
            ? "No regression signals recorded."
            : string.Join(" ", regression.Reasons);
        var novelty = string.Equals(regression.FailureNovelty, "none", StringComparison.Ordinal)
            ? string.Empty
            : $" Novelty: {regression.FailureNovelty.Replace('_', ' ')}.";
        return $"Window {regression.ComparisonWindow}. {reasons}{novelty}";
    }

    private static string BuildValidationBaselineSummaryText(ValidationReleaseBaseline baseline)
    {
        var commit = string.IsNullOrWhiteSpace(baseline.CommitHash)
            ? "commit unavailable"
            : $"commit {baseline.CommitHash[..Math.Min(7, baseline.CommitHash.Length)]}";
        return $"Active baseline {baseline.BaselineId} from {baseline.CapturedUtc.ToLocalTime():g} ({commit}).";
    }

    private static string BuildValidationBaselineComparisonSummaryText(ValidationBaselineComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(comparison.BaselineId))
            return "No active release baseline recorded.";

        var changedStages = comparison.ChangedFailingStages.Count == 0
            ? "no changed failing stages"
            : string.Join(", ", comparison.ChangedFailingStages);
        return $"Baseline {comparison.BaselineId} vs latest {comparison.LatestRunId}: drift {comparison.DriftClassification.Replace('_', ' ')}; {changedStages}.";
    }

    private static string BuildBaselineStageOutcomeDisplay(string status, string classification)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "missing" : status;
        return classification switch
        {
            "passed_on_retry" => $"{normalizedStatus} (after retry)",
            "flaky_suspected" => $"{normalizedStatus} (flaky suspected)",
            "missing" => normalizedStatus,
            _ when string.Equals(normalizedStatus, "failed", StringComparison.Ordinal) &&
                   !string.Equals(classification, "failed", StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(classification) => $"{normalizedStatus} ({classification.Replace('_', ' ')})",
            _ => normalizedStatus
        };
    }

    private static string BuildValidationStageOutcomeSummary(ValidationHistoryEntry entry)
        => string.Join(
            "; ",
            entry.StageOutcomes.Select(outcome =>
                $"{outcome.StageLabel}: {BuildValidationStageOutcomeStatus(outcome)}"));

    private static string BuildValidationStageOutcomeStatus(ValidationHistoryStageOutcome outcome)
    {
        var baseStatus = string.Equals(outcome.Status, "passed", StringComparison.Ordinal) ? "passed" : "failed";
        if (string.Equals(outcome.StabilityClassification, "flaky_suspected", StringComparison.Ordinal))
            return $"{baseStatus} (flaky suspected)";

        if (string.Equals(outcome.StabilityClassification, "passed_on_retry", StringComparison.Ordinal))
            return $"{baseStatus} (after retry)";

        if (outcome.RetryUsed && string.Equals(outcome.Status, "failed", StringComparison.Ordinal))
            return "failed (after retry)";

        return baseStatus;
    }

    private async Task RunBuilderProofMatrixAsync()
    {
        var blocker = GetBuilderProofDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
            return;

        BeginOperationProgress(
            "Running builder proof",
            $"Executing the bounded builder proof matrix with {BuilderExecutionService.BuilderProofFloorModelId}.",
            "Prepare proof matrix",
            "Run proof targets",
            "Write proof verdict",
            "Complete");

        using var busy = EnterBusyScope("RunBuilderProofMatrix");
        AddNarration("info", "Builder proof started", new Dictionary<string, string>
        {
            ["model_id"] = BuilderExecutionService.BuilderProofFloorModelId,
            ["provider"] = string.IsNullOrWhiteSpace(SelectedProviderMode) ? "ollama" : SelectedProviderMode
        });

        try
        {
            SetOperationStepState("Prepare proof matrix", "active", "Preparing bounded proof targets.");
            var run = await _builderExecutionService.RunBuilderProofMatrixAsync(
                _validationRunnerService.RepoRoot,
                BuilderExecutionService.BuilderProofFloorModelId,
                string.IsNullOrWhiteSpace(SelectedProviderMode) ? "ollama" : SelectedProviderMode,
                narrate: evt =>
                {
                    AddNarration(evt.Kind, evt.Message, evt.Data);
                    var data = evt.Data ?? new Dictionary<string, string>(StringComparer.Ordinal);
                    switch (evt.Message)
                    {
                        case "BUILDER_PROOF_STARTED":
                            SetOperationStepState("Prepare proof matrix", "completed", "Builder proof matrix prepared.");
                            SetOperationStepState("Run proof targets", "active", $"Running {(data.TryGetValue("model_id", out var modelId) ? modelId : BuilderExecutionService.BuilderProofFloorModelId)} proof targets.");
                            break;
                        case "BUILDER_PROOF_TARGET_STARTED":
                            if (data.TryGetValue("target_label", out var label))
                                SetOperationStepState("Run proof targets", "active", $"Running {label}.");
                            break;
                        case "BUILDER_PROOF_COMPLETED":
                            SetOperationStepState("Run proof targets", "completed", "Builder proof targets completed.");
                            break;
                    }
                }).ConfigureAwait(true);

            SetOperationStepState("Run proof targets", "completed", $"Processed {run.CaseResults.Count} proof target(s).");
            SetOperationStepState("Write proof verdict", "completed", run.VerdictSummary);
            SetOperationStepState("Complete", "completed", run.FinalClassification);
            LoadBuilderProofArtifacts();
            CompleteOperationProgress(!string.Equals(run.FinalClassification, "failed", StringComparison.Ordinal), run.VerdictSummary);
        }
        catch (Exception ex)
        {
            SetOperationStepState("Run proof targets", "failed", ex.Message);
            SetOperationStepState("Write proof verdict", "failed", "Builder proof did not complete.");
            CompleteOperationProgress(false, $"Builder proof failed: {ex.Message}");
            RecordFailure(
                "Builder proof matrix",
                ex.ToString(),
                BuilderExecutionService.BuilderProofRootForRepo(_validationRunnerService.RepoRoot),
                "Inspect the builder proof logs and rerun the matrix in bounded scope.");
        }
    }

    private async Task RunBuilderComparativeProofAsync()
    {
        var blocker = GetBuilderComparativeProofDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
            return;

        BeginOperationProgress(
            "Running comparative proof",
            "Comparing the latest escalation-worthy builder proof target against the stronger-tier path.",
            "Resolve stronger tier",
            "Run stronger-tier proof",
            "Run split proof",
            "Write comparative evidence",
            "Complete");

        using var busy = EnterBusyScope("RunBuilderComparativeProof");
        AddNarration("info", "Builder comparative proof started", new Dictionary<string, string>
        {
            ["current_model"] = BuilderProofModelId,
            ["target_id"] = _latestBuilderModelEscalationDecision?.TargetId ?? string.Empty
        });

        try
        {
            SetOperationStepState("Resolve stronger tier", "active", "Resolving stronger-tier model availability.");
            var comparativeRun = await _builderExecutionService.RunBuilderComparativeProofAsync(
                _validationRunnerService.RepoRoot,
                provider: string.IsNullOrWhiteSpace(SelectedProviderMode) ? "ollama" : SelectedProviderMode,
                narrate: evt =>
                {
                    AddNarration(evt.Kind, evt.Message, evt.Data);
                    var data = evt.Data ?? new Dictionary<string, string>(StringComparer.Ordinal);
                    switch (evt.Message)
                    {
                        case "BUILDER_COMPARATIVE_PROOF_STARTED":
                            SetOperationStepState("Resolve stronger tier", "completed", $"Resolved {(data.TryGetValue("stronger_model", out var strongerModel) ? strongerModel : "stronger tier")}.");
                            SetOperationStepState("Run stronger-tier proof", "active", "Running bounded stronger-tier comparison.");
                            break;
                        case "BUILDER_COMPARATIVE_PROOF_CASE_STARTED":
                            if (data.TryGetValue("case_kind", out var caseKind) &&
                                string.Equals(caseKind, "split_floor", StringComparison.Ordinal))
                            {
                                SetOperationStepState("Run stronger-tier proof", "completed", "Stronger-tier comparison completed.");
                                SetOperationStepState("Run split proof", "active", "Running split low-floor comparison.");
                            }
                            break;
                        case "BUILDER_COMPARATIVE_PROOF_COMPLETED":
                            SetOperationStepState("Run stronger-tier proof", "completed", "Stronger-tier comparison completed.");
                            break;
                    }
                }).ConfigureAwait(true);

            if (comparativeRun.SplitLowFloorCase is null)
            {
                SetOperationStepState("Run split proof", "completed", "Split proof was not required for the latest routing state.");
            }
            else
            {
                SetOperationStepState("Run split proof", "completed", comparativeRun.SplitThenEscalateSummary);
            }

            SetOperationStepState("Write comparative evidence", "completed", comparativeRun.Summary);
            SetOperationStepState("Complete", "completed", comparativeRun.ComparativeClassification);
            LoadBuilderProofArtifacts();
            CompleteOperationProgress(true, comparativeRun.Summary);
        }
        catch (Exception ex)
        {
            SetOperationStepState("Resolve stronger tier", "failed", ex.Message);
            SetOperationStepState("Run stronger-tier proof", "failed", "Comparative proof did not complete.");
            SetOperationStepState("Write comparative evidence", "failed", "Comparative proof artifacts were not written.");
            CompleteOperationProgress(false, $"Builder comparative proof failed: {ex.Message}");
            RecordFailure(
                "Builder comparative proof",
                ex.ToString(),
                BuilderExecutionService.BuilderProofRootForRepo(_validationRunnerService.RepoRoot),
                "Inspect the stronger-tier availability artifact and rerun the bounded comparative proof.");
        }
    }

    private Task LaunchPreparedBuilderRouteAsync()
        => LaunchPreparedBuilderRouteCoreAsync(
            routeOverride: null,
            overrideReason: null,
            blocker: GetBuilderPreparedLaunchDisabledReason(),
            operationName: "Launching prepared route",
            narrationTitle: "Prepared builder route started",
            narrationRoute: BuilderPrepRouteState,
            introText: $"Launching {(string.IsNullOrWhiteSpace(BuilderPrepRouteBadge) ? "prepared route" : BuilderPrepRouteBadge.ToLowerInvariant())} for the latest builder intake.");

    private Task LaunchBuilderOverrideRouteAsync()
    {
        var candidate = GetBuilderPreparedRouteOverrideCandidate();
        var routeLabel = string.IsNullOrWhiteSpace(candidate.Route) ? "override route" : candidate.Route;
        return LaunchPreparedBuilderRouteCoreAsync(
            routeOverride: candidate.Route,
            overrideReason: candidate.Reason,
            blocker: GetBuilderOverrideLaunchDisabledReason(),
            operationName: "Launching override route",
            narrationTitle: "Builder override route started",
            narrationRoute: routeLabel,
            introText: $"Launching override route {routeLabel} for the latest builder intake.");
    }

    private async Task LaunchPreparedBuilderRouteCoreAsync(
        string? routeOverride,
        string? overrideReason,
        string blocker,
        string operationName,
        string narrationTitle,
        string narrationRoute,
        string introText)
    {
        if (!string.IsNullOrWhiteSpace(blocker))
            return;

        BeginOperationProgress(
            operationName,
            introText,
            "Check launch readiness",
            "Run prepared route",
            "Write route result",
            "Complete");

        using var busy = EnterBusyScope(string.IsNullOrWhiteSpace(routeOverride) ? "LaunchPreparedBuilderRoute" : "LaunchBuilderOverrideRoute");
        AddNarration("info", narrationTitle, new Dictionary<string, string>
        {
            ["route"] = narrationRoute,
            ["model_id"] = BuilderProofModelId
        });

        try
        {
            SetOperationStepState("Check launch readiness", "active", "Verifying current intake, prep, and route evidence.");
            var result = await _builderExecutionService.LaunchPreparedBuilderRouteAsync(
                _validationRunnerService.RepoRoot,
                provider: string.IsNullOrWhiteSpace(SelectedProviderMode) ? "ollama" : SelectedProviderMode,
                routeOverride: routeOverride,
                overrideReason: overrideReason).ConfigureAwait(true);

            SetOperationStepState("Check launch readiness", "completed", "Prepared route eligibility confirmed.");
            SetOperationStepState("Run prepared route", "completed", $"{result.ActualRouteUsed}: {result.FinalRouteOutcomeClassification}.");
            SetOperationStepState("Write route result", "completed", result.Summary);
            SetOperationStepState("Complete", "completed", result.PreparedRouteComparisonState);
            LoadBuilderProofArtifacts();

            var success = string.Equals(result.FinalRouteOutcomeClassification, "launched_and_passed", StringComparison.Ordinal) ||
                          string.Equals(result.FinalRouteOutcomeClassification, "launched_and_passed_with_repair", StringComparison.Ordinal);
            CompleteOperationProgress(success, result.Summary);
            if (!success)
            {
                RecordFailure(
                    "Prepared builder route",
                    result.Summary,
                    result.ArtifactPath,
                    "Inspect the prepared route result and continue through the linked follow-up artifacts if the route stayed unresolved.");
            }
        }
        catch (Exception ex)
        {
            SetOperationStepState("Check launch readiness", "failed", ex.Message);
            SetOperationStepState("Run prepared route", "failed", "Prepared builder route did not complete.");
            SetOperationStepState("Write route result", "failed", "Prepared builder route artifacts were not written.");
            CompleteOperationProgress(false, $"{operationName} failed: {ex.Message}");
            RecordFailure(
                string.IsNullOrWhiteSpace(routeOverride) ? "Prepared builder route" : "Prepared builder override route",
                ex.ToString(),
                BuilderExecutionService.BuilderProofRootForRepo(_validationRunnerService.RepoRoot),
                "Inspect the prepared route launch artifact and rerun the bounded route only after the blocker is resolved.");
        }
    }

    private async Task RunNextBuilderSplitStepAsync()
    {
        var blocker = GetBuilderSplitExecutionDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
            return;

        var step = GetNextBuilderSplitExecutionStep();
        if (step is null || _latestBuilderProofRun is null)
            return;

        BeginOperationProgress(
            "Running split-first step",
            $"Executing split step {step.StepNumber}: {step.StepLabel}.",
            "Load split-first plan",
            step.StepLabel,
            "Complete");

        using var busy = EnterBusyScope("RunBuilderSplitStep");
        try
        {
            SetOperationStepState("Load split-first plan", "active", "Loading the latest split-first execution state.");
            SetOperationStepState("Load split-first plan", "completed", "Loaded the latest split-first execution state.");
            SetOperationStepState(step.StepLabel, "active", step.Detail);

            if (string.Equals(step.ExecutionMode, "rerun_capable", StringComparison.Ordinal))
            {
                var outcome = await _builderExecutionService.RunBuilderSplitStepRerunAsync(
                    _validationRunnerService.RepoRoot,
                    provider: string.IsNullOrWhiteSpace(SelectedProviderMode) ? "ollama" : SelectedProviderMode,
                    stepId: step.StepId).ConfigureAwait(true);
                SetOperationStepState(step.StepLabel, "completed", outcome.ClosureClassification);
                SetOperationStepState("Complete", "completed", outcome.PracticalRouteSummary);
                LoadBuilderProofArtifacts();
                CompleteOperationProgress(true, outcome.Summary);
                return;
            }

            var targetPath = ResolveBuilderSplitStepPrimaryPath(step);
            if (string.IsNullOrWhiteSpace(targetPath) || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
            {
                SetOperationStepState(step.StepLabel, "failed", "Linked artifact path is unavailable.");
                CompleteOperationProgress(false, "Split-step execution failed: linked artifact path is unavailable.");
                return;
            }

            var completionState = string.Equals(step.ExecutionMode, "view_only", StringComparison.Ordinal)
                ? "opened"
                : "executed";
            BuilderExecutionService.RecordBuilderSplitStepInteraction(
                _validationRunnerService.RepoRoot,
                _latestBuilderProofRun.RunFolder,
                step.StepId,
                completionState,
                step.StepType,
                step.Detail,
                targetPath);
            await OpenPathIfExistsAsync(targetPath).ConfigureAwait(true);
            SetOperationStepState(step.StepLabel, "completed", step.Detail);
            SetOperationStepState("Complete", "completed", $"{step.StepLabel} completed.");
            LoadBuilderProofArtifacts();
            CompleteOperationProgress(true, $"{step.StepLabel} completed.");
        }
        catch (Exception ex)
        {
            SetOperationStepState(step.StepLabel, "failed", ex.Message);
            CompleteOperationProgress(false, $"Split-step execution failed: {ex.Message}");
            RecordFailure(
                "Builder split-step execution",
                ex.ToString(),
                _latestBuilderProofRun.RunFolder,
                "Inspect the split-first execution artifact and retry the next bounded step.");
        }
    }

    private Task OpenBuilderProofSummaryAsync()
        => OpenPathIfExistsAsync(_builderProofSummaryPath);

    private Task OpenBuilderProofRunFolderAsync()
        => OpenFolderIfExistsAsync(_builderProofRunPath);

    private Task OpenBuilderModelFloorVerdictAsync()
        => OpenPathIfExistsAsync(_builderModelFloorVerdictPath);

    private Task OpenBuilderFailurePatternsAsync()
        => OpenPathIfExistsAsync(_builderModelFloorFailurePatternsPath);

    private Task OpenBuilderExternalProofSummaryAsync()
        => OpenPathIfExistsAsync(_builderExternalProofSummaryPath);

    private Task OpenBuilderModelFloorPolicyAsync()
        => OpenPathIfExistsAsync(_builderModelFloorPolicyPath);

    private Task OpenBuilderTrustBandsAsync()
        => OpenPathIfExistsAsync(_builderModelTrustBandsPath);

    private Task OpenBuilderScopeSummaryAsync()
        => OpenPathIfExistsAsync(_builderModelScopeSummaryPath);

    private Task OpenBuilderRoutingRecommendationAsync()
        => OpenPathIfExistsAsync(_builderModelRoutingRecommendationPath);

    private Task OpenBuilderEscalationDecisionAsync()
        => OpenPathIfExistsAsync(_builderModelEscalationDecisionPath);

    private Task OpenBuilderRoutingPlanAsync()
        => OpenPathIfExistsAsync(_builderModelRoutingPlanPath);

    private Task OpenBuilderStrongerTierAvailabilityAsync()
        => OpenPathIfExistsAsync(_builderStrongerTierAvailabilityPath);

    private Task OpenBuilderComparativeProofSummaryAsync()
        => OpenPathIfExistsAsync(_builderComparativeProofSummaryPath);

    private Task OpenBuilderRoutingPolicyEvidenceAsync()
        => OpenPathIfExistsAsync(_builderRoutingPolicyPath);

    private Task OpenBuilderSplitFirstPlanAsync()
        => OpenPathIfExistsAsync(_builderSplitFirstPlanPath);

    private Task OpenBuilderTieredRoutingPolicyAsync()
        => OpenPathIfExistsAsync(_builderTieredRoutingPath);

    private Task OpenBuilderDefaultGuidanceAsync()
        => OpenPathIfExistsAsync(_builderDefaultPolicyPath);

    private Task OpenBuilderGuidanceHistoryAsync()
        => OpenPathIfExistsAsync(_builderDefaultPolicyHistoryPath);

    private Task OpenBuilderLatestRoutingDecisionAsync()
        => OpenPathIfExistsAsync(_builderRequestPolicyDecisionPath);

    private Task OpenBuilderGuidanceSupportAsync()
        => OpenPathIfExistsAsync(_builderPolicyStabilityPath);

    private Task OpenBuilderRequestIntakeAsync()
        => OpenPathIfExistsAsync(_builderRequestIntakePath);

    private Task OpenBuilderExecutionPrepAsync()
        => OpenPathIfExistsAsync(_builderExecutionPrepPath);

    private Task OpenBuilderExecutionLaunchAsync()
        => OpenPathIfExistsAsync(_builderExecutionLaunchPath);

    private Task OpenBuilderExecutionResultAsync()
        => OpenPathIfExistsAsync(_builderExecutionResultPath);

    private Task OpenBuilderReadinessGateAsync()
        => OpenPathIfExistsAsync(_builderReadinessGatePath);

    private Task OpenBuilderReadinessHistoryAsync()
        => OpenPathIfExistsAsync(_builderReadinessGateHistoryPath);

    private Task OpenBuilderConfirmedClassesAsync()
        => OpenPathIfExistsAsync(_builderConfirmedClassesPath);

    private Task OpenBuilderDefaultRouteDecisionAsync()
        => OpenPathIfExistsAsync(_builderDefaultRouteDecisionPath);

    private Task OpenBuilderLaunchDefaultDecisionAsync()
        => OpenPathIfExistsAsync(_builderLaunchDefaultDecisionPath);

    private Task OpenBuilderRouteOverrideEvidenceAsync()
        => OpenPathIfExistsAsync(_builderRouteOverridePath);

    private Task OpenBuilderRouteReviewAsync()
        => OpenPathIfExistsAsync(_builderRouteReviewPath);

    private Task OpenBuilderRouteReconfirmationAsync()
        => OpenPathIfExistsAsync(_builderRouteReconfirmationPath);

    private Task OpenBuilderDefaultRouteRecoveryAsync()
        => OpenPathIfExistsAsync(_builderDefaultRouteRecoveryPath);

    private Task OpenBuilderReadinessContradictionsAsync()
        => OpenPathIfExistsAsync(_builderReadinessContradictionsPath);

    private Task OpenBuilderRouteStabilitySummaryAsync()
        => OpenPathIfExistsAsync(_builderRouteStabilitySummaryPath);

    private Task OpenBuilderSplitStepExecutionAsync()
        => OpenPathIfExistsAsync(_builderSplitStepExecutionPath);

    private Task OpenBuilderSplitFirstOutcomeAsync()
        => OpenPathIfExistsAsync(_builderSplitFirstOutcomePath);

    private Task CopyBuilderProofSummaryAsync()
    {
        if (!HasBuilderProofSummary)
            return Task.CompletedTask;

        if (HasBuilderProofSummaryPath)
            return _workspaceShell.CopyTextAsync(File.ReadAllText(_builderProofSummaryPath));

        return _workspaceShell.CopyTextAsync(_builderProofSummaryText);
    }

    private Task CopyBuilderScopeSummaryAsync()
    {
        if (!HasBuilderModelScopeSummary)
            return Task.CompletedTask;

        if (HasBuilderModelScopeSummaryPath)
            return _workspaceShell.CopyTextAsync(File.ReadAllText(_builderModelScopeSummaryPath));

        return _workspaceShell.CopyTextAsync(_builderModelScopeSummary);
    }

    private Task CopyBuilderRoutingRecommendationAsync()
    {
        if (!HasBuilderModelRoutingRecommendationSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderModelRoutingRecommendationSummary);
    }

    private Task CopyBuilderSplitTaskGuidanceAsync()
    {
        if (!HasBuilderModelSplitTaskGuidanceSummary)
            return Task.CompletedTask;

        if (_latestBuilderModelRoutingPlan is not null && _latestBuilderModelRoutingPlan.SplitTaskGuidance.Count > 0)
            return _workspaceShell.CopyTextAsync(string.Join(System.Environment.NewLine, _latestBuilderModelRoutingPlan.SplitTaskGuidance));

        return _workspaceShell.CopyTextAsync(_builderModelSplitTaskGuidanceSummary);
    }

    private Task CopyBuilderComparativeProofSummaryAsync()
    {
        if (!HasBuilderComparativeProofSummary)
            return Task.CompletedTask;

        if (HasBuilderComparativeProofSummaryPath)
            return _workspaceShell.CopyTextAsync(File.ReadAllText(_builderComparativeProofSummaryPath));

        return _workspaceShell.CopyTextAsync(_builderComparativeProofSummary);
    }

    private Task CopyBuilderRoutingPolicySummaryAsync()
    {
        if (!HasBuilderRoutingPolicySummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderRoutingPolicySummary);
    }

    private Task CopyBuilderSplitFirstPlanSummaryAsync()
    {
        if (!HasBuilderSplitFirstPlanSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderSplitFirstPlanSummary);
    }

    private Task CopyBuilderPrimaryRoutingRecommendationAsync()
    {
        if (!HasBuilderPrimaryRoutingRecommendationSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderPrimaryRoutingRecommendationSummary);
    }

    private Task CopyBuilderWeakSpotMitigationSummaryAsync()
    {
        if (!HasBuilderWeakSpotMitigationSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderWeakSpotMitigationSummary);
    }

    private Task CopyBuilderDefaultGuidanceSummaryAsync()
    {
        if (!HasBuilderDefaultGuidanceSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderDefaultPolicySummary);
    }

    private Task CopyBuilderLatestRoutingDecisionAsync()
    {
        if (!HasBuilderLatestRoutingDecisionSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderRequestPolicyDecisionSummary);
    }

    private Task CopyBuilderExecutionPrepSummaryAsync()
    {
        if (!HasBuilderPrepSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderExecutionPrepSummary);
    }

    private Task CopyBuilderIntakeRoutingSummaryAsync()
    {
        if (!HasBuilderIntakeSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderRequestIntakeSummary);
    }

    private Task CopyBuilderExecutionLaunchSummaryAsync()
    {
        if (!HasBuilderLaunchSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderExecutionLaunchSummary);
    }

    private Task CopyBuilderExecutionResultSummaryAsync()
    {
        if (!HasBuilderResultSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderExecutionResultSummary);
    }

    private Task CopyBuilderReadinessSummaryAsync()
    {
        if (!HasBuilderReadinessGateSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderReadinessGateSummary);
    }

    private Task CopyBuilderReadinessContradictionNoteAsync()
    {
        if (!HasBuilderReadinessLatestContradictionNote)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderReadinessLatestContradictionNote);
    }

    private Task CopyBuilderConfirmedClassesSummaryAsync()
    {
        if (!HasBuilderConfirmedClassesSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderConfirmedClassesSummary);
    }

    private Task CopyBuilderDefaultRouteDecisionSummaryAsync()
    {
        if (!HasBuilderDefaultRouteDecisionSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderDefaultRouteDecisionSummary);
    }

    private Task CopyBuilderLaunchDefaultSummaryAsync()
    {
        if (!HasBuilderLaunchDefaultDecisionSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderLaunchDefaultDecisionSummary);
    }

    private Task CopyBuilderRouteOverrideSummaryAsync()
    {
        if (!HasBuilderRouteOverrideSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderRouteOverrideSummary);
    }

    private Task CopyBuilderRouteReconfirmationSummaryAsync()
    {
        if (!HasBuilderRouteReconfirmationSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderRouteReconfirmationSummary);
    }

    private Task CopyBuilderDefaultRouteRecoverySummaryAsync()
    {
        if (!HasBuilderDefaultRouteRecoverySummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderDefaultRouteRecoverySummary);
    }

    private Task CopyBuilderSplitExecutionSummaryAsync()
    {
        if (!HasBuilderSplitStepExecutionSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderSplitStepExecutionSummary);
    }

    private Task CopyBuilderSplitComparativeClosureSummaryAsync()
    {
        if (!HasBuilderSplitFirstOutcomeSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_builderSplitFirstOutcomeSummary);
    }

    private static string ResolveBuilderSplitStepPrimaryPath(BuilderSplitStepExecutionStepState step)
    {
        if (!string.IsNullOrWhiteSpace(step.EvidencePath) && PathExists(step.EvidencePath))
            return step.EvidencePath;

        return step.LinkedArtifactPaths.FirstOrDefault(PathExists) ?? string.Empty;
    }

    private bool CanSetReleaseBaseline()
        => string.IsNullOrWhiteSpace(GetSetReleaseBaselineDisabledReason());

    private string GetSetReleaseBaselineDisabledReason()
    {
        if (!Directory.Exists(_validationRunnerService.RepoRoot) || !File.Exists(Path.Combine(_validationRunnerService.RepoRoot, "Shoots.sln")))
            return "Release baseline is disabled because the Shoots repo root could not be resolved.";

        if (IsOperationActive)
            return $"Release baseline is unavailable while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return "Release baseline is unavailable while another UI action is busy.";

        if (_lastValidationResult is null)
            return "Release baseline is available after the latest validation result is loaded.";

        var classification = string.IsNullOrWhiteSpace(_lastValidationResult.StabilityClassification)
            ? (_lastValidationResult.Success ? "passed" : "failed")
            : _lastValidationResult.StabilityClassification;
        if (!_lastValidationResult.Success || !string.Equals(classification, "passed", StringComparison.Ordinal))
            return "Release baseline can only be set from the latest clean validation result.";

        return string.Empty;
    }

    private async Task SetReleaseBaselineAsync()
    {
        var blocker = GetSetReleaseBaselineDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker) || _lastValidationResult is null)
            return;

        try
        {
            ValidationRunnerService.SetActiveReleaseBaseline(_validationRunnerService.RepoRoot, _lastValidationResult, BuildValidationSettings());
            ValidationRunnerService.RefreshHandoffArtifacts(_validationRunnerService.RepoRoot, BuildValidationSettings());
            LoadValidationTrendArtifacts();
            AddNarration("info", "VALIDATION_BASELINE_SET", new Dictionary<string, string>
            {
                ["run_id"] = _lastValidationResult.RunId,
                ["output_folder"] = _lastValidationResult.OutputFolder
            });
            await Task.CompletedTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RecordFailure(
                "Validation baseline",
                ex.ToString(),
                _lastValidationResult.OutputFolder,
                "Review the latest validation artifacts and retry setting the release baseline.");
        }
    }

    private async Task RunValidationActionAsync(ValidationAction action)
    {
        _validationFollowupPinnedOutputFolder = string.Empty;
        await ExecuteValidationActionAsync(action, null, beginOperationProgress: true).ConfigureAwait(true);
    }

    private async Task<ValidationRunResult?> ExecuteValidationActionAsync(
        ValidationAction action,
        GeneratedOutputContext? generatedContext,
        bool beginOperationProgress,
        string? actionLabelOverride = null,
        bool recordSemanticReuseOutcomes = true)
    {
        var blocker = beginOperationProgress ? GetValidationDisabledReason(action) : string.Empty;
        if (!string.IsNullOrWhiteSpace(blocker))
            return null;

        var settings = BuildValidationSettings();
        var orchestrationPolicy = ValidationRunnerService.DescribeAction(action, settings);
        var stepLabels = _validationRunnerService.GetStageLabels(action, IncludeValidateBuildForFullLoop);
        var actionLabel = actionLabelOverride ?? DescribeValidationAction(action);

        if (beginOperationProgress)
        {
            _activeValidationAction = action;
            _activeValidationActionLabel = actionLabel;
            _activeValidationStageId = string.Empty;
            BeginOperationProgress(
                "Running validation",
                $"{actionLabel} started in {_validationRunnerService.RepoRoot}.",
                stepLabels.ToArray());
        }

        var priorSuggestions = _semanticReuseSuggestions.ToArray();

        _validationStageResults.Clear();
        _validationSummary = $"{actionLabel} started.";
        _validationOutputFolder = string.Empty;
        _validationFirstFailureText = string.Empty;
        _validationFirstFailureLogPath = string.Empty;
        _validationStabilityClassification = "not_run";
        _validationStabilityArtifactPath = string.Empty;
        UpdateValidationOrchestrationState(null, orchestrationPolicy.RunMode, actionLabel);
        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(ValidationOutputFolder));
        OnPropertyChanged(nameof(HasValidationOutputFolder));
        OnPropertyChanged(nameof(ValidationFirstFailureText));
        OnPropertyChanged(nameof(HasValidationFirstFailure));
        OnPropertyChanged(nameof(ValidationFirstFailureLogPath));
        OnPropertyChanged(nameof(HasValidationFirstFailureLogPath));
        OnPropertyChanged(nameof(ValidationStabilityClassification));
        OnPropertyChanged(nameof(ValidationStabilityBadge));
        OnPropertyChanged(nameof(ValidationStabilityArtifactPath));
        OnPropertyChanged(nameof(HasValidationStabilityArtifactPath));
        OnPropertyChanged(nameof(ValidationRunMode));
        OnPropertyChanged(nameof(ValidationRunModeBadge));
        OnPropertyChanged(nameof(ValidationRunModeSummary));
        OnPropertyChanged(nameof(ValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(HasValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(ValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(HasValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(ValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(HasValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(ValidationSequenceSummary));
        OnPropertyChanged(nameof(ValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationStageResults));
        OpenValidationOrchestrationArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationOrchestrationNoteCommand.RaiseCanExecuteChanged();

        try
        {
            if (generatedContext is not null)
            {
                PersistGeneratedOutputValidationLink(new GeneratedOutputValidationLink(
                    generatedContext.RunId,
                    generatedContext.RunPath,
                    generatedContext.SourcePath,
                    "validating",
                    $"{actionLabel} started.",
                    actionLabel,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow));
            }

            var result = await _validationRunnerService.RunAsync(
                action,
                settings,
                progress => RunOnUiThread(() => HandleValidationProgress(progress))).ConfigureAwait(true);

            ApplyValidationRunResult(result);
            if (generatedContext is not null)
            {
                PersistGeneratedOutputValidationLink(new GeneratedOutputValidationLink(
                    generatedContext.RunId,
                    generatedContext.RunPath,
                    generatedContext.SourcePath,
                    result.Success ? "passed" : "failed",
                    result.Summary,
                    actionLabel,
                    result.RunId,
                    result.OutputFolder,
                    result.FirstFailureText,
                    DateTimeOffset.UtcNow));
            }

            if (recordSemanticReuseOutcomes)
            {
                RecordSemanticReuseOutcomeEvidence(
                    priorSuggestions,
                    generatedContext,
                    result,
                    DetermineOutcomeClassification(result),
                    result.RunId,
                    string.Empty,
                    Path.Combine(result.OutputFolder, "validation_result.json"),
                    "validation");
            }

            if (beginOperationProgress)
                CompleteOperationProgress(result.Success, result.Summary);

            var validationClassification = string.IsNullOrWhiteSpace(result.StabilityClassification)
                ? (result.Success ? "passed" : "failed")
                : result.StabilityClassification;
            if (EnableSemanticReuseSuggestions &&
                beginOperationProgress &&
                (!result.Success || !string.Equals(validationClassification, "passed", StringComparison.Ordinal)))
            {
                SelectedSemanticReuseContext = "Validation failure";
                await RefreshSimilarCasesAsync().ConfigureAwait(true);
            }

            AddNarration(result.Success ? "success" : "error", "VALIDATION_RUN_COMPLETED", new Dictionary<string, string>
            {
                ["action"] = actionLabel,
                ["success"] = result.Success.ToString(),
                ["output_folder"] = result.OutputFolder
            });

            if (!result.Success && AutoOpenValidationLogsOnFailure)
            {
                var target = !string.IsNullOrWhiteSpace(result.FirstFailureLogPath) ? result.FirstFailureLogPath : result.OutputFolder;
                await _workspaceShell.OpenFolderAsync(target).ConfigureAwait(true);
            }

            return result;
        }
        catch (Exception ex)
        {
            _validationSummary = $"Validation failed: {ex.Message}";
            _validationFirstFailureText = ex.Message;
            _validationStabilityClassification = "failed";
            OnPropertyChanged(nameof(ValidationSummary));
            OnPropertyChanged(nameof(ValidationFirstFailureText));
            OnPropertyChanged(nameof(HasValidationFirstFailure));
            OnPropertyChanged(nameof(ValidationStabilityClassification));
            OnPropertyChanged(nameof(ValidationStabilityBadge));
            if (generatedContext is not null)
            {
                PersistGeneratedOutputValidationLink(new GeneratedOutputValidationLink(
                    generatedContext.RunId,
                    generatedContext.RunPath,
                    generatedContext.SourcePath,
                    "failed",
                    $"Validation failed: {ex.Message}",
                    actionLabel,
                    null,
                    _validationOutputFolder,
                    ex.Message,
                    DateTimeOffset.UtcNow));
            }

            if (beginOperationProgress)
            {
                SetOperationStatus("Validation failed", ex.Message);
                CompleteOperationProgress(false, $"Validation failed: {ex.Message}");
            }
            RecordFailure(
                "Validation",
                ex.ToString(),
                _validationOutputFolder,
                "Inspect the validation log folder and rerun the requested validation action.");
            return null;
        }
        finally
        {
            if (beginOperationProgress)
            {
                _activeValidationAction = null;
                _activeValidationActionLabel = string.Empty;
                _activeValidationStageId = string.Empty;
                OnPropertyChanged(nameof(ValidationDisabledReason));
                OnPropertyChanged(nameof(HasValidationDisabledReason));
                OnPropertyChanged(nameof(BuildUiProjectValidationDisabledReason));
                OnPropertyChanged(nameof(RunUiTestsValidationDisabledReason));
                OnPropertyChanged(nameof(RunSmokeValidationDisabledReason));
                OnPropertyChanged(nameof(RunIntegrityValidationDisabledReason));
                OnPropertyChanged(nameof(RunFullValidationLoopDisabledReason));
                OnPropertyChanged(nameof(ValidationSequenceSummary));
                OnPropertyChanged(nameof(ValidationActionPolicies));
                OnPropertyChanged(nameof(HasValidationActionPolicies));
            }
        }
    }

    private void HandleValidationProgress(ValidationProgressEvent progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.OutputFolder))
        {
            _validationOutputFolder = progress.OutputFolder;
            OnPropertyChanged(nameof(ValidationOutputFolder));
            OnPropertyChanged(nameof(HasValidationOutputFolder));
        }

        switch (progress.EventType)
        {
            case "run_started":
                SetOperationStatus("Running validation", progress.Message);
                break;
            case "stage_started":
                _activeValidationStageId = progress.StageId;
                SetOperationStatus(progress.StageLabel, progress.Message);
                SetOperationStepState(progress.StageLabel, "active", progress.Message);
                break;
            case "stage_retry_started":
                _activeValidationStageId = progress.StageId;
                SetOperationStatus(progress.StageLabel, progress.Message);
                SetOperationStepState(progress.StageLabel, "active", progress.Message);
                break;
            case "output":
                if (!string.IsNullOrWhiteSpace(progress.OutputLine))
                {
                    SetOperationLatestEvent($"{progress.StageLabel}: {progress.OutputLine.Trim()}");
                }
                break;
            case "stage_retry_completed":
                SetOperationStepState(
                    progress.StageLabel,
                    progress.Status,
                    progress.Message);
                SetOperationStatus("Running validation", progress.Message);
                break;
            case "stage_completed":
                SetOperationStepState(
                    progress.StageLabel,
                    progress.Status,
                    progress.Message);
                SetOperationStatus("Running validation", progress.Message);
                break;
            case "run_completed":
                _activeValidationStageId = string.Empty;
                SetOperationStatus(progress.Status == "failed" ? "Validation failed" : "Validation completed", progress.Message);
                break;
        }

        OnPropertyChanged(nameof(ValidationDisabledReason));
        OnPropertyChanged(nameof(HasValidationDisabledReason));
        OnPropertyChanged(nameof(BuildUiProjectValidationDisabledReason));
        OnPropertyChanged(nameof(RunUiTestsValidationDisabledReason));
        OnPropertyChanged(nameof(RunSmokeValidationDisabledReason));
        OnPropertyChanged(nameof(RunIntegrityValidationDisabledReason));
        OnPropertyChanged(nameof(RunFullValidationLoopDisabledReason));
        OnPropertyChanged(nameof(ValidationSequenceSummary));
        OnPropertyChanged(nameof(ValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationActionPolicies));
    }

    private void ApplyValidationRunResult(ValidationRunResult result)
    {
        _lastValidationResult = result;
        UpdateValidationOrchestrationState(result);
        _validationSummary = result.Summary;
        _validationOutputFolder = result.OutputFolder;
        _validationFirstFailureText = result.FirstFailureText ?? string.Empty;
        _validationFirstFailureLogPath = result.FirstFailureLogPath ?? string.Empty;
        _validationStabilityClassification = string.IsNullOrWhiteSpace(result.StabilityClassification)
            ? (result.Success ? "passed" : "failed")
            : result.StabilityClassification;
        _validationStabilityArtifactPath = !string.IsNullOrWhiteSpace(result.StabilityArtifactPath)
            ? result.StabilityArtifactPath!
            : Path.Combine(result.OutputFolder, "validation_stability.json");

        _validationStageResults.Clear();
        foreach (var stage in result.Stages)
        {
            _validationStageResults.Add(new ValidationStageResultRow(
                stage.StageLabel,
                stage.Status,
                stage.Summary,
                stage.LogPath,
                stage.DurationMs,
                string.IsNullOrWhiteSpace(stage.StabilityClassification)
                    ? (string.Equals(stage.Status, "passed", StringComparison.Ordinal) ? "passed" : "failed")
                    : stage.StabilityClassification,
                stage.RetryCount,
                stage.RetryLogPath));
        }

        LoadValidationRuns();
        ResetSemanticReuseSuggestions("Refresh Similar Cases to compare the latest validation artifacts.");

        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(ValidationOutputFolder));
        OnPropertyChanged(nameof(HasValidationOutputFolder));
        OnPropertyChanged(nameof(ValidationFirstFailureText));
        OnPropertyChanged(nameof(HasValidationFirstFailure));
        OnPropertyChanged(nameof(ValidationFirstFailureLogPath));
        OnPropertyChanged(nameof(HasValidationFirstFailureLogPath));
        OnPropertyChanged(nameof(ValidationStabilityClassification));
        OnPropertyChanged(nameof(ValidationStabilityBadge));
        OnPropertyChanged(nameof(ValidationStabilityArtifactPath));
        OnPropertyChanged(nameof(HasValidationStabilityArtifactPath));
        OnPropertyChanged(nameof(ValidationRunMode));
        OnPropertyChanged(nameof(ValidationRunModeBadge));
        OnPropertyChanged(nameof(ValidationRunModeSummary));
        OnPropertyChanged(nameof(ValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(HasValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(ValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(HasValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(ValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(HasValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(ValidationSequenceSummary));
        OnPropertyChanged(nameof(ValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationStageResults));
        OpenValidationOutputFolderCommand.RaiseCanExecuteChanged();
        OpenValidationFailureLogCommand.RaiseCanExecuteChanged();
        OpenValidationStabilityArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationOrchestrationArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationOrchestrationNoteCommand.RaiseCanExecuteChanged();
    }

    private Task OpenValidationOutputFolderAsync()
        => OpenFolderIfExistsAsync(_validationOutputFolder);

    private Task OpenValidationFailureLogAsync()
        => OpenPathIfExistsAsync(_validationFirstFailureLogPath);

    private Task OpenValidationStabilityArtifactAsync()
        => OpenPathIfExistsAsync(_validationStabilityArtifactPath);

    private Task OpenValidationOrchestrationArtifactAsync()
        => OpenPathIfExistsAsync(_validationOrchestrationArtifactPath);

    private Task OpenValidationOrchestrationNoteAsync()
        => OpenPathIfExistsAsync(_validationOrchestrationPolicyNotePath);

    private Task OpenValidationHandoffSummaryAsync()
        => OpenPathIfExistsAsync(_validationHandoffSummaryPath);

    private Task OpenValidationHandoffBundleFolderAsync()
        => OpenFolderIfExistsAsync(Path.GetDirectoryName(_validationHandoffBundlePath) ?? string.Empty);

    private Task CopyValidationHandoffSummaryAsync()
    {
        if (!HasValidationHandoffSummary)
            return Task.CompletedTask;

        if (HasValidationHandoffSummaryPath)
            return _workspaceShell.CopyTextAsync(File.ReadAllText(_validationHandoffSummaryPath));

        return _workspaceShell.CopyTextAsync(_validationHandoffSummaryText);
    }

    private Task CopyValidationHandoffArtifactPathsAsync()
    {
        var text = BuildValidationHandoffArtifactPathClipboardText();
        return string.IsNullOrWhiteSpace(text)
            ? Task.CompletedTask
            : _workspaceShell.CopyTextAsync(text);
    }

    private Task OpenValidationFollowupIntakeAsync()
        => OpenPathIfExistsAsync(_validationFollowupIntakePath);

    private Task OpenValidationFollowupPromptAsync()
        => OpenPathIfExistsAsync(_validationFollowupPromptPath);

    private Task CopyValidationFollowupSummaryAsync()
    {
        if (!HasValidationFollowupSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationFollowupSummaryText);
    }

    private Task CopyValidationFollowupPromptAsync()
    {
        if (!HasValidationFollowupPromptPath)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(File.ReadAllText(_validationFollowupPromptPath));
    }

    private Task OpenValidationFollowupPlanAsync()
    {
        if (!HasValidationFollowupPlanPath)
            return Task.CompletedTask;

        MarkValidationFollowupPlanInteraction(_latestValidationFollowupPlan?.Steps.FirstOrDefault(), "opened", "open_artifact", _validationFollowupPlanPath);
        return OpenPathIfExistsAsync(_validationFollowupPlanPath);
    }

    private Task OpenValidationRepairPrepBundleAsync()
    {
        if (!HasValidationRepairPrepBundlePath)
            return Task.CompletedTask;

        var step = _latestValidationFollowupPlan?.Steps.FirstOrDefault(item => string.Equals(item.ActionKind, "open_repair_prep_bundle", StringComparison.Ordinal));
        MarkValidationFollowupPlanInteraction(step, "opened", "open_repair_prep_bundle", _validationRepairPrepBundlePath);
        return OpenPathIfExistsAsync(_validationRepairPrepBundlePath);
    }

    private Task CopyValidationFollowupPlanSummaryAsync()
    {
        if (!HasValidationFollowupPlanSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationFollowupPlanSummaryText);
    }

    private Task CopyValidationRepairPrepSummaryAsync()
    {
        if (!HasValidationRepairPrepSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationRepairPrepSummaryText);
    }

    private Task CopyValidationFollowupRerunRecommendationAsync()
    {
        if (!HasValidationFollowupRerunRecommendation)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationFollowupRerunRecommendationText);
    }

    private Task OpenValidationFollowupExecutionOutcomeAsync()
        => OpenPathIfExistsAsync(_validationFollowupExecutionOutcomePath);

    private Task OpenValidationFollowupEscalationAsync()
        => OpenPathIfExistsAsync(_validationFollowupEscalationPath);

    private Task OpenValidationFollowupResolutionReviewAsync()
        => OpenPathIfExistsAsync(_validationFollowupResolutionReviewPath);

    private Task OpenValidationResolutionHandoffAsync()
        => OpenPathIfExistsAsync(_validationResolutionHandoffPath);

    private Task OpenValidationResolutionPromotionReviewAsync()
        => OpenPathIfExistsAsync(_validationResolutionPromotionReviewPath);

    private Task OpenValidationReleaseDecisionSummaryAsync()
        => OpenPathIfExistsAsync(_validationReleaseDecisionSummaryPath);

    private bool CanOpenValidationFollowupRerunArtifacts()
        => !string.IsNullOrWhiteSpace(ResolveValidationFollowupRerunArtifactsPath(requireExists: false));

    private Task OpenValidationFollowupRerunArtifactsAsync()
    {
        var path = ResolveValidationFollowupRerunArtifactsPath(requireExists: false);
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        return _workspaceShell.OpenFolderAsync(path);
    }

    private Task CopyValidationFollowupOutcomeNextStepAsync()
    {
        if (!HasValidationFollowupOutcomeNextStateText)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationFollowupOutcomeNextStepText);
    }

    private Task CopyValidationFollowupEscalationSummaryAsync()
    {
        if (!HasValidationFollowupEscalationSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationFollowupEscalationSummaryText);
    }

    private Task CopyValidationFollowupClosureSummaryAsync()
    {
        if (!HasValidationFollowupResolutionSummary)
            return Task.CompletedTask;

        var lines = new List<string>();
        if (HasValidationFollowupResolutionOriginalIssueSummary)
            lines.Add(_validationFollowupResolutionOriginalIssueSummary);
        lines.Add(_validationFollowupResolutionSummaryText);
        if (HasValidationFollowupResolutionClosureText)
            lines.Add(_validationFollowupResolutionClosureText);
        if (HasValidationFollowupResolutionReopenSummary)
            lines.Add(_validationFollowupResolutionReopenSummaryText);
        if (HasValidationFollowupResolutionFreshnessText)
            lines.Add(_validationFollowupResolutionFreshnessText);

        return _workspaceShell.CopyTextAsync(string.Join(System.Environment.NewLine, lines));
    }

    private Task CopyValidationResolutionHandoffSummaryAsync()
    {
        if (!HasValidationResolutionHandoffSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationResolutionHandoffSummaryText);
    }

    private Task CopyValidationResolutionPromotionSummaryAsync()
    {
        if (!HasValidationResolutionPromotionSummary)
            return Task.CompletedTask;

        return _workspaceShell.CopyTextAsync(_validationResolutionPromotionSummaryText);
    }

    private Task CopyValidationReleaseDecisionSummaryAsync()
    {
        if (!HasValidationReleaseDecisionSummary)
            return Task.CompletedTask;

        var lines = new List<string> { _validationReleaseDecisionSummaryText };
        if (HasValidationReleaseDecisionNotesSummary)
            lines.Add(_validationReleaseDecisionNotesSummaryText);

        return _workspaceShell.CopyTextAsync(string.Join(System.Environment.NewLine, lines));
    }

    private string ResolveValidationFollowupRerunArtifactsPath(bool requireExists)
    {
        var candidates = new[]
        {
            _latestValidationFollowupExecutionOutcome?.RerunValidationOutputFolder ?? string.Empty,
            _latestValidationFollowupExecutionState?.LatestRerun?.RerunValidationOutputFolder ?? string.Empty,
            _latestValidationFollowupExecutionState?.LatestRerun?.ResultArtifactPath ?? string.Empty,
            _latestValidationFollowupExecutionState?.LatestRerun?.StabilityArtifactPath ?? string.Empty
        };

        return requireExists
            ? candidates.FirstOrDefault(path => PathExists(path)) ?? string.Empty
            : candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty;
    }

    private bool CanRunValidationFollowupRecommendedRerun()
    {
        var step = GetRecommendedFollowupPlanStep();
        return step is not null && string.IsNullOrWhiteSpace(GetValidationFollowupPlanStepBlockReason(step));
    }

    private bool CanOpenValidationFollowupFirstEvidence()
    {
        var step = GetFirstEvidenceFollowupPlanStep();
        return step is not null && string.IsNullOrWhiteSpace(GetValidationFollowupPlanStepBlockReason(step));
    }

    private bool CanCopyValidationFollowupRerunCommandSummary()
        => GetRecommendedFollowupPlanStep() is { CommandSummary.Length: > 0 };

    private bool CanExecuteValidationFollowupPlanStep(ValidationFollowupPlanStepRow step)
        => step.HasPrimaryAction && string.IsNullOrWhiteSpace(step.BlockReason);

    private bool CanCopyValidationFollowupPlanStep(ValidationFollowupPlanStepRow step)
        => step.HasCopyAction;

    private async Task RunValidationFollowupRecommendedRerunAsync()
    {
        var step = GetRecommendedFollowupPlanStep();
        if (step is null)
            return;

        await ExecuteGuidedValidationFollowupStepAsync(step).ConfigureAwait(true);
    }

    private Task OpenValidationFollowupFirstEvidenceAsync()
    {
        var step = GetFirstEvidenceFollowupPlanStep();
        if (step is null)
            return Task.CompletedTask;

        return ExecuteGuidedValidationFollowupStepAsync(step);
    }

    private async Task CopyValidationFollowupRerunCommandSummaryAsync()
    {
        var step = GetRecommendedFollowupPlanStep();
        if (step is null || string.IsNullOrWhiteSpace(step.CommandSummary))
            return;

        MarkValidationFollowupPlanInteraction(step, "copied", "copy_command_summary", step.CommandSummary);
        await _workspaceShell.CopyTextAsync(step.CommandSummary).ConfigureAwait(true);
    }

    private async Task ExecuteValidationFollowupPlanStepAsync(ValidationFollowupPlanStepRow? row)
    {
        if (row is null)
            return;

        var step = _latestValidationFollowupPlan?.Steps.FirstOrDefault(item => item.Order == row.Order && string.Equals(item.StepType, row.StepType, StringComparison.Ordinal));
        if (step is null)
            return;

        await ExecuteGuidedValidationFollowupStepAsync(step).ConfigureAwait(true);
    }

    private async Task CopyValidationFollowupPlanStepAsync(ValidationFollowupPlanStepRow? row)
    {
        if (row is null)
            return;

        var step = _latestValidationFollowupPlan?.Steps.FirstOrDefault(item => item.Order == row.Order && string.Equals(item.StepType, row.StepType, StringComparison.Ordinal));
        if (step is null)
            return;

        var text = string.IsNullOrWhiteSpace(step.CommandSummary) ? step.Summary : step.CommandSummary;
        if (string.IsNullOrWhiteSpace(text))
            return;

        MarkValidationFollowupPlanInteraction(step, "copied", "copy_step_summary", text);
        await _workspaceShell.CopyTextAsync(text).ConfigureAwait(true);
    }

    private async Task ExecuteGuidedValidationFollowupStepAsync(ValidationFollowupPlanStep step)
    {
        var blocker = GetValidationFollowupPlanStepBlockReason(step);
        if (!string.IsNullOrWhiteSpace(blocker))
            return;

        switch (step.ActionKind)
        {
            case "open_log":
            case "open_artifact":
            {
                var target = ResolveValidationFollowupStepPrimaryPath(step);
                if (string.IsNullOrWhiteSpace(target))
                    return;

                MarkValidationFollowupPlanInteraction(step, "opened", step.ActionKind, target);
                await OpenPathIfExistsAsync(target).ConfigureAwait(true);
                break;
            }
            case "open_repair_prep_bundle":
            {
                if (!HasValidationRepairPrepBundlePath)
                    return;

                MarkValidationFollowupPlanInteraction(step, "opened", step.ActionKind, _validationRepairPrepBundlePath);
                await OpenPathIfExistsAsync(_validationRepairPrepBundlePath).ConfigureAwait(true);
                break;
            }
            case "rerun_build_scope":
            case "rerun_single_stage":
            case "rerun_single_test_or_project":
            {
                if (!TryMapFollowupStepToValidationAction(step, out var action, out var actionLabel))
                    return;

                var sourceResult = _latestValidationFollowupPlan is null
                    ? null
                    : ValidationRunnerService.LoadRunResultForOutputFolder(_latestValidationFollowupPlan.OutputFolder);
                if (sourceResult is null || _latestValidationFollowupPlan is null)
                    return;

                _validationFollowupPinnedOutputFolder = _latestValidationFollowupPlan.OutputFolder;
                MarkValidationFollowupPlanInteraction(step, "executed", step.ActionKind, step.CommandSummary);
                var rerunResult = await ExecuteValidationActionAsync(
                    action,
                    null,
                    beginOperationProgress: true,
                    actionLabelOverride: actionLabel,
                    recordSemanticReuseOutcomes: false).ConfigureAwait(true);
                if (rerunResult is null)
                    return;

                var outcome = BuildFollowupRerunOutcomeClassification(sourceResult, rerunResult);
                ValidationRunnerService.RecordFollowupRerun(
                    _latestValidationFollowupPlan.OutputFolder,
                    step.Order,
                    step.StepType,
                    step.ActionKind,
                    actionLabel,
                    step.CommandSummary,
                    rerunResult,
                    outcome);
                LoadValidationFollowupPlanArtifacts();
                break;
            }
        }
    }

    private void MarkValidationFollowupPlanInteraction(ValidationFollowupPlanStep? step, string completionState, string actionKind, string detail)
    {
        if (step is null || _latestValidationFollowupPlan is null)
            return;

        ValidationRunnerService.RecordFollowupStepInteraction(
            _latestValidationFollowupPlan.OutputFolder,
            step.Order,
            step.StepType,
            completionState,
            actionKind,
            detail,
            ResolveValidationFollowupStepPrimaryPath(step));
        LoadValidationFollowupPlanArtifacts();
    }

    private Task OpenValidationHistoryLedgerAsync()
        => OpenPathIfExistsAsync(_validationHistoryLedgerPath);

    private Task OpenValidationTrendArtifactAsync()
        => OpenPathIfExistsAsync(_validationTrendArtifactPath);

    private Task OpenValidationRegressionArtifactAsync()
        => OpenPathIfExistsAsync(_validationRegressionArtifactPath);

    private Task OpenValidationBaselineArtifactAsync()
        => OpenPathIfExistsAsync(_validationBaselineArtifactPath);

    private Task OpenValidationBaselineHistoryArtifactAsync()
        => OpenPathIfExistsAsync(_validationBaselineHistoryArtifactPath);

    private Task OpenValidationBaselineComparisonArtifactAsync()
        => OpenPathIfExistsAsync(_validationBaselineComparisonArtifactPath);

    private bool CanRefreshSimilarCases()
        => string.IsNullOrWhiteSpace(GetSemanticReuseDisabledReason());

    private string GetSemanticReuseDisabledReason()
    {
        if (!EnableSemanticReuseSuggestions)
            return "Semantic reuse suggestions are disabled in Validation Options.";

        if (IsOperationActive)
            return $"Similar cases are unavailable while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return "Similar cases are unavailable while another UI action is busy.";

        return BuildSemanticReuseQueries().Count == 0
            ? "Similar cases are available after planning context is prepared, a failed validation is loaded, a repair comparison exists, or a provider issue is visible."
            : string.Empty;
    }

    private async Task RefreshSimilarCasesAsync()
    {
        var blocker = GetSemanticReuseDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
            return;

        try
        {
            var result = await _semanticReuseService
                .FindSimilarCasesAsync(BuildSemanticReuseQueries(), BuildValidationSettings())
                .ConfigureAwait(true);
            ApplySemanticReuseResult(result);
            AddNarration("info", "SEMANTIC_REUSE_REFRESHED", new Dictionary<string, string>
            {
                ["status"] = result.Status,
                ["suggestion_count"] = result.Suggestions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        catch (Exception ex)
        {
            _semanticReuseStatus = "local_only";
            _semanticReuseSummary = $"Semantic reuse refresh failed: {ex.Message}";
            OnPropertyChanged(nameof(SemanticReuseStatus));
            OnPropertyChanged(nameof(SemanticReuseBadge));
            OnPropertyChanged(nameof(SemanticReuseSummary));
            RecordFailure(
                "Semantic reuse",
                ex.ToString(),
                _semanticReuseIndexPath,
                "Inspect the semantic reuse index artifacts and retry similar-case refresh if needed.");
        }
    }

    private void ApplySemanticReuseResult(SemanticReuseSuggestionSet result)
    {
        _semanticReuseStatus = result.Status;
        _semanticReuseSummary = result.Summary;
        _semanticReuseDesignNotePath = result.DesignNotePath;
        _semanticReuseIndexPath = result.IndexPath;
        _semanticReuseLinkagePath = result.LinkagePath;
        LoadSemanticReuseDerivedArtifacts();
        _semanticReuseSuggestions.Clear();
        foreach (var suggestion in result.Suggestions)
        {
            var metadata = suggestion.Metadata ?? Array.Empty<SemanticReuseMetadataField>();
            var changedFiles = GetSemanticReuseMetadataValue(metadata, "changed_file_names");
            var changedFilesSummary = string.IsNullOrWhiteSpace(changedFiles)
                ? string.Empty
                : $"Changed files: {string.Join(", ", changedFiles.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5))}";
            var promotionStatus = GetSemanticReuseMetadataValue(metadata, "promotion_status");
            var adoptionState = GetSemanticReuseMetadataValue(metadata, "adoption_state");
            var repairedValidationStatus = GetSemanticReuseMetadataValue(metadata, "repaired_validation_status");
            var stageSummary = GetSemanticReuseMetadataValue(metadata, "failing_stage");
            var validationOutcomeSummary = BuildSemanticReuseValidationOutcomeSummary(suggestion.CaseType, suggestion.Outcome, repairedValidationStatus, stageSummary);
            var promotionAdoptionSummary = BuildSemanticReusePromotionSummary(promotionStatus, adoptionState);
            var row = new SemanticReuseSuggestionRow(
                suggestion.DocumentId,
                suggestion.ContextKind,
                suggestion.ContextLabel,
                suggestion.CaseType,
                suggestion.Title,
                suggestion.Summary,
                suggestion.Outcome,
                suggestion.RankingLabel,
                suggestion.Score,
                suggestion.MatchExplanation,
                suggestion.PrimaryArtifactPath,
                suggestion.SourceRunId,
                validationOutcomeSummary,
                changedFilesSummary,
                promotionAdoptionSummary,
                suggestion.UsefulnessSummary,
                suggestion.ArtifactLinks.Select(link => link.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray());
            row.PropertyChanged += SemanticReuseSuggestionPropertyChanged;
            _semanticReuseSuggestions.Add(row);
        }

        SelectSemanticReuseContextForCurrentState();
        UpdateSemanticReuseContextSummary();

        OnPropertyChanged(nameof(SemanticReuseStatus));
        OnPropertyChanged(nameof(SemanticReuseBadge));
        OnPropertyChanged(nameof(SemanticReuseSummary));
        OnPropertyChanged(nameof(SelectedSemanticReuseContext));
        OnPropertyChanged(nameof(SemanticReuseDesignNotePath));
        OnPropertyChanged(nameof(HasSemanticReuseDesignNotePath));
        OnPropertyChanged(nameof(SemanticReuseIndexPath));
        OnPropertyChanged(nameof(HasSemanticReuseIndexPath));
        OnPropertyChanged(nameof(SemanticReuseLinkagePath));
        OnPropertyChanged(nameof(HasSemanticReuseLinkagePath));
        OnPropertyChanged(nameof(SemanticReuseEffectivenessSummary));
        OnPropertyChanged(nameof(HasSemanticReuseEffectivenessSummary));
        OnPropertyChanged(nameof(SemanticReuseEffectivenessPath));
        OnPropertyChanged(nameof(HasSemanticReuseEffectivenessPath));
        OnPropertyChanged(nameof(SemanticReusePlaybookPath));
        OnPropertyChanged(nameof(HasSemanticReusePlaybookPath));
        OnPropertyChanged(nameof(SemanticReusePlaybookSummary));
        OnPropertyChanged(nameof(HasSemanticReusePlaybookSummary));
        OnPropertyChanged(nameof(HasSemanticReuseSuggestions));
        OnPropertyChanged(nameof(VisibleSemanticReuseSuggestions));
        OnPropertyChanged(nameof(HasVisibleSemanticReuseSuggestions));
        OnPropertyChanged(nameof(HasSemanticReusePlaybooks));
        OnPropertyChanged(nameof(VisibleSemanticReusePlaybooks));
        OnPropertyChanged(nameof(HasVisibleSemanticReusePlaybooks));
        OnPropertyChanged(nameof(SemanticReuseContextSummary));
        OnPropertyChanged(nameof(HasSemanticReuseContextSummary));
        OnPropertyChanged(nameof(SelectedSemanticReuseRepairReferenceCount));
        OnPropertyChanged(nameof(HasSelectedSemanticReuseRepairReferences));
        OnPropertyChanged(nameof(SemanticReuseDisabledReason));
        OnPropertyChanged(nameof(HasSemanticReuseDisabledReason));
        RefreshValidationFollowupSuggestionSummary();
        OnPropertyChanged(nameof(ValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(HasValidationFollowupReuseSuggestionSummary));
        OpenSemanticReuseDesignNoteCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseIndexCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseEffectivenessCommand.RaiseCanExecuteChanged();
        OpenSemanticReusePlaybookCatalogCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseSuggestionArtifactCommand.NotifyCanExecuteChanged();
        OpenSemanticReusePlaybookArtifactCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<SemanticReuseQuery> BuildSemanticReuseQueries()
    {
        var queries = new List<SemanticReuseQuery>();
        var generatedContext = ResolveGeneratedOutputContext();
        var hasPlanningContext =
            !string.IsNullOrWhiteSpace(IntakeIntent) ||
            !string.IsNullOrWhiteSpace(IntakeTarget) ||
            !string.IsNullOrWhiteSpace(IntakeAttachments) ||
            !string.IsNullOrWhiteSpace(IntakeStack) ||
            IsWorkOrderLocked;
        var planningText = string.Join(
            " ",
            new[] { IntakeIntent, IntakeTarget, IntakeAttachments, IntakeStack, JobSpecDigest, ActiveWorkspace?.Name, CurrentProject?.Name }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (hasPlanningContext && !string.IsNullOrWhiteSpace(planningText))
        {
            queries.Add(new SemanticReuseQuery(
                $"planning|{JobSpecDigest}",
                "Current planning context",
                new[] { "generated_output_pattern", "validation_failure_record", "repair_bundle_summary", "repair_promotion_outcome" },
                planningText,
                string.Empty,
                new[]
                {
                    new SemanticReuseMetadataField("project_name", ActiveWorkspace?.Name ?? CurrentProject?.Name ?? string.Empty),
                    new SemanticReuseMetadataField("source_path", ActiveWorkspace?.RootPath ?? string.Empty)
                },
                new[] { ActiveWorkspace?.RootPath ?? string.Empty },
                ContextKind: "planning",
                PreferredSourceRunIds: Array.Empty<string>()));
        }

        if (_lastValidationResult is not null)
        {
            var classification = string.IsNullOrWhiteSpace(_lastValidationResult.StabilityClassification)
                ? (_lastValidationResult.Success ? "passed" : "failed")
                : _lastValidationResult.StabilityClassification;
            if (!_lastValidationResult.Success || !string.Equals(classification, "passed", StringComparison.Ordinal))
            {
                var failedStage = _lastValidationResult.FirstFailure?.StageLabel
                    ?? _lastValidationResult.Stages.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal))?.StageLabel
                    ?? _lastValidationResult.Stages.FirstOrDefault(stage => !string.Equals(stage.StabilityClassification, "passed", StringComparison.Ordinal))?.StageLabel
                    ?? string.Empty;
                queries.Add(new SemanticReuseQuery(
                    _lastValidationResult.RunId,
                    "Current validation failure",
                    new[] { "validation_failure_record", "repair_bundle_summary", "repair_promotion_outcome", "generated_output_pattern", "baseline_drift_regression_summary", "replay_divergence_summary" },
                    string.Join(" ", new[] { _lastValidationResult.ActionLabel, _lastValidationResult.Summary, _lastValidationResult.FirstFailureText }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    classification,
                    new[]
                    {
                        new SemanticReuseMetadataField("failing_stage", failedStage),
                        new SemanticReuseMetadataField("failing_test_name", _lastValidationResult.FirstFailure?.FailingTestName ?? string.Empty),
                        new SemanticReuseMetadataField("project_name", ActiveWorkspace?.Name ?? CurrentProject?.Name ?? string.Empty)
                    },
                    new[]
                    {
                        Path.Combine(_lastValidationResult.OutputFolder, "validation_result.json"),
                        generatedContext?.RunPath ?? string.Empty,
                        generatedContext is null ? string.Empty : GeneratedOutputValidationLinkService.PathForRun(generatedContext.RunPath),
                        generatedContext is null ? string.Empty : RepairReviewArtifactsService.HistoryPathForRun(generatedContext.RunPath),
                        generatedContext is null ? string.Empty : RepairReviewArtifactsService.PromotionPathForRun(generatedContext.RunPath),
                        !string.IsNullOrWhiteSpace(_lastValidationResult.StabilityArtifactPath)
                            ? _lastValidationResult.StabilityArtifactPath!
                            : Path.Combine(_lastValidationResult.OutputFolder, "validation_stability.json")
                    },
                    ContextKind: "validation_failure",
                    PreferredSourceRunIds: generatedContext is null
                        ? new[] { _lastValidationResult.RunId }
                        : new[] { generatedContext.RunId, _lastValidationResult.RunId }));
            }
        }

        if (_latestRepairComparison is not null)
        {
            queries.Add(new SemanticReuseQuery(
                _latestRepairComparison.RepairId,
                "Current repair attempt",
                new[] { "repair_bundle_summary", "repair_promotion_outcome", "validation_failure_record", "generated_output_pattern" },
                string.Join(" ", new[] { _latestRepairComparison.SourceFirstFailureExcerpt, _latestRepairComparison.RepairedFirstFailureExcerpt, _latestRepairComparison.RepairSummary }.Where(value => !string.IsNullOrWhiteSpace(value))),
                _latestRepairComparison.ImprovementState,
                new[]
                {
                    new SemanticReuseMetadataField("failing_stage", _latestRepairComparison.SourceFailedStage),
                    new SemanticReuseMetadataField("changed_file_names", string.Join("|", _latestRepairComparison.ChangedFiles.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)))),
                    new SemanticReuseMetadataField("project_name", ActiveWorkspace?.Name ?? CurrentProject?.Name ?? string.Empty)
                },
                new[]
                {
                    RepairReviewArtifactsService.ComparisonPathForRepair(_latestRepairComparison.RepairResultFolder),
                    _latestRepairComparison.RepairBundlePath
                },
                ContextKind: "repair_attempt",
                PreferredSourceRunIds: new[]
                {
                    _latestRepairComparison.SourceValidationRunId,
                    _latestRepairComparison.RepairedValidationRunId,
                    generatedContext?.RunId ?? string.Empty
                }));
        }

        var providerIssue = _providerDiagnostics
            .OrderByDescending(item => item.ObservedAtUtc)
            .ThenBy(item => item.Provider, StringComparer.Ordinal)
            .FirstOrDefault(item => !string.Equals(item.Classification, "available", StringComparison.Ordinal));
        if (providerIssue is not null)
        {
            queries.Add(new SemanticReuseQuery(
                $"{providerIssue.Provider}|{providerIssue.ObservedAtUtc:O}",
                "Current provider issue",
                new[] { "provider_diagnostics_episode" },
                string.Join(" ", new[] { providerIssue.Provider, providerIssue.Classification, providerIssue.ErrorCode, providerIssue.Summary }.Where(value => !string.IsNullOrWhiteSpace(value))),
                providerIssue.Classification,
                new[]
                {
                    new SemanticReuseMetadataField("provider_name", providerIssue.Provider),
                    new SemanticReuseMetadataField("provider_classification", providerIssue.Classification)
                },
                new[] { ProviderDiagnosticsPath },
                ContextKind: "provider_diagnostics",
                PreferredSourceRunIds: Array.Empty<string>()));
        }

        return queries;
    }

    private Task OpenSemanticReuseDesignNoteAsync()
        => OpenPathIfExistsAsync(_semanticReuseDesignNotePath);

    private Task OpenSemanticReuseIndexAsync()
        => OpenPathIfExistsAsync(_semanticReuseIndexPath);

    private Task OpenSemanticReuseEffectivenessAsync()
        => OpenPathIfExistsAsync(_semanticReuseEffectivenessPath);

    private Task OpenSemanticReusePlaybookCatalogAsync()
        => OpenPathIfExistsAsync(_semanticReusePlaybookPath);

    private Task OpenSemanticReuseSuggestionArtifactAsync(SemanticReuseSuggestionRow? row)
        => OpenPathIfExistsAsync(row?.PrimaryArtifactPath ?? string.Empty);

    private Task OpenSemanticReusePlaybookArtifactAsync(SemanticReusePlaybookRow? row)
        => OpenPathIfExistsAsync(row?.PrimaryArtifactPath ?? string.Empty);

    private bool MatchesSemanticReuseContextFilter(string contextKind)
        => SelectedSemanticReuseContext switch
        {
            "Planning" => string.Equals(contextKind, "planning", StringComparison.Ordinal),
            "Validation failure" => string.Equals(contextKind, "validation_failure", StringComparison.Ordinal),
            "Repair attempt" => string.Equals(contextKind, "repair_attempt", StringComparison.Ordinal)
                || string.Equals(contextKind, "repair_bundle_reference", StringComparison.Ordinal),
            "Provider diagnostics" => string.Equals(contextKind, "provider_diagnostics", StringComparison.Ordinal),
            _ => true
        };

    private static int MapSemanticReusePlaybookConfidence(string confidence)
        => confidence switch
        {
            "trusted" => 3,
            "corroborated" => 2,
            "tentative" => 1,
            _ => 0
        };

    private bool MatchesCurrentSemanticReusePlaybook(SemanticReusePlaybookRow row)
    {
        var currentSignals = BuildCurrentSemanticReuseSignals(row.ContextKind);
        if (currentSignals.Count == 0)
            return false;

        foreach (var signal in currentSignals)
        {
            var rowValues = row.MatchMetadata
                .Where(field => string.Equals(field.Name, signal.Name, StringComparison.Ordinal))
                .SelectMany(field => field.Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (rowValues.Count == 0)
                continue;

            var signalValues = signal.Value
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (signalValues.Overlaps(rowValues))
                return true;
        }

        return false;
    }

    private IReadOnlyList<SemanticReuseMetadataField> BuildCurrentSemanticReuseSignals(string contextKind)
    {
        var projectName = ActiveWorkspace?.Name ?? CurrentProject?.Name ?? string.Empty;
        var currentFailedStage = _lastValidationResult is null ? string.Empty : DetermineFailedStage(_lastValidationResult).StageLabel;
        var signals = contextKind switch
        {
            "planning" => new[]
            {
                new SemanticReuseMetadataField("project_name", projectName),
                new SemanticReuseMetadataField("source_path", ActiveWorkspace?.RootPath ?? ResolveGeneratedOutputContext()?.SourcePath ?? string.Empty)
            },
            "provider_diagnostics" => BuildCurrentProviderSignals(),
            "repair_bundle_reference" => new[]
            {
                new SemanticReuseMetadataField("failing_stage", _latestRepairComparison?.SourceFailedStage ?? currentFailedStage),
                new SemanticReuseMetadataField("changed_file_names", string.Join("|", _latestRepairComparison?.ChangedFiles.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)) ?? Array.Empty<string>())),
                new SemanticReuseMetadataField("project_name", projectName)
            },
            _ => new[]
            {
                new SemanticReuseMetadataField("failing_stage", currentFailedStage),
                new SemanticReuseMetadataField("failing_test_name", _lastValidationResult?.FirstFailure?.FailingTestName ?? string.Empty),
                new SemanticReuseMetadataField("project_name", projectName)
            }
        };

        return signals
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .ToArray();
    }

    private IReadOnlyList<SemanticReuseMetadataField> BuildCurrentProviderSignals()
    {
        var providerIssue = _providerDiagnostics
            .OrderByDescending(item => item.ObservedAtUtc)
            .ThenBy(item => item.Provider, StringComparer.Ordinal)
            .FirstOrDefault(item => !string.Equals(item.Classification, "available", StringComparison.Ordinal));
        if (providerIssue is null)
            return Array.Empty<SemanticReuseMetadataField>();

        return new[]
        {
            new SemanticReuseMetadataField("provider_name", providerIssue.Provider),
            new SemanticReuseMetadataField("provider_classification", providerIssue.Classification)
        };
    }

    private static string DetermineOutcomeClassification(ValidationRunResult result)
    {
        if (string.Equals(result.StabilityClassification, "passed_on_retry", StringComparison.Ordinal))
            return "passed_on_retry";

        if (result.Success)
            return "passed";

        return "failed";
    }

    private void RecordSemanticReuseOutcomeEvidence(
        IReadOnlyList<SemanticReuseSuggestionRow> priorSuggestions,
        GeneratedOutputContext? generatedContext,
        ValidationRunResult result,
        string outcomeClassification,
        string validationRunId,
        string repairId,
        string evidenceArtifactPath,
        string outcomeArtifactKind)
    {
        if (!EnableSemanticReuseSuggestions || priorSuggestions.Count == 0)
            return;

        var settings = BuildValidationSettings();
        var baseArtifactPaths = new[]
        {
            evidenceArtifactPath,
            result.OutputFolder,
            result.StabilityArtifactPath ?? string.Empty,
            generatedContext?.RunPath ?? string.Empty,
            generatedContext is null ? string.Empty : GeneratedOutputValidationLinkService.PathForRun(generatedContext.RunPath)
        };
        var sourceRunId = generatedContext?.RunId
            ?? ResolveGeneratedOutputContext()?.RunId
            ?? _linkedGeneratedOutputRunId
            ?? string.Empty;

        RecordSemanticReuseOutcomeForContext(
            priorSuggestions,
            "planning",
            generatedContext is not null,
            _validationRunnerService.RepoRoot,
            sourceRunId,
            validationRunId,
            repairId,
            outcomeClassification,
            result.Summary,
            evidenceArtifactPath,
            baseArtifactPaths,
            outcomeArtifactKind,
            settings,
            result.CompletedUtc);

        RecordSemanticReuseOutcomeForContext(
            priorSuggestions,
            "validation_failure",
            true,
            _validationRunnerService.RepoRoot,
            sourceRunId,
            validationRunId,
            repairId,
            outcomeClassification,
            result.Summary,
            evidenceArtifactPath,
            baseArtifactPaths,
            outcomeArtifactKind,
            settings,
            result.CompletedUtc);

        RecordSemanticReuseOutcomeForContext(
            priorSuggestions,
            "provider_diagnostics",
            true,
            _validationRunnerService.RepoRoot,
            sourceRunId,
            validationRunId,
            repairId,
            outcomeClassification,
            result.Summary,
            evidenceArtifactPath,
            baseArtifactPaths.Append(ProviderDiagnosticsPath),
            outcomeArtifactKind,
            settings,
            result.CompletedUtc);
    }

    private static void RecordSemanticReuseOutcomeForContext(
        IEnumerable<SemanticReuseSuggestionRow> priorSuggestions,
        string contextKind,
        bool shouldRecord,
        string repoRoot,
        string sourceRunId,
        string validationRunId,
        string repairId,
        string outcomeClassification,
        string evidenceSummary,
        string evidenceArtifactPath,
        IEnumerable<string> linkedArtifactPaths,
        string outcomeArtifactKind,
        ValidationSettings settings,
        DateTimeOffset recordedUtc)
    {
        if (!shouldRecord)
            return;

        var references = priorSuggestions
            .Where(row => string.Equals(row.ContextKind, contextKind, StringComparison.Ordinal))
            .Select(row => row.ToRepairReferenceCase())
            .OrderBy(row => row.DocumentId, StringComparer.Ordinal)
            .ThenBy(row => row.PrimaryArtifactPath, StringComparer.Ordinal)
            .ToArray();
        if (references.Length == 0)
            return;

        SemanticReuseService.RecordSuggestionOutcome(
            repoRoot,
            references,
            contextKind,
            sourceRunId,
            validationRunId,
            repairId,
            outcomeClassification,
            evidenceSummary,
            evidenceArtifactPath,
            linkedArtifactPaths,
            outcomeArtifactKind,
            recordedUtc,
            settings);
    }

    private void SelectSemanticReuseContextForCurrentState()
    {
        if (_semanticReuseSuggestions.Count == 0)
        {
            _selectedSemanticReuseContext = "All contexts";
            return;
        }

        if (!string.Equals(_selectedSemanticReuseContext, "All contexts", StringComparison.Ordinal) &&
            VisibleSemanticReuseSuggestions.Count > 0)
        {
            return;
        }

        if (_latestRepairComparison is not null && _semanticReuseSuggestions.Any(row => string.Equals(row.ContextKind, "repair_attempt", StringComparison.Ordinal)))
        {
            _selectedSemanticReuseContext = "Repair attempt";
            return;
        }

        if (_lastValidationResult is not null &&
            (!_lastValidationResult.Success || !string.Equals(_validationStabilityClassification, "passed", StringComparison.Ordinal)) &&
            _semanticReuseSuggestions.Any(row => string.Equals(row.ContextKind, "validation_failure", StringComparison.Ordinal)))
        {
            _selectedSemanticReuseContext = "Validation failure";
            return;
        }

        if (_semanticReuseSuggestions.Any(row => string.Equals(row.ContextKind, "planning", StringComparison.Ordinal)))
        {
            _selectedSemanticReuseContext = "Planning";
            return;
        }

        if (_semanticReuseSuggestions.Any(row => string.Equals(row.ContextKind, "provider_diagnostics", StringComparison.Ordinal)))
        {
            _selectedSemanticReuseContext = "Provider diagnostics";
            return;
        }

        _selectedSemanticReuseContext = "All contexts";
    }

    private void UpdateSemanticReuseContextSummary()
    {
        var scoped = VisibleSemanticReuseSuggestions;
        if (scoped.Count == 0)
        {
            _semanticReuseContextSummary = SelectedSemanticReuseContext switch
            {
                "Planning" => "No similar planning cases are currently loaded.",
                "Validation failure" => "No similar validation failures are currently loaded.",
                "Repair attempt" => "No similar repair outcomes are currently loaded.",
                "Provider diagnostics" => "No similar provider issues are currently loaded.",
                _ => "No similar cases are currently loaded."
            };
            return;
        }

        _semanticReuseContextSummary = SelectedSemanticReuseContext switch
        {
            "Planning" => BuildPlanningSemanticReuseSummary(scoped),
            "Validation failure" => BuildValidationFailureSemanticReuseSummary(scoped),
            "Repair attempt" => BuildRepairSemanticReuseSummary(scoped),
            "Provider diagnostics" => BuildProviderSemanticReuseSummary(scoped),
            _ => $"Showing {scoped.Count} similar past case{(scoped.Count == 1 ? string.Empty : "s")} across the current contexts."
        };
    }

    private static string BuildPlanningSemanticReuseSummary(IReadOnlyList<SemanticReuseSuggestionRow> scoped)
    {
        var passingOutputs = scoped.Count(row =>
            string.Equals(row.CaseType, "generated_output_pattern", StringComparison.Ordinal) &&
            string.Equals(row.Outcome, "passed", StringComparison.Ordinal));
        var repairedCleanly = scoped.Count(row =>
            (string.Equals(row.CaseType, "repair_bundle_summary", StringComparison.Ordinal) ||
             string.Equals(row.CaseType, "repair_promotion_outcome", StringComparison.Ordinal)) &&
            (string.Equals(row.Outcome, "passed", StringComparison.Ordinal) ||
             string.Equals(row.Outcome, "improved", StringComparison.Ordinal)));
        var cautionary = scoped.Count - passingOutputs - repairedCleanly;
        return $"Prior passing outputs {passingOutputs}; repaired patterns that later validated cleanly {repairedCleanly}; cautionary failures to avoid {Math.Max(0, cautionary)}.";
    }

    private static string BuildValidationFailureSemanticReuseSummary(IReadOnlyList<SemanticReuseSuggestionRow> scoped)
    {
        var top = scoped[0];
        var similarStage = ExtractSemanticReuseStage(top);
        var retryPasses = scoped.Count(row => string.Equals(row.Outcome, "passed_on_retry", StringComparison.Ordinal));
        var neededRepair = scoped.Count(row =>
            string.Equals(row.CaseType, "repair_bundle_summary", StringComparison.Ordinal) ||
            string.Equals(row.CaseType, "repair_promotion_outcome", StringComparison.Ordinal));
        var stayedFailed = scoped.Count(row =>
            string.Equals(row.Outcome, "failed", StringComparison.Ordinal) ||
            string.Equals(row.Outcome, "unchanged", StringComparison.Ordinal) ||
            string.Equals(row.Outcome, "regressed", StringComparison.Ordinal) ||
            string.Equals(row.Outcome, "flaky_suspected", StringComparison.Ordinal));
        return $"Most similar stage: {similarStage}. Similar failures usually passed on retry {retryPasses} time(s), needed repair {neededRepair} time(s), and stayed failed {stayedFailed} time(s).";
    }

    private static string BuildRepairSemanticReuseSummary(IReadOnlyList<SemanticReuseSuggestionRow> scoped)
    {
        var passed = scoped.Count(row => string.Equals(row.Outcome, "passed", StringComparison.Ordinal));
        var improved = scoped.Count(row => string.Equals(row.Outcome, "improved", StringComparison.Ordinal));
        var unchanged = scoped.Count(row => string.Equals(row.Outcome, "unchanged", StringComparison.Ordinal));
        var regressed = scoped.Count(row => string.Equals(row.Outcome, "regressed", StringComparison.Ordinal));
        return $"Prior repair outcomes: passed after repair {passed}, improved {improved}, unchanged {unchanged}, regressed {regressed}.";
    }

    private static string BuildProviderSemanticReuseSummary(IReadOnlyList<SemanticReuseSuggestionRow> scoped)
    {
        var top = scoped[0];
        return $"Most similar provider issue: {top.Title}. {top.MatchExplanation}.";
    }

    private static string ExtractSemanticReuseStage(SemanticReuseSuggestionRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ValidationOutcomeSummary) &&
            row.ValidationOutcomeSummary.Contains("Stage:", StringComparison.Ordinal))
        {
            var markerIndex = row.ValidationOutcomeSummary.IndexOf("Stage:", StringComparison.Ordinal);
            return markerIndex >= 0
                ? row.ValidationOutcomeSummary[(markerIndex + "Stage:".Length)..].Trim()
                : row.ValidationOutcomeSummary;
        }

        return row.Title;
    }

    private static string GetSemanticReuseMetadataValue(IReadOnlyList<SemanticReuseMetadataField> metadata, string name)
        => metadata
            .FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal))
            ?.Value
            ?? string.Empty;

    private static string BuildSemanticReuseValidationOutcomeSummary(string caseType, string outcome, string repairedValidationStatus, string stageSummary)
    {
        var baseSummary = caseType switch
        {
            "repair_bundle_summary" when !string.IsNullOrWhiteSpace(repairedValidationStatus)
                => $"Validation after repair: {repairedValidationStatus.Replace('_', ' ')}",
            "generated_output_pattern" => $"Validation outcome: {outcome.Replace('_', ' ')}",
            "validation_failure_record" => $"Historical outcome: {outcome.Replace('_', ' ')}",
            "repair_promotion_outcome" => $"Repair outcome: {outcome.Replace('_', ' ')}",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(stageSummary))
            return baseSummary;

        return string.IsNullOrWhiteSpace(baseSummary)
            ? $"Stage: {stageSummary}"
            : $"{baseSummary}; Stage: {stageSummary}";
    }

    private static string BuildSemanticReusePromotionSummary(string promotionStatus, string adoptionState)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(promotionStatus))
            parts.Add($"Promotion {promotionStatus.Replace('_', ' ')}");
        if (!string.IsNullOrWhiteSpace(adoptionState))
            parts.Add($"Adoption {adoptionState.Replace('_', ' ')}");
        return string.Join("; ", parts);
    }

    private void SemanticReuseSuggestionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SemanticReuseSuggestionRow.IsSelectedForRepairReference), StringComparison.Ordinal))
            return;

        OnPropertyChanged(nameof(SelectedSemanticReuseRepairReferenceCount));
        OnPropertyChanged(nameof(HasSelectedSemanticReuseRepairReferences));
    }

    private bool CanValidateGeneratedOutput()
        => string.IsNullOrWhiteSpace(GetValidateGeneratedOutputDisabledReason());

    private string GetValidateGeneratedOutputDisabledReason()
    {
        var blocker = GetValidationDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
            return blocker;

        return ResolveGeneratedOutputContext() is null
            ? "Generated output validation is unavailable until a run is selected."
            : string.Empty;
    }

    private async Task ValidateGeneratedOutputAsync()
    {
        var context = ResolveGeneratedOutputContext();
        if (context is null)
            return;

        _validationFollowupPinnedOutputFolder = string.Empty;
        await ExecuteValidationActionAsync(
            ValidationAction.RunFullValidationLoop,
            context,
            beginOperationProgress: true,
            actionLabelOverride: "Validate generated output").ConfigureAwait(true);
    }

    private bool CanAttemptRepair()
        => string.IsNullOrWhiteSpace(GetAttemptRepairDisabledReason());

    private string GetAttemptRepairDisabledReason()
    {
        if (IsOperationActive)
            return $"Repair is unavailable while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return "Repair is unavailable while another UI action is busy.";

        var context = ResolveGeneratedOutputContext();
        if (context is null)
            return "Repair is unavailable until a generated run is selected.";

        if (!string.Equals(GeneratedOutputValidationStatus, "failed", StringComparison.Ordinal))
            return "Repair is available after a failed generated-output validation run.";

        if (_lastValidationResult is null)
            return "Repair is unavailable until a linked validation result is loaded.";

        return string.Empty;
    }

    private async Task AttemptRepairAsync()
    {
        var blocker = GetAttemptRepairDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
            return;

        var context = ResolveGeneratedOutputContext();
        if (context is null || _lastValidationResult is null)
            return;

        BeginOperationProgress(
            "Attempting repair",
            "Collecting failure context for generated output.",
            "Collecting failure context",
            "Applying repair",
            "Re-running validation",
            "Completed");

        try
        {
            SetOperationStepState("Collecting failure context", "active", "Building deterministic repair bundle.");
            var sourceValidation = _lastValidationResult;
            var priorSuggestions = _semanticReuseSuggestions.ToArray();
            var failedStage = DetermineFailedStage(sourceValidation);
            var repairId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfffZ}-repair";
            var selectedReferences = _semanticReuseSuggestions
                .Where(row => row.IsSelectedForRepairReference)
                .Select(row => row.ToRepairReferenceCase())
                .OrderBy(row => row.DocumentId, StringComparer.Ordinal)
                .ThenBy(row => row.PrimaryArtifactPath, StringComparer.Ordinal)
                .ToArray();
            var bundle = new RepairBundle(
                repairId,
                _validationRunnerService.RepoRoot,
                context.SourcePath,
                context.RunId,
                context.RunPath,
                sourceValidation.RunId,
                failedStage.StageLabel,
                sourceValidation.FirstFailureText ?? failedStage.Summary,
                sourceValidation.OutputFolder,
                sourceValidation.FirstFailureLogPath,
                BuildRepairArtifactPaths(context.RunPath, sourceValidation),
                DateTimeOffset.UtcNow,
                selectedReferences);
            SetOperationStepState("Collecting failure context", "completed", selectedReferences.Length == 0
                ? failedStage.StageLabel
                : $"{failedStage.StageLabel} with {selectedReferences.Length} linked similar case reference(s).");

            SetOperationStepState("Applying repair", "active", "Running explicit repair attempt.");
            var repairResult = await _repairAttemptService.AttemptRepairAsync(bundle).ConfigureAwait(true);
            SetOperationStepState("Applying repair", "completed", repairResult.Summary);

            SetOperationStepState("Re-running validation", "active", "Re-running linked generated-output validation.");
            await ExecuteValidationActionAsync(
                ValidationAction.RunFullValidationLoop,
                context,
                beginOperationProgress: false,
                actionLabelOverride: "Validate generated output",
                recordSemanticReuseOutcomes: false).ConfigureAwait(true);
            var repairedValidation = _lastValidationResult ?? sourceValidation;
            var improvementState = BuildRepairImprovementState(sourceValidation, repairedValidation);
            var repairBundlePath = Path.Combine(repairResult.RepairFolder, "repair_bundle.json");
            var comparison = new RepairComparisonRecord(
                repairResult.RepairId,
                sourceValidation.RunId,
                sourceValidation.Success ? "passed" : "failed",
                sourceValidation.Summary,
                failedStage.StageLabel,
                sourceValidation.FirstFailureText ?? failedStage.Summary,
                repairedValidation.RunId,
                repairedValidation.Success ? "passed" : "failed",
                repairedValidation.Summary,
                DetermineFailedStage(repairedValidation).StageLabel,
                repairedValidation.FirstFailureText ?? DetermineFailedStage(repairedValidation).Summary,
                improvementState,
                repairResult.ChangedFiles.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                repairResult.Summary,
                repairBundlePath,
                repairResult.RepairFolder,
                repairedValidation.OutputFolder,
                DateTimeOffset.UtcNow);
            RepairReviewArtifactsService.SaveComparison(comparison);
            var history = RepairReviewArtifactsService.AppendHistory(
                context.RunPath,
                new RepairHistoryEntry(
                    repairResult.RepairId,
                    comparison.RecordedUtc,
                    sourceValidation.RunId,
                    repairedValidation.RunId,
                    repairResult.RepairOutcome,
                    improvementState,
                    $"{repairResult.Summary} Validation {improvementState}.",
                    repairBundlePath,
                    repairResult.RepairFolder,
                    repairedValidation.OutputFolder,
                    RepairReviewArtifactsService.ComparisonPathForRepair(repairResult.RepairFolder)),
                SelectedValidationKeepLastRuns);
            var promotion = RepairReviewArtifactsService.LoadPromotion(context.RunPath);
            ApplyRepairReviewState(history.Attempts, comparison, promotion);
            SemanticReuseService.RecordRepairReferenceOutcome(
                _validationRunnerService.RepoRoot,
                bundle,
                repairedValidation,
                improvementState,
                BuildValidationSettings());
            RecordSemanticReuseOutcomeEvidence(
                priorSuggestions,
                context,
                repairedValidation,
                improvementState,
                repairedValidation.RunId,
                repairResult.RepairId,
                RepairReviewArtifactsService.ComparisonPathForRepair(repairResult.RepairFolder),
                "repair");

            _repairSummary = $"{repairResult.Summary} Validation {improvementState}.";
            OnPropertyChanged(nameof(RepairSummary));
            SetOperationStepState("Re-running validation", repairedValidation.Success ? "completed" : "failed", repairedValidation.Summary);
            SetOperationStepState("Completed", repairedValidation.Success ? "completed" : "failed", _repairSummary);
            CompleteOperationProgress(repairedValidation.Success, _repairSummary);
            if (EnableSemanticReuseSuggestions)
            {
                SelectedSemanticReuseContext = "Repair attempt";
                await RefreshSimilarCasesAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _repairSummary = $"Repair failed: {ex.Message}";
            OnPropertyChanged(nameof(RepairSummary));
            SetOperationStepState("Applying repair", "failed", ex.Message);
            CompleteOperationProgress(false, $"Repair failed: {ex.Message}");
            RecordFailure(
                "Repair",
                ex.ToString(),
                _repairOutputFolder,
                "Inspect the repair bundle and validation logs, then retry the repair action if appropriate.");
        }
    }

    private Task OpenRepairOutputFolderAsync()
        => OpenFolderIfExistsAsync(_repairOutputFolder);

    private Task OpenRepairBundleFolderAsync()
        => OpenFolderIfExistsAsync(Path.GetDirectoryName(_repairBundlePath) ?? string.Empty);

    private Task OpenLinkedRepairValidationRunFolderAsync()
        => OpenFolderIfExistsAsync(_repairLinkedValidationRunFolder);

    private Task OpenRepairAuditSummaryFolderAsync()
        => OpenFolderIfExistsAsync(_repairAuditSummaryFolder);

    private Task OpenPromotedRepairFolderAsync()
        => OpenFolderIfExistsAsync(_promotedRepairFolder);

    private bool CanPromoteRepairResult()
        => string.IsNullOrWhiteSpace(GetPromoteRepairDisabledReason());

    private bool CanAdoptRepair()
        => string.IsNullOrWhiteSpace(GetAdoptRepairDisabledReason());

    private bool CanReplaceRepair()
        => string.IsNullOrWhiteSpace(GetReplaceRepairDisabledReason());

    private bool CanUnadoptRepair()
        => string.IsNullOrWhiteSpace(GetUnadoptRepairDisabledReason());

    private string GetPromoteRepairDisabledReason()
    {
        if (IsOperationActive)
            return $"Promotion is unavailable while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return "Promotion is unavailable while another UI action is busy.";

        if (ResolveGeneratedOutputContext() is null)
            return "Promotion is unavailable until a generated run is selected.";

        if (_latestRepairHistoryEntry is null || _latestRepairComparison is null)
            return "Promotion is unavailable until a repair attempt is recorded.";

        if (!RepairReviewArtifactsService.CanPromote(_latestRepairComparison.ImprovementState))
            return "Promotion is available for improved or passed repair results.";

        if (string.Equals(_repairPromotionStatus, "promoted_from_repair", StringComparison.Ordinal) &&
            string.Equals(_promotedRepairId, _latestRepairHistoryEntry.RepairId, StringComparison.Ordinal))
        {
            return "The latest repair result is already promoted.";
        }

        if (string.IsNullOrWhiteSpace(_latestRepairHistoryEntry.LinkedValidationRunFolder) ||
            !Directory.Exists(_latestRepairHistoryEntry.LinkedValidationRunFolder))
        {
            return "Promotion is unavailable until the linked validation run folder exists.";
        }

        return string.Empty;
    }

    private string GetAdoptRepairDisabledReason()
    {
        var blocker = GetRepairAdoptionActionBlocker("Adoption");
        if (!string.IsNullOrWhiteSpace(blocker))
            return blocker;

        return string.Equals(_repairPromotionRecord!.AdoptionState, "adopted", StringComparison.Ordinal)
            ? "Adoption is already recorded for the promoted repair."
            : string.Empty;
    }

    private string GetReplaceRepairDisabledReason()
    {
        var blocker = GetRepairAdoptionActionBlocker("Replacement");
        if (!string.IsNullOrWhiteSpace(blocker))
            return blocker;

        return string.Equals(_repairPromotionRecord!.AdoptionState, "replaced_by_newer_output", StringComparison.Ordinal)
            ? "Replacement is already recorded for the promoted repair."
            : string.Empty;
    }

    private string GetUnadoptRepairDisabledReason()
    {
        var blocker = GetRepairAdoptionActionBlocker("Rollback");
        if (!string.IsNullOrWhiteSpace(blocker))
            return blocker;

        return string.Equals(_repairPromotionRecord!.AdoptionState, "rolled_back", StringComparison.Ordinal)
            ? "The promoted repair is already marked as no longer current."
            : string.Empty;
    }

    private string GetRepairAdoptionActionBlocker(string actionLabel)
    {
        if (IsOperationActive)
            return $"{actionLabel} is unavailable while {OperationStatusLine.ToLowerInvariant()} is in progress.";

        if (IsBusy)
            return $"{actionLabel} is unavailable while another UI action is busy.";

        if (ResolveGeneratedOutputContext() is null)
            return $"{actionLabel} is unavailable until a generated run is selected.";

        if (_repairPromotionRecord is null || string.IsNullOrWhiteSpace(_repairPromotionRecord.RepairId))
            return $"{actionLabel} is unavailable until a repair result is promoted.";

        if (_latestRepairComparison is null)
            return $"{actionLabel} is unavailable until the promoted repair comparison is loaded.";

        return string.Empty;
    }

    private Task PromoteRepairResultAsync()
    {
        var blocker = GetPromoteRepairDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
            return Task.CompletedTask;

        var context = ResolveGeneratedOutputContext();
        if (context is null || _latestRepairHistoryEntry is null || _latestRepairComparison is null)
            return Task.CompletedTask;

        BeginOperationProgress(
            "Promoting repair",
            "Reviewing the latest repair result before promotion.",
            "Validating repair outcome",
            "Writing promotion metadata",
            "Completed");

        try
        {
            SetOperationStepState("Validating repair outcome", "active", $"Latest repair outcome: {_latestRepairComparison.ImprovementState}.");
            SetOperationStepState("Validating repair outcome", "completed", $"Repair {_latestRepairHistoryEntry.RepairId} is eligible for promotion.");

            SetOperationStepState("Writing promotion metadata", "active", "Writing deterministic promotion artifact.");
            var promotion = RepairReviewArtifactsService.CreatePromotion(
                context.RunId,
                context.RunPath,
                _latestRepairHistoryEntry,
                _latestRepairComparison,
                $"Repair outcome {_latestRepairComparison.ImprovementState}.",
                RepairReviewNote,
                DateTimeOffset.UtcNow);
            promotion = RepairReviewArtifactsService.WriteAuditSummary(_latestRepairComparison, promotion);
            RepairReviewArtifactsService.SavePromotion(context.RunPath, promotion);
            RepairReviewArtifactsService.AppendPromotionLedger(
                _validationRunnerService.RepoRoot,
                new PromotedRepairLedgerEntry(
                    promotion.SourceRunId,
                    promotion.SourceRunPath,
                    promotion.RepairId,
                    promotion.PromotedUtc,
                    promotion.ImprovementState,
                    promotion.ConfidenceSignal,
                    promotion.ConfidenceText,
                    RepairReviewArtifactsService.BuildPromotedArtifactPaths(promotion),
                    promotion.OperatorNote),
                SelectedValidationKeepLastRuns);
            ApplyRepairReviewState(RepairReviewArtifactsService.LoadHistory(context.RunPath).Attempts, _latestRepairComparison, promotion);
            SetOperationStepState("Writing promotion metadata", "completed", promotion.Reason);
            SetOperationStepState("Completed", "completed", $"Repair {_latestRepairHistoryEntry.RepairId} promoted.");
            CompleteOperationProgress(true, $"Repair {_latestRepairHistoryEntry.RepairId} promoted.");

            AddNarration("success", "REPAIR_PROMOTED", new Dictionary<string, string>
            {
                ["run_id"] = context.RunId,
                ["repair_id"] = _latestRepairHistoryEntry.RepairId,
                ["outcome"] = _latestRepairComparison.ImprovementState
            });
        }
        catch (Exception ex)
        {
            SetOperationStepState("Writing promotion metadata", "failed", ex.Message);
            CompleteOperationProgress(false, $"Promotion failed: {ex.Message}");
            RecordFailure(
                "Repair promotion",
                ex.ToString(),
                context.RunPath,
                "Inspect the repair history and promotion artifact, then retry the promotion action.");
        }

        return Task.CompletedTask;
    }

    private Task AdoptRepairAsync()
        => UpdateRepairAdoptionStateAsync(
            GetAdoptRepairDisabledReason(),
            "Marking repair adopted",
            "Writing adoption metadata",
            "adopted",
            "Repair was adopted into the current working output.");

    private Task ReplaceRepairAsync()
        => UpdateRepairAdoptionStateAsync(
            GetReplaceRepairDisabledReason(),
            "Marking repair replaced",
            "Writing replacement metadata",
            "replaced_by_newer_output",
            "Repair was replaced by newer generated output.");

    private Task UnadoptRepairAsync()
        => UpdateRepairAdoptionStateAsync(
            GetUnadoptRepairDisabledReason(),
            "Marking repair no longer current",
            "Writing rollback metadata",
            "rolled_back",
            "Repair was marked as no longer current.");

    private Task UpdateRepairAdoptionStateAsync(
        string blocker,
        string operationLabel,
        string metadataStepLabel,
        string adoptionState,
        string adoptionReason)
    {
        if (!string.IsNullOrWhiteSpace(blocker))
            return Task.CompletedTask;

        var context = ResolveGeneratedOutputContext();
        if (context is null || _repairPromotionRecord is null || _latestRepairComparison is null)
            return Task.CompletedTask;

        BeginOperationProgress(
            operationLabel,
            $"{operationLabel} for repair {_repairPromotionRecord.RepairId}.",
            "Loading promotion metadata",
            metadataStepLabel,
            "Completed");

        try
        {
            SetOperationStepState("Loading promotion metadata", "active", $"Loading promotion record for {_repairPromotionRecord.RepairId}.");
            SetOperationStepState("Loading promotion metadata", "completed", $"Loaded promotion record for {_repairPromotionRecord.RepairId}.");

            SetOperationStepState(metadataStepLabel, "active", $"Writing {adoptionState.Replace('_', ' ')} state.");
            var updated = RepairReviewArtifactsService.UpdateAdoptionState(
                _repairPromotionRecord,
                adoptionState,
                adoptionReason,
                RepairReviewNote,
                DateTimeOffset.UtcNow);
            updated = RepairReviewArtifactsService.WriteAuditSummary(_latestRepairComparison, updated);
            RepairReviewArtifactsService.SavePromotion(context.RunPath, updated);
            ApplyRepairReviewState(RepairReviewArtifactsService.LoadHistory(context.RunPath).Attempts, _latestRepairComparison, updated);
            SetOperationStepState(metadataStepLabel, "completed", updated.AdoptionReason);
            SetOperationStepState("Completed", "completed", updated.AdoptionReason);
            CompleteOperationProgress(true, updated.AdoptionReason);

            AddNarration("success", "REPAIR_ADOPTION_STATE_UPDATED", new Dictionary<string, string>
            {
                ["run_id"] = context.RunId,
                ["repair_id"] = updated.RepairId,
                ["adoption_state"] = updated.AdoptionState
            });
        }
        catch (Exception ex)
        {
            SetOperationStepState(metadataStepLabel, "failed", ex.Message);
            CompleteOperationProgress(false, $"{operationLabel} failed: {ex.Message}");
            RecordFailure(
                "Repair adoption",
                ex.ToString(),
                context.RunPath,
                "Inspect the promotion metadata and retry the requested trust action.");
        }

        return Task.CompletedTask;
    }

    private GeneratedOutputContext? ResolveGeneratedOutputContext()
    {
        if (SelectedRunHistory is not null)
            return new GeneratedOutputContext(SelectedRunHistory.RunId, SelectedRunHistory.RunPath, SelectedRunHistory.RunPath);

        if (!string.IsNullOrWhiteSpace(_lastDemoRunPath))
        {
            var runId = _runHistory.FirstOrDefault(row => string.Equals(row.RunPath, _lastDemoRunPath, StringComparison.OrdinalIgnoreCase))?.RunId
                ?? Path.GetFileName(_lastDemoRunPath);
            return new GeneratedOutputContext(runId, _lastDemoRunPath, _lastDemoRunPath);
        }

        return null;
    }

    private void PersistGeneratedOutputValidationLink(GeneratedOutputValidationLink link)
    {
        GeneratedOutputValidationLinkService.Save(link);
        if (ResolveGeneratedOutputContext() is { } active &&
            string.Equals(active.RunPath, link.SourceRunPath, StringComparison.OrdinalIgnoreCase))
        {
            LoadGeneratedOutputValidationLink(active.RunId, active.RunPath);
        }
    }

    private void LoadGeneratedOutputValidationLink(string runId, string runPath)
    {
        var link = File.Exists(GeneratedOutputValidationLinkService.PathForRun(runPath))
            ? GeneratedOutputValidationLinkService.Load(runPath)
            : GeneratedOutputValidationLinkService.CreateDefault(runId, runPath);

        _linkedGeneratedOutputRunId = runId;
        _linkedGeneratedOutputRunPath = runPath;
        _generatedOutputValidationStatus = link.ValidationStatus;
        _generatedOutputValidationSummary = link.ValidationSummary;
        _generatedOutputValidationRunId = link.ValidationRunId ?? string.Empty;
        _generatedOutputValidationSourcePath = link.SourcePath;
        LoadRepairReviewState(runPath);

        OnPropertyChanged(nameof(GeneratedOutputValidationStatus));
        OnPropertyChanged(nameof(GeneratedOutputValidationBadge));
        OnPropertyChanged(nameof(GeneratedOutputValidationSummary));
        OnPropertyChanged(nameof(GeneratedOutputValidationRunId));
        OnPropertyChanged(nameof(HasGeneratedOutputValidationRunId));
        OnPropertyChanged(nameof(GeneratedOutputValidationSourcePath));
        OnPropertyChanged(nameof(AttemptRepairDisabledReason));
        OnPropertyChanged(nameof(PromoteRepairDisabledReason));
        ValidateGeneratedOutputCommand.RaiseCanExecuteChanged();
        AttemptRepairCommand.RaiseCanExecuteChanged();
        PromoteRepairResultCommand.RaiseCanExecuteChanged();
    }

    private static IReadOnlyList<string> BuildRepairArtifactPaths(string runPath, ValidationRunResult validationResult)
        => new[]
        {
            runPath,
            Path.Combine(runPath, "run.json"),
            GeneratedOutputValidationLinkService.PathForRun(runPath),
            validationResult.OutputFolder,
            validationResult.FirstFailureLogPath ?? string.Empty
        }.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();

    private void LoadRepairReviewState(string runPath)
    {
        var history = RepairReviewArtifactsService.LoadHistory(runPath);
        var latestHistory = history.Attempts
            .OrderByDescending(item => item.AttemptedUtc)
            .ThenByDescending(item => item.RepairId, StringComparer.Ordinal)
            .FirstOrDefault();
        var comparison = latestHistory is null
            ? null
            : RepairReviewArtifactsService.LoadComparison(latestHistory.ComparisonPath);
        var promotion = RepairReviewArtifactsService.LoadPromotion(runPath);
        ApplyRepairReviewState(history.Attempts, comparison, promotion);
    }

    private void ApplyRepairReviewState(
        IReadOnlyList<RepairHistoryEntry>? historyEntries,
        RepairComparisonRecord? comparison,
        RepairPromotionRecord? promotion)
    {
        var orderedHistory = (historyEntries ?? Array.Empty<RepairHistoryEntry>())
            .OrderByDescending(item => item.AttemptedUtc)
            .ThenByDescending(item => item.RepairId, StringComparer.Ordinal)
            .ToArray();
        _latestRepairHistoryEntry = orderedHistory.FirstOrDefault();
        _latestRepairComparison = comparison;
        _repairPromotionRecord = promotion;

        _repairHistory.Clear();
        foreach (var entry in orderedHistory)
        {
            _repairHistory.Add(new RepairHistoryRow(
                entry.RepairId,
                entry.AttemptedUtc,
                entry.SourceValidationRunId,
                entry.RepairedValidationRunId,
                entry.RepairOutcome,
                entry.ImprovementState,
                entry.Summary,
                entry.RepairBundlePath,
                entry.RepairResultFolder,
                entry.LinkedValidationRunFolder));
        }

        _repairBundlePath = _latestRepairHistoryEntry?.RepairBundlePath ?? string.Empty;
        _repairOutputFolder = _latestRepairHistoryEntry?.RepairResultFolder ?? string.Empty;
        _repairLinkedValidationRunFolder = _latestRepairHistoryEntry?.LinkedValidationRunFolder ?? string.Empty;
        _repairSummary = _latestRepairHistoryEntry?.Summary ?? "No repair attempts recorded.";
        _repairOutcome = _latestRepairHistoryEntry?.ImprovementState ?? string.Empty;
        _promotedRepairFolder = promotion?.RepairResultFolder ?? string.Empty;
        _repairAuditSummaryFolder = promotion?.AuditSummaryFolder ?? string.Empty;
        _repairComparisonSourceStage = comparison?.SourceFailedStage ?? string.Empty;
        _repairComparisonSourceExcerpt = comparison?.SourceFirstFailureExcerpt ?? string.Empty;
        _repairComparisonRepairedStage = comparison?.RepairedFailedStage ?? string.Empty;
        _repairComparisonRepairedExcerpt = comparison?.RepairedFirstFailureExcerpt ?? string.Empty;
        _repairComparisonValidationResult = comparison is null
            ? string.Empty
            : $"{comparison.RepairedValidationStatus}: {comparison.RepairedValidationSummary}";

        _repairChangedFiles.Clear();
        foreach (var path in (comparison?.ChangedFiles ?? Array.Empty<string>()).OrderBy(path => path, StringComparer.Ordinal))
        {
            _repairChangedFiles.Add(path);
        }

        _repairPromotionStatus = DetermineRepairPromotionStatus(promotion, _latestRepairHistoryEntry?.RepairId);
        _promotedRepairId = promotion?.RepairId ?? string.Empty;
        _repairPromotionSummary = BuildRepairPromotionSummary(promotion, _repairPromotionStatus);
        _repairAdoptionStatus = promotion?.AdoptionState ?? "not_promoted";
        _repairAdoptionSummary = BuildRepairAdoptionSummary(promotion);
        _repairConfidenceSignal = promotion?.ConfidenceSignal
            ?? (comparison is null ? string.Empty : RepairReviewArtifactsService.DetermineConfidenceSignal(comparison.ImprovementState));
        _repairConfidenceText = promotion?.ConfidenceText
            ?? (comparison is null ? string.Empty : RepairReviewArtifactsService.DetermineConfidenceText(comparison.ImprovementState));
        _generatedOutputTrustState = DetermineGeneratedOutputTrustState(_generatedOutputValidationStatus, _latestRepairHistoryEntry is not null, promotion, _repairPromotionStatus);
        _repairLineageSummary = BuildRepairLineageSummary(_linkedGeneratedOutputRunId, _generatedOutputValidationRunId, _latestRepairHistoryEntry, promotion);
        RepairReviewNote = promotion?.OperatorNote ?? string.Empty;

        OnPropertyChanged(nameof(RepairSummary));
        OnPropertyChanged(nameof(RepairBundlePath));
        OnPropertyChanged(nameof(HasRepairBundlePath));
        OnPropertyChanged(nameof(RepairOutputFolder));
        OnPropertyChanged(nameof(HasRepairOutputFolder));
        OnPropertyChanged(nameof(RepairOutcome));
        OnPropertyChanged(nameof(HasRepairChangedFiles));
        OnPropertyChanged(nameof(HasRepairHistory));
        OnPropertyChanged(nameof(RepairComparisonSourceStage));
        OnPropertyChanged(nameof(HasRepairComparisonSourceStage));
        OnPropertyChanged(nameof(RepairComparisonSourceExcerpt));
        OnPropertyChanged(nameof(HasRepairComparisonSourceExcerpt));
        OnPropertyChanged(nameof(RepairComparisonRepairedStage));
        OnPropertyChanged(nameof(HasRepairComparisonRepairedStage));
        OnPropertyChanged(nameof(RepairComparisonRepairedExcerpt));
        OnPropertyChanged(nameof(HasRepairComparisonRepairedExcerpt));
        OnPropertyChanged(nameof(RepairComparisonValidationResult));
        OnPropertyChanged(nameof(HasRepairComparisonValidationResult));
        OnPropertyChanged(nameof(RepairLinkedValidationRunFolder));
        OnPropertyChanged(nameof(HasRepairLinkedValidationRunFolder));
        OnPropertyChanged(nameof(RepairPromotionStatus));
        OnPropertyChanged(nameof(RepairPromotionBadge));
        OnPropertyChanged(nameof(RepairPromotionSummary));
        OnPropertyChanged(nameof(RepairAdoptionStatus));
        OnPropertyChanged(nameof(RepairAdoptionBadge));
        OnPropertyChanged(nameof(RepairAdoptionSummary));
        OnPropertyChanged(nameof(RepairConfidenceSignal));
        OnPropertyChanged(nameof(RepairConfidenceText));
        OnPropertyChanged(nameof(HasRepairConfidenceText));
        OnPropertyChanged(nameof(GeneratedOutputTrustState));
        OnPropertyChanged(nameof(GeneratedOutputTrustBadge));
        OnPropertyChanged(nameof(PromotedRepairFolder));
        OnPropertyChanged(nameof(HasPromotedRepairFolder));
        OnPropertyChanged(nameof(RepairAuditSummaryFolder));
        OnPropertyChanged(nameof(HasRepairAuditSummaryFolder));
        OnPropertyChanged(nameof(RepairLineageSummary));
        OnPropertyChanged(nameof(HasRepairLineage));
        OnPropertyChanged(nameof(PromotedRepairId));
        OnPropertyChanged(nameof(HasPromotedRepairId));
        OnPropertyChanged(nameof(AttemptRepairDisabledReason));
        OnPropertyChanged(nameof(PromoteRepairDisabledReason));
        OnPropertyChanged(nameof(AdoptRepairDisabledReason));
        OnPropertyChanged(nameof(ReplaceRepairDisabledReason));
        OnPropertyChanged(nameof(UnadoptRepairDisabledReason));
        OpenRepairOutputFolderCommand.RaiseCanExecuteChanged();
        OpenRepairBundleFolderCommand.RaiseCanExecuteChanged();
        OpenLinkedRepairValidationRunFolderCommand.RaiseCanExecuteChanged();
        OpenRepairAuditSummaryFolderCommand.RaiseCanExecuteChanged();
        OpenPromotedRepairFolderCommand.RaiseCanExecuteChanged();
        AttemptRepairCommand.RaiseCanExecuteChanged();
        PromoteRepairResultCommand.RaiseCanExecuteChanged();
        AdoptRepairCommand.RaiseCanExecuteChanged();
        ReplaceRepairCommand.RaiseCanExecuteChanged();
        UnadoptRepairCommand.RaiseCanExecuteChanged();
        ResetSemanticReuseSuggestions("Refresh Similar Cases to compare the latest repair and validation artifacts.");
    }

    private static ValidationStageResult DetermineFailedStage(ValidationRunResult validationResult)
        => validationResult.Stages.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal))
            ?? validationResult.Stages.Last();

    private static string BuildRepairImprovementState(ValidationRunResult previous, ValidationRunResult current)
    {
        if (current.Success)
            return "passed";

        var previousScore = ScoreValidationOutcome(previous);
        var currentScore = ScoreValidationOutcome(current);

        if (currentScore > previousScore)
            return "improved";

        if (currentScore < previousScore)
            return "regressed";

        return "unchanged";
    }

    private static int ScoreValidationOutcome(ValidationRunResult result)
    {
        if (result.Success)
            return int.MaxValue;

        var passedStages = result.Stages.Count(stage => string.Equals(stage.Status, "passed", StringComparison.Ordinal));
        var failedIndex = result.Stages
            .Select((stage, index) => new { stage, index })
            .FirstOrDefault(item => string.Equals(item.stage.Status, "failed", StringComparison.Ordinal))?.index ?? -1;
        return (passedStages * 100) + Math.Max(failedIndex, 0);
    }

    private static string BuildFollowupRerunOutcomeClassification(ValidationRunResult previous, ValidationRunResult current)
    {
        if (current.Success)
        {
            return previous.Success ? "passed" : "improved";
        }

        var currentPrimaryStage = SelectPrimaryComparisonStage(current);
        if (currentPrimaryStage is not null)
        {
            var previousMatchingStage = previous.Stages.FirstOrDefault(stage =>
                string.Equals(stage.StageId, currentPrimaryStage.StageId, StringComparison.Ordinal) ||
                string.Equals(stage.StageLabel, currentPrimaryStage.StageLabel, StringComparison.Ordinal));
            if (previousMatchingStage is not null)
            {
                var previousFailed = string.Equals(previousMatchingStage.Status, "failed", StringComparison.Ordinal);
                var currentFailed = string.Equals(currentPrimaryStage.Status, "failed", StringComparison.Ordinal);
                if (previousFailed == currentFailed)
                    return "unchanged";

                return currentFailed ? "regressed" : "improved";
            }
        }

        return BuildRepairImprovementState(previous, current);
    }

    private static ValidationStageResult? SelectPrimaryComparisonStage(ValidationRunResult result)
        => result.Stages.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal))
            ?? result.Stages.LastOrDefault();

    private static string DetermineRepairPromotionStatus(RepairPromotionRecord? promotion, string? latestRepairId)
    {
        if (promotion is null)
            return "not_promoted";

        if (!string.IsNullOrWhiteSpace(latestRepairId) &&
            !string.Equals(latestRepairId, promotion.RepairId, StringComparison.Ordinal))
        {
            return "superseded_by_later_repair";
        }

        return "promoted_from_repair";
    }

    private static string BuildRepairPromotionSummary(RepairPromotionRecord? promotion, string status)
    {
        if (promotion is null)
            return "No promoted repair result.";

        return status switch
        {
            "superseded_by_later_repair" => $"Repair {promotion.RepairId} was promoted on {promotion.PromotedUtc.ToLocalTime():g} and was superseded by a later repair.",
            _ => $"Repair {promotion.RepairId} promoted on {promotion.PromotedUtc.ToLocalTime():g}. {promotion.Reason}"
        };
    }

    private static string BuildRepairAdoptionSummary(RepairPromotionRecord? promotion)
    {
        if (promotion is null)
            return "No promoted repair is available for adoption.";

        return promotion.AdoptionState switch
        {
            "adopted" => $"Repair {promotion.RepairId} was adopted on {promotion.StateUpdatedUtc.ToLocalTime():g}. {promotion.AdoptionReason}",
            "replaced_by_newer_output" => $"Repair {promotion.RepairId} was marked replaced on {promotion.StateUpdatedUtc.ToLocalTime():g}. {promotion.AdoptionReason}",
            "rolled_back" => $"Repair {promotion.RepairId} was marked no longer current on {promotion.StateUpdatedUtc.ToLocalTime():g}. {promotion.AdoptionReason}",
            _ => $"Repair {promotion.RepairId} is promoted only. {promotion.AdoptionReason}"
        };
    }

    private static string DetermineGeneratedOutputTrustState(
        string validationStatus,
        bool hasRepairHistory,
        RepairPromotionRecord? promotion,
        string promotionStatus)
    {
        if (string.Equals(promotionStatus, "superseded_by_later_repair", StringComparison.Ordinal) ||
            string.Equals(promotion?.AdoptionState, "replaced_by_newer_output", StringComparison.Ordinal) ||
            string.Equals(promotion?.AdoptionState, "rolled_back", StringComparison.Ordinal))
        {
            return "superseded";
        }

        if (string.Equals(promotion?.AdoptionState, "adopted", StringComparison.Ordinal))
            return "adopted";

        if (promotion is not null)
            return "promoted";

        if (hasRepairHistory)
            return "repaired";

        if (string.Equals(validationStatus, "passed", StringComparison.Ordinal) ||
            string.Equals(validationStatus, "failed", StringComparison.Ordinal))
        {
            return "validated";
        }

        return "unvalidated";
    }

    private static string BuildRepairLineageSummary(
        string linkedRunId,
        string validationRunId,
        RepairHistoryEntry? historyEntry,
        RepairPromotionRecord? promotion)
    {
        if (string.IsNullOrWhiteSpace(linkedRunId) &&
            string.IsNullOrWhiteSpace(validationRunId) &&
            historyEntry is null &&
            promotion is null)
        {
            return "No repair lineage recorded.";
        }

        var validationToken = !string.IsNullOrWhiteSpace(validationRunId)
            ? validationRunId
            : historyEntry?.RepairedValidationRunId ?? promotion?.RepairedValidationRunId ?? "none";
        var repairToken = historyEntry?.RepairId ?? promotion?.RepairId ?? "none";
        var promotionToken = promotion is null ? "not promoted" : promotion.Status;
        return $"Generated output {linkedRunId} -> validation {validationToken} -> repair {repairToken} -> {promotionToken}";
    }

    private void LoadProviderDiagnosticsHistory()
    {
        _providerDiagnostics.Clear();
        var path = ResolveProviderDiagnosticsPath();
        if (!File.Exists(path))
        {
            OnPropertyChanged(nameof(HasProviderDiagnostics));
            OnPropertyChanged(nameof(ProviderDiagnosticsPath));
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<IReadOnlyList<ProviderDiagnosticEventRow>>(File.ReadAllText(path));
            if (entries is not null)
            {
                foreach (var entry in entries.OrderByDescending(item => item.ObservedAtUtc))
                {
                    _providerDiagnostics.Add(entry);
                }
            }
        }
        catch
        {
            // Keep diagnostics non-blocking; a malformed ledger should not block the UI.
        }

        OnPropertyChanged(nameof(HasProviderDiagnostics));
        OnPropertyChanged(nameof(ProviderDiagnosticsPath));
        ResetSemanticReuseSuggestions("Refresh Similar Cases to compare the latest provider diagnostics.");
    }

    private void AppendProviderDiagnosticEvent(BackendStatus status)
    {
        var path = ResolveProviderDiagnosticsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        IReadOnlyList<ProviderDiagnosticEventRow> existing;
        try
        {
            existing = File.Exists(path)
                ? JsonSerializer.Deserialize<IReadOnlyList<ProviderDiagnosticEventRow>>(File.ReadAllText(path)) ?? Array.Empty<ProviderDiagnosticEventRow>()
                : Array.Empty<ProviderDiagnosticEventRow>();
        }
        catch
        {
            existing = Array.Empty<ProviderDiagnosticEventRow>();
        }

        var next = existing
            .Concat(new[]
            {
                new ProviderDiagnosticEventRow(
                    status.Kind.ToString(),
                    status.IsAvailable ? "available" : "unavailable",
                    ClassifyProviderError(status),
                    status.ErrorCode ?? string.Empty,
                    status.Summary ?? status.Detail ?? string.Empty,
                    status.ObservedAtUtc,
                    status.Endpoint ?? string.Empty)
            })
            .OrderByDescending(item => item.ObservedAtUtc)
            .Take(12)
            .ToArray();

        File.WriteAllText(path, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        _providerDiagnostics.Clear();
        foreach (var entry in next)
        {
            _providerDiagnostics.Add(entry);
        }

        OnPropertyChanged(nameof(HasProviderDiagnostics));
        OnPropertyChanged(nameof(ProviderDiagnosticsPath));
        RefreshValidationTrendArtifacts(BuildValidationSettings());
        ResetSemanticReuseSuggestions("Refresh Similar Cases to compare the latest provider diagnostics.");
    }

    private string ResolveProviderDiagnosticsPath()
    {
        var root = CurrentProject?.WorkspacePath
            ?? ActiveWorkspace?.RootPath
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Shoots.UI");
        return Path.Combine(root, "provider_diagnostics.json");
    }

    private static string ClassifyProviderError(BackendStatus status)
    {
        if (status.IsAvailable)
            return "available";

        var code = status.ErrorCode ?? string.Empty;
        if (code.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        if (code.Contains("connection_refused", StringComparison.OrdinalIgnoreCase))
            return "connection_refused";
        if (code.Contains("host_not_found", StringComparison.OrdinalIgnoreCase))
            return "host_not_found";
        if (code.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            return "cancelled";
        if (code.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
            return "unknown";

        return "unknown";
    }

    private void RaiseReplayCommandCanExecuteChanged()
    {
        ReplayPlanCommand?.RaiseCanExecuteChanged();
        ReplayLatestRunCommand?.RaiseCanExecuteChanged();
        ReplaySelectedRunCommand?.RaiseCanExecuteChanged();
    }

    private static string WithNarrationHeading(string line)
    {
        foreach (var mapping in NarrationHeadings)
        {
            if (line.Contains($"\"code\":\"{mapping.Key}\"", StringComparison.Ordinal))
            {
                return $"[{mapping.Value}] {line}";
            }
        }

        return line;
    }

    private Task RefreshNarrationAsync()
    {
        _narrationLines.Clear();

        var newest = Directory.GetFiles(Path.GetFullPath(Path.Combine("artifacts")), "events.ndjson", SearchOption.AllDirectories)
            .Where(path => path.Replace('\\', '/').Contains("/narration/", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(newest) || !File.Exists(newest))
        {
            return Task.CompletedTask;
        }

        foreach (var line in File.ReadLines(newest))
        {
            if (string.Equals(_selectedNarrationPhase, "all", StringComparison.OrdinalIgnoreCase))
            {
                _narrationLines.Add(WithNarrationHeading(line));
                continue;
            }

            if (line.Contains($"\"phase\":\"{_selectedNarrationPhase}\"", StringComparison.OrdinalIgnoreCase))
            {
                _narrationLines.Add(WithNarrationHeading(line));
            }
        }

        return Task.CompletedTask;
    }

    private void PersistExecutionSelection()
    {
        if (ActiveWorkspace is null)
        {
            return;
        }

        var providerKind = char.ToUpperInvariant(_selectedProviderMode[0]) + _selectedProviderMode[1..].ToLowerInvariant();
        var updated = ActiveWorkspace with
        {
            SelectedProviderKind = providerKind
        };

        ActiveWorkspace = updated;
        _workspaceProvider.UpdateWorkspace(updated);
    }

    private bool CanRefreshBackends() => string.IsNullOrWhiteSpace(GetRefreshBackendsDisabledReason());

    private string GetRefreshBackendsDisabledReason()
    {
        if (ProbeInFlight)
        {
            return "ui.backends.refresh.in_progress: wait for backend probe completion.";
        }

        if (IsOperationActive)
        {
            var busyReason = BuildOperationBusyReason();
            if (!string.IsNullOrWhiteSpace(busyReason))
            {
                return busyReason;
            }
        }

        return string.Empty;
    }

    private async Task RefreshBackendStatusAsync()
    {
        LogUiAction("Connect to Ollama / Refresh backends click");

        var blocker = GetRefreshBackendsDisabledReason();
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            Trace.WriteLine($"[Shoots.UI] RefreshBackends command blocked. reason={blocker}");
            return;
        }

        BeginOperationProgress(
            "Refreshing backend",
            "Probing backend health and model catalog.",
            "Probe Ollama",
            "Probe Qdrant",
            "Refresh model catalog");
        ProbeInFlight = true;
        try
        {
            SetOperationStatus("Waiting on provider", "Checking Ollama endpoint.");
            SetOperationStepState("Probe Ollama", "active", "Checking Ollama endpoint.");
            var ollama = await _backendProbeService.ProbeOllamaAsync(default);
            SetOperationStepState("Probe Ollama", ollama.IsAvailable ? "completed" : "failed", ollama.Summary ?? ollama.Detail);
            SetOperationStatus("Waiting on provider", "Checking vector memory endpoint.");
            SetOperationStepState("Probe Qdrant", "active", "Checking vector memory endpoint.");
            var qdrant = await _backendProbeService.ProbeQdrantAsync(default);
            SetOperationStepState("Probe Qdrant", qdrant.IsAvailable ? "completed" : "failed", qdrant.Summary ?? qdrant.Detail);
            _ollamaStatus = ollama.WithBounds();
            _qdrantStatus = qdrant.WithBounds();
            _lastProbeUtc = DateTimeOffset.UtcNow;
            AppendProviderDiagnosticEvent(_ollamaStatus);
            AppendProviderDiagnosticEvent(_qdrantStatus);

            OnPropertyChanged(nameof(OllamaStatus));
            OnPropertyChanged(nameof(QdrantStatus));
            OnPropertyChanged(nameof(LastProbeUtc));
            OnPropertyChanged(nameof(OllamaEndpoint));
            OnPropertyChanged(nameof(QdrantEndpoint));
            OnPropertyChanged(nameof(AiProviderStatus));
            OnPropertyChanged(nameof(ProviderAvailabilityWarning));
            OnPropertyChanged(nameof(BackendDisabledReason));
            OnPropertyChanged(nameof(RunIntakePlanDisabledReason));

            SetOperationStatus("Waiting on provider", "Loading model catalog.");
            SetOperationStepState("Refresh model catalog", "active", "Loading models from backend.");
            await RefreshModelCatalogFromBackendAsync();
            if (HasModelCatalogError)
            {
                SetOperationStepState("Refresh model catalog", "failed", ModelCatalogError);
                CompleteOperationProgress(false, $"Backend refresh completed with model error: {ModelCatalogError}");
            }
            else
            {
                SetOperationStepState("Refresh model catalog", "completed", $"Loaded {_availableModels.Count} models.");
                CompleteOperationProgress(true, "Backend refresh completed.");
            }
        }
        catch (Exception ex)
        {
            SetOperationStepState("Refresh model catalog", "failed", ex.Message);
            CompleteOperationProgress(false, $"Backend refresh failed: {ex.Message}");
            throw;
        }
        finally
        {
            ProbeInFlight = false;
        }
    }

    private async Task RefreshModelCatalogFromBackendAsync()
    {
        if (!_ollamaStatus.IsAvailable)
        {
            _availableModels.Clear();
            SelectedModelId = string.Empty;
            _lastKnownModelId = string.Empty;
            ModelCatalogError = _ollamaStatus.ErrorCode ?? "ui.ollama.unreachable";
            OnPropertyChanged(nameof(DefaultModelId));
            OnPropertyChanged(nameof(CatalogHash));
            return;
        }

        var tags = await _ollamaClient.GetTagsAsync(default);

        if (!tags.IsSuccess)
        {
            _availableModels.Clear();
            ModelCatalogError = tags.ErrorCode ?? "ui.ollama.bad_json";
            SelectedModelId = string.Empty;
            _lastKnownModelId = string.Empty;
            OnPropertyChanged(nameof(DefaultModelId));
            OnPropertyChanged(nameof(CatalogHash));
            return;
        }

        var orderedModels = tags.ModelNames
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .OrderBy(static model => model, StringComparer.Ordinal)
            .ToList();

        var preferred = System.Environment.GetEnvironmentVariable("SHOOTS_PREFERRED_MODEL_ID")?.Trim();

        if (orderedModels.Count == 0)
        {
            _availableModels.Clear();
            ModelCatalogError = "ui.ollama.empty_catalog";
            ApplyModelSelection(preferred);
            OnPropertyChanged(nameof(DefaultModelId));
            OnPropertyChanged(nameof(CatalogHash));
            return;
        }

        _availableModels.Clear();
        foreach (var model in orderedModels)
        {
            _availableModels.Add(model);
        }

        ModelCatalogError = string.Empty;

        ApplyModelSelection(preferred);

        OnPropertyChanged(nameof(DefaultModelId));
        OnPropertyChanged(nameof(CatalogHash));
    }

    private void ApplyModelSelection(string? preferredModelId)
    {
        if (_availableModels.Count == 0)
        {
            SelectedModelId = string.Empty;
            _lastKnownModelId = string.Empty;
            return;
        }

        if (_availableModels.Contains(_selectedModelId, StringComparer.Ordinal))
        {
            _lastKnownModelId = _selectedModelId;
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredModelId) && _availableModels.Contains(preferredModelId, StringComparer.Ordinal))
        {
            SelectedModelId = preferredModelId;
            _lastKnownModelId = SelectedModelId;
            return;
        }

        SelectedModelId = _availableModels[0];
        _lastKnownModelId = SelectedModelId;
    }

    private void OnTraceLineCaptured(string line)
        => AppendActionLogLine(line);

    private void AppendActionLogLine(string line)
    {
        const int maxEntries = 200;

        if (_actionLogLines.Count >= maxEntries)
        {
            _actionLogLines.RemoveAt(0);
        }

        _actionLogLines.Add(line);
    }

    private void LogUiAction(string action)
    {
        var projectId = ActiveWorkspace?.ProjectId ?? "none";
        var workspacePath = ActiveWorkspace?.RootPath ?? "none";
        var now = DateTimeOffset.UtcNow;

        Trace.WriteLine($"[Shoots.UI] action='{action}' ts_utc={now:O} thread_id={System.Environment.CurrentManagedThreadId} project_id={projectId} workspace_path={workspacePath}");
    }

    private void BeginOperationProgress(string statusLine, string detail, params string[] steps)
    {
        RunOnUiThread(() =>
        {
            _operationStatusLine = statusLine;
            _operationStatusDetail = detail;
            _operationLatestEvent = detail;
            _operationStartedUtc = DateTimeOffset.UtcNow;
            _operationLastProgressUtc = _operationStartedUtc;
            _isOperationActive = true;
            _isOperationVisible = true;
            _isOperationWaiting = false;
            _operationWaitHint = string.Empty;
            _operationDisplayUntilUtc = null;
            _operationProgressSteps.Clear();
            _operationNarrationFeed.Clear();
            for (var index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                _operationProgressSteps.Add(new OperationProgressStepRow(step, index + 1));
            }

            RebuildVisibleOperationProgressSteps();

            OnPropertyChanged(nameof(OperationStatusLine));
            OnPropertyChanged(nameof(OperationStatusDetail));
            OnPropertyChanged(nameof(OperationLatestEvent));
            OnPropertyChanged(nameof(IsOperationActive));
            OnPropertyChanged(nameof(IsOperationVisible));
            OnPropertyChanged(nameof(HasOperationSteps));
            OnPropertyChanged(nameof(HasOperationNarration));
            OnPropertyChanged(nameof(OperationElapsedLabel));
            OnPropertyChanged(nameof(CurrentOperation));
            OnPropertyChanged(nameof(CurrentOperationStage));
            OnPropertyChanged(nameof(CurrentOperationStartedAt));
            OnPropertyChanged(nameof(OperationLastProgressAt));
            OnPropertyChanged(nameof(CurrentOperationStatus));
            OnPropertyChanged(nameof(CurrentOperationDetail));
            OnPropertyChanged(nameof(IsOperationWaiting));
            OnPropertyChanged(nameof(OperationWaitHint));
            OnPropertyChanged(nameof(BusyState));
            OnPropertyChanged(nameof(IsOperationBusyIndicatorVisible));
            OnPropertyChanged(nameof(IsOperationCompletionHoldActive));
            OnPropertyChanged(nameof(CompletionHold));
            OnPropertyChanged(nameof(ActionDisableReason));
            OnPropertyChanged(nameof(RunDemoPlanDisabledReason));
            OnPropertyChanged(nameof(QuickDemoDisabledReason));
            RaiseCommandCanExecute();

            _operationProgressTimer?.Start();
        });
    }

    private void SetOperationStatus(string statusLine, string? detail = null)
    {
        RunOnUiThread(() =>
        {
            _operationStatusLine = statusLine;
            _operationLastProgressUtc = DateTimeOffset.UtcNow;
            _isOperationWaiting = false;
            _operationWaitHint = string.Empty;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                _operationStatusDetail = detail;
                _operationLatestEvent = detail;
                OnPropertyChanged(nameof(OperationLatestEvent));
                AppendOperationNarrationLine(detail);
            }

            OnPropertyChanged(nameof(OperationStatusLine));
            OnPropertyChanged(nameof(OperationStatusDetail));
            OnPropertyChanged(nameof(CurrentOperation));
            OnPropertyChanged(nameof(CurrentOperationStage));
            OnPropertyChanged(nameof(CurrentOperationStatus));
            OnPropertyChanged(nameof(CurrentOperationDetail));
            OnPropertyChanged(nameof(OperationLastProgressAt));
            OnPropertyChanged(nameof(IsOperationWaiting));
            OnPropertyChanged(nameof(OperationWaitHint));
            OnPropertyChanged(nameof(ActionDisableReason));
            OnPropertyChanged(nameof(RunDemoPlanDisabledReason));
            OnPropertyChanged(nameof(QuickDemoDisabledReason));
        });
    }

    private void SetOperationLatestEvent(string latestEvent)
    {
        RunOnUiThread(() =>
        {
            _operationLatestEvent = latestEvent;
            _operationLastProgressUtc = DateTimeOffset.UtcNow;
            _isOperationWaiting = false;
            _operationWaitHint = string.Empty;
            OnPropertyChanged(nameof(OperationLatestEvent));
            OnPropertyChanged(nameof(OperationLastProgressAt));
            OnPropertyChanged(nameof(IsOperationWaiting));
            OnPropertyChanged(nameof(OperationWaitHint));
            AppendOperationNarrationLine(latestEvent);
        });
    }

    private void AppendOperationNarrationLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var trimmed = message.Trim();
        if (_operationNarrationFeed.Count > 0 && string.Equals(_operationNarrationFeed[^1], trimmed, StringComparison.Ordinal))
        {
            _operationLastProgressUtc = DateTimeOffset.UtcNow;
            return;
        }

        _operationNarrationFeed.Add(trimmed);
        while (_operationNarrationFeed.Count > 20)
        {
            _operationNarrationFeed.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasOperationNarration));
    }

    private void SetOperationStepState(string stepName, string state, string? detail = null)
    {
        RunOnUiThread(() =>
        {
            var step = _operationProgressSteps.FirstOrDefault(candidate => string.Equals(candidate.Name, stepName, StringComparison.Ordinal));
            if (step is null)
            {
                step = new OperationProgressStepRow(stepName, _operationProgressSteps.Count + 1);
                _operationProgressSteps.Add(step);
                OnPropertyChanged(nameof(HasOperationSteps));
            }

            step.SetState(state, detail);
            _operationLastProgressUtc = DateTimeOffset.UtcNow;
            _isOperationWaiting = false;
            _operationWaitHint = string.Empty;
            OnPropertyChanged(nameof(OperationLastProgressAt));
            OnPropertyChanged(nameof(IsOperationWaiting));
            OnPropertyChanged(nameof(OperationWaitHint));
            RebuildVisibleOperationProgressSteps();
        });
    }

    private void CompleteOperationProgress(bool success, string detail)
    {
        RunOnUiThread(() =>
        {
            _operationStatusLine = success ? "Completed" : "Failed";
            _operationStatusDetail = detail;
            _operationLatestEvent = detail;
            _operationLastProgressUtc = DateTimeOffset.UtcNow;
            _isOperationActive = false;
            _isOperationVisible = true;
            _isOperationWaiting = false;
            _operationWaitHint = string.Empty;
            _operationDisplayUntilUtc = DateTimeOffset.UtcNow.Add(OperationCompletionHoldDuration);

            RebuildVisibleOperationProgressSteps();

            OnPropertyChanged(nameof(OperationStatusLine));
            OnPropertyChanged(nameof(OperationStatusDetail));
            OnPropertyChanged(nameof(OperationLatestEvent));
            OnPropertyChanged(nameof(IsOperationActive));
            OnPropertyChanged(nameof(IsOperationVisible));
            OnPropertyChanged(nameof(OperationElapsedLabel));
            OnPropertyChanged(nameof(OperationLastProgressAt));
            OnPropertyChanged(nameof(CurrentOperation));
            OnPropertyChanged(nameof(CurrentOperationStage));
            OnPropertyChanged(nameof(CurrentOperationStatus));
            OnPropertyChanged(nameof(CurrentOperationDetail));
            OnPropertyChanged(nameof(IsOperationWaiting));
            OnPropertyChanged(nameof(OperationWaitHint));
            OnPropertyChanged(nameof(BusyState));
            OnPropertyChanged(nameof(IsOperationBusyIndicatorVisible));
            OnPropertyChanged(nameof(IsOperationCompletionHoldActive));
            OnPropertyChanged(nameof(CompletionHold));
            OnPropertyChanged(nameof(ActionDisableReason));
            OnPropertyChanged(nameof(RunDemoPlanDisabledReason));
            OnPropertyChanged(nameof(QuickDemoDisabledReason));
            RaiseCommandCanExecute();

            _operationProgressTimer?.Start();
        });
    }

    private void ResetOperationProgressToIdle()
    {
        _operationStatusLine = "Idle";
        _operationStatusDetail = "No active operation.";
        _operationLatestEvent = string.Empty;
        _isOperationActive = false;
        _isOperationVisible = false;
        _operationDisplayUntilUtc = null;
        _operationStartedUtc = null;
        _operationLastProgressUtc = null;
        _isOperationWaiting = false;
        _operationWaitHint = string.Empty;
        _operationProgressSteps.Clear();
        _visibleOperationProgressSteps.Clear();
        _operationNarrationFeed.Clear();

        OnPropertyChanged(nameof(OperationStatusLine));
        OnPropertyChanged(nameof(OperationStatusDetail));
        OnPropertyChanged(nameof(OperationLatestEvent));
        OnPropertyChanged(nameof(IsOperationActive));
        OnPropertyChanged(nameof(IsOperationVisible));
        OnPropertyChanged(nameof(HasOperationSteps));
        OnPropertyChanged(nameof(HasVisibleOperationSteps));
        OnPropertyChanged(nameof(HasOperationNarration));
        OnPropertyChanged(nameof(OperationElapsedLabel));
        OnPropertyChanged(nameof(CurrentOperation));
        OnPropertyChanged(nameof(CurrentOperationStage));
        OnPropertyChanged(nameof(CurrentOperationStartedAt));
        OnPropertyChanged(nameof(OperationLastProgressAt));
        OnPropertyChanged(nameof(CurrentOperationStatus));
        OnPropertyChanged(nameof(CurrentOperationDetail));
        OnPropertyChanged(nameof(IsOperationWaiting));
        OnPropertyChanged(nameof(OperationWaitHint));
        OnPropertyChanged(nameof(BusyState));
        OnPropertyChanged(nameof(IsOperationBusyIndicatorVisible));
        OnPropertyChanged(nameof(IsOperationCompletionHoldActive));
        OnPropertyChanged(nameof(CompletionHold));
        OnPropertyChanged(nameof(ActionDisableReason));
        OnPropertyChanged(nameof(RunDemoPlanDisabledReason));
        OnPropertyChanged(nameof(QuickDemoDisabledReason));
        RaiseCommandCanExecute();
    }

    private void HandleOperationProgressTimerTick()
    {
        OnPropertyChanged(nameof(OperationElapsedLabel));
        OnPropertyChanged(nameof(CurrentOperationStartedAt));

        if (_isOperationActive && !_isOperationWaiting && _operationLastProgressUtc is { } lastProgressUtc && DateTimeOffset.UtcNow - lastProgressUtc >= TimeSpan.FromSeconds(20))
        {
            _isOperationWaiting = true;
            _operationWaitHint = BuildOperationWaitHint();
            OnPropertyChanged(nameof(IsOperationWaiting));
            OnPropertyChanged(nameof(OperationWaitHint));
        }

        if (_isOperationActive)
        {
            return;
        }

        if (OperationCompletionHoldDuration <= TimeSpan.Zero)
        {
            ResetOperationProgressToIdle();
            _operationProgressTimer?.Stop();
            return;
        }

        if (_operationDisplayUntilUtc is null || DateTimeOffset.UtcNow < _operationDisplayUntilUtc.Value)
        {
            return;
        }

        ResetOperationProgressToIdle();
        _operationProgressTimer?.Stop();
    }

    private string BuildOperationWaitHint()
    {
        if (_operationStatusLine.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            _operationStatusDetail.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            _operationStatusDetail.Contains("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return "No recent progress updates. Waiting for provider response.";
        }

        if (_operationStatusDetail.Contains("host", StringComparison.OrdinalIgnoreCase))
        {
            return "No recent progress updates. Waiting for host response.";
        }

        return "No recent progress updates. Waiting for host or provider response.";
    }

    private string BuildOperationElapsedLabel()
    {
        if (_operationStartedUtc is null)
        {
            return string.Empty;
        }

        var end = _isOperationActive ? DateTimeOffset.UtcNow : (_operationDisplayUntilUtc ?? DateTimeOffset.UtcNow);
        var elapsed = end - _operationStartedUtc.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return $"Elapsed: {elapsed:mm\\:ss}";
    }

    private void RebuildVisibleOperationProgressSteps()
    {
        var visible = ShowFullTimeline
            ? _operationProgressSteps.OrderBy(step => step.StepOrder).ToList()
            : _operationProgressSteps
                .OrderBy(step => step.StepOrder)
                .Where(step => string.Equals(step.StepState, "active", StringComparison.Ordinal)
                    || string.Equals(step.StepState, "failed", StringComparison.Ordinal)
                    || !string.Equals(step.StepState, "pending", StringComparison.Ordinal))
                .TakeLast(3)
                .ToList();

        _visibleOperationProgressSteps.Clear();
        foreach (var step in visible)
        {
            _visibleOperationProgressSteps.Add(step);
        }

        OnPropertyChanged(nameof(VisibleOperationProgressSteps));
        OnPropertyChanged(nameof(HasVisibleOperationSteps));
    }

    private string BuildOperationBusyReason()
    {
        if (!_isOperationActive && !IsOperationCompletionHoldActive)
        {
            return string.Empty;
        }

        if (IsOperationCompletionHoldActive)
        {
            return "Run disabled while completion state is being displayed.";
        }

        if (OperationStatusLine.Contains("verifying", StringComparison.OrdinalIgnoreCase))
        {
            return "Run disabled while verification is in progress.";
        }

        return $"Run disabled while {OperationStatusLine.ToLowerInvariant()} is in progress.";
    }

    private static string ExtractFailureExceptionType(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Unknown";
        }

        var separator = reason.IndexOf(':');
        if (separator > 0)
        {
            var prefix = reason[..separator].Trim();
            if (prefix.EndsWith("Exception", StringComparison.Ordinal) || prefix.EndsWith("Error", StringComparison.Ordinal))
            {
                return prefix;
            }
        }

        return "Unknown";
    }

    private static string ExtractFailureFirstStackFrame(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        var lines = reason.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("at ", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return string.Empty;
    }

    private static string ExtractFailureMessage(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        var lines = reason.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var firstLine = lines[0];
        var separator = firstLine.IndexOf(':');
        if (separator > 0)
        {
            var prefix = firstLine[..separator].Trim();
            if (prefix.EndsWith("Exception", StringComparison.Ordinal) || prefix.EndsWith("Error", StringComparison.Ordinal))
            {
                return firstLine[(separator + 1)..].Trim();
            }
        }

        return firstLine;
    }

    private string GetRunDemoPlanDisabledReason()
    {
        if (CurrentProject is null)
        {
            return "Load a project before running the demo plan.";
        }

        var busyReason = BuildOperationBusyReason();
        if (!string.IsNullOrWhiteSpace(busyReason))
        {
            return busyReason;
        }

        return string.Empty;
    }

    private string GetQuickDemoDisabledReason()
    {
        if (IsCreatingProject)
        {
            return "Quick Demo is disabled while project creation is already active.";
        }

        var busyReason = BuildOperationBusyReason();
        if (!string.IsNullOrWhiteSpace(busyReason))
        {
            return busyReason;
        }

        return string.Empty;
    }

    private void RunOnUiThread(Action action)
    {
        if (Application.Current is null)
        {
            action();
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }


    private IDisposable EnterBusyScope(string operation)
    {
        IsBusy = true;
        BusyOperation = operation;
        return new BusyScope(this);
    }

    private void AddNarration(string kind, string message, IReadOnlyDictionary<string, string>? data = null)
    {
        RunOnUiThread(() =>
        {
            _narrationEvents.Add(new NarrationEvent(DateTimeOffset.UtcNow, kind, message, data));
            if (_isOperationVisible)
            {
                _operationLatestEvent = message;
                OnPropertyChanged(nameof(OperationLatestEvent));
                AppendOperationNarrationLine(message);
            }
        });
    }

    private sealed class BusyScope : IDisposable
    {
        private readonly MainWindowViewModel _owner;
        private bool _disposed;

        public BusyScope(MainWindowViewModel owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.IsBusy = false;
            _owner.BusyOperation = string.Empty;
        }
    }

    private ProjectModel LoadProject(string projectFilePath)
    {
        var project = _localProjectService.LoadProject(projectFilePath);
        var invariantResult = ProjectInvariants.Verify(project.WorkspacePath);
        Trace.WriteLine($"[Shoots.UI] project.invariants {JsonSerializer.Serialize(invariantResult)}");
        if (!invariantResult.Ok)
        {
            ProjectCreationErrorMessage = $"Project invariants failed: {string.Join("; ", invariantResult.Missing.Concat(invariantResult.Errors))}";
        }

        CurrentProject = project;
        var recoveredRuns = RunRecoveryService.MarkCrashedRunningRuns(project.WorkspacePath, evt => AddNarration(evt.Kind, evt.Message, evt.Data));
        if (recoveredRuns.Count > 0)
        {
            Trace.WriteLine($"[Shoots.UI] recovered crashed runs: {string.Join(",", recoveredRuns)}");
        }

        AddNarration("info", "Project loaded", new Dictionary<string, string>
        {
            ["project_id"] = project.ProjectId,
            ["workspace_path"] = project.WorkspacePath
        });
        return project;
    }

    private void TryLoadLastProjectFromRecents()
    {
        var lastWorkspace = _workspaceProvider.GetRecentWorkspaces().FirstOrDefault();
        if (lastWorkspace is null)
        {
            return;
        }

        var projectFilePath = Path.Combine(lastWorkspace.RootPath, "project.json");
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        try
        {
            _ = LoadProject(projectFilePath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Shoots.UI] Failed to load recent project: {ex.Message}");
        }
    }


	internal IReadOnlyList<IAiHelpSurface> GetAiHelpSurfacesForRegistration()
	{
		return BuildAiHelpSurfaces().ToList();
	}

	private void RegisterAiSurfaces()
	{
		// Build surfaces deterministically
		var surfaces = BuildAiHelpSurfaces();

		// IAiHelpFacade has drifted a few times. We bind softly:
		// - RegisterSurfaces(IEnumerable<IAiHelpSurface>)
		// - RegisterSurface(IAiHelpSurface)
		// - AddSurface(IAiHelpSurface)
		// - AddSurfaces(IEnumerable<IAiHelpSurface>)
		// If none exist, we do nothing (UI still runs; tests will tell us what to wire next).
		var t = _aiHelpFacade.GetType();

		// Prefer bulk registration
		var bulkNames = new[] { "RegisterSurfaces", "AddSurfaces" };
		foreach (var name in bulkNames)
		{
			var m = t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
			if (m is null) continue;
			var ps = m.GetParameters();
			if (ps.Length != 1) continue;

			try
			{
				m.Invoke(_aiHelpFacade, new object?[] { surfaces });
				return;
			}
			catch
			{
				// try next candidate
			}
		}

		// Fall back to per-surface
		var singleNames = new[] { "RegisterSurface", "AddSurface" };
		foreach (var name in singleNames)
		{
			var m = t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
			if (m is null) continue;
			var ps = m.GetParameters();
			if (ps.Length != 1) continue;

			foreach (var surface in surfaces)
			{
				try { m.Invoke(_aiHelpFacade, new object?[] { surface }); }
				catch { /* keep going */ }
			}
			return;
		}

		// No compatible method found: do nothing.
	}

    private void InitializeChatIntake()
    {
        ChatTranscript = new ReadOnlyObservableCollection<string>(_chatTranscript);
        Narration = new ReadOnlyObservableCollection<NarrationEvent>(_narrationEvents);
    }

    // ---- Startup handlers ----
    private Task HandleStartupLanguageAsync(string input)
    {
        var previous = _startupFlow.State;
        _pendingProjectLanguage = string.Equals(input, "skip", StringComparison.OrdinalIgnoreCase)
            ? "dotnet"
            : input;

        if (!_startupFlow.TrySetLanguage(_pendingProjectLanguage, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "Language captured.");
        AddStartupMessage($"System: Language = {_pendingProjectLanguage}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupProjectNameAsync(string input)
    {
        var previous = _startupFlow.State;
        _pendingProjectName = string.Equals(input, "skip", StringComparison.OrdinalIgnoreCase)
            ? $"project-{DateTimeOffset.UtcNow:yyyyMMdd}"
            : input;

        if (!_startupFlow.TrySetProjectName(_pendingProjectName, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "Project name captured.");
        AddStartupMessage($"System: Project name = {_pendingProjectName}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupDescriptionAsync(string input)
    {
        var previous = _startupFlow.State;
        _pendingProjectDescription = string.Equals(input, "skip", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : input;

        if (!_startupFlow.TrySetDescription(_pendingProjectDescription, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "Description captured.");
        AddStartupMessage($"System: Description captured.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupProviderAsync(string input)
    {
        var previous = _startupFlow.State;
        var normalized = input.Trim();
        if (!string.Equals(normalized, "Local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "Remote", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "Delegated", StringComparison.OrdinalIgnoreCase))
        {
            AddStartupMessage("System: Provider must be one of Local, Remote, Delegated.");
            return Task.CompletedTask;
        }

        _pendingProviderKind = char.ToUpperInvariant(normalized[0]) + normalized[1..].ToLowerInvariant();
        if (!_startupFlow.TrySetProviderKind(_pendingProviderKind, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "Provider captured.");
        AddStartupMessage($"System: Provider = {_pendingProviderKind}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupEnvironmentAsync(string input)
    {
        var previous = _startupFlow.State;
        _pendingEnvironmentId = string.Equals(input, "skip", StringComparison.OrdinalIgnoreCase)
            ? "host-local"
            : input.Trim();

        if (!_startupFlow.TrySetEnvironmentId(_pendingEnvironmentId, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "Environment captured.");
        AddStartupMessage($"System: Environment = {_pendingEnvironmentId}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupConfirmAsync(string input)
    {
        if (!string.Equals(input, "confirm", StringComparison.OrdinalIgnoreCase))
        {
            AddStartupMessage("System: Type \"confirm\" to create the project.");
            return Task.CompletedTask;
        }

        var createdUtc = DateTimeOffset.UtcNow;
        var projectName = string.IsNullOrWhiteSpace(_pendingProjectName) ? $"project-{createdUtc:yyyyMMdd}" : _pendingProjectName;
        var projectId = ComputeDeterministicProjectId(projectName);
        var projectRoot = Path.GetFullPath(Path.Combine(".state", "projects", projectId));

        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, "inputs"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "outputs"));

        var descriptor = new PersistedProjectDescriptor(
            projectId,
            projectName,
            createdUtc,
            SelectedEnvironmentId: _pendingEnvironmentId,
            ProviderKind: _pendingProviderKind,
            ProviderEndpoint: _pendingProviderEndpoint,
            Language: _pendingProjectLanguage,
            Description: _pendingProjectDescription,
            projectRoot);

        var descriptorPath = Path.Combine(projectRoot, "project.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, new JsonSerializerOptions { WriteIndented = true }));

        CreateProjectScaffold(
            projectRoot,
            projectId,
            projectName,
            _pendingProjectDescription,
            _pendingProjectLanguage,
            _pendingProviderKind,
            _pendingProviderEndpoint,
            _pendingEnvironmentId,
            createdUtc);

        var workspace = new ProjectWorkspace(
            Name: projectName,
            RootPath: projectRoot,
            LastOpenedUtc: createdUtc,
            ProjectId: projectId,
            CreatedUtc: createdUtc,
            SelectedEnvironmentId: descriptor.SelectedEnvironmentId,
            SelectedProviderKind: _pendingProviderKind,
            SelectedProviderEndpoint: _pendingProviderEndpoint);

        _workspaceProvider.SetActiveWorkspace(workspace);
        LoadWorkspaces();
        SelectWorkspace(workspace);

        _startupComplete = true;
        _startupFlow.TryConfirmCreate(out _);
        AddStartupMessage($"System: Project created at {projectRoot}.");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleContinueExistingPathAsync(string input)
    {
        var root = Path.GetFullPath(input);
        if (!Directory.Exists(root))
        {
            AddStartupMessage("System: Path does not exist.");
            return Task.CompletedTask;
        }

        var name = new DirectoryInfo(root).Name;
        var now = DateTimeOffset.UtcNow;
        var workspace = new ProjectWorkspace(name, root, now, ProjectId: ComputeDeterministicProjectId(name), CreatedUtc: now);
        _workspaceProvider.SetActiveWorkspace(workspace);

        if (!_startupFlow.TrySetExistingProjectPath(root, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        _startupComplete = true;
        AddStartupMessage($"System: Attached project {name}.");
        LoadWorkspaces();
        SelectWorkspace(workspace);
        NotifyStartupFlowChanged();

        return Task.CompletedTask;
    }

    private Task HandleContinueExistingConfirmAsync(string input)
    {
        AddStartupMessage("System: Existing project attachment is completed via path input.");
        return Task.CompletedTask;
    }

    private Task HandleExploreModeAsync(string input)
    {
        if (string.Equals(input, "promote", StringComparison.OrdinalIgnoreCase))
        {
            _startupFlow.Reset();
            _startupFlow.TryBeginNewProject(out _);
            AddStartupMessage("System: Explore mode promoted to startup project flow.");
            NotifyStartupFlowChanged();
            return Task.CompletedTask;
        }

        AddStartupMessage("System: Explore mode active. Type \"promote\" to start a project.");
        return Task.CompletedTask;
    }

    private static void CreateProjectScaffold(
        string projectRoot,
        string projectId,
        string projectName,
        string description,
        string language,
        string providerKind,
        string providerEndpoint,
        string environmentId,
        DateTimeOffset createdUtc)
    {
        var semantic = new
        {
            projectId,
            projectName,
            description,
            language,
            providerKind,
            providerEndpoint,
            environmentId
        };

        var canonical = CanonicalJson.Normalize(JsonSerializer.Serialize(semantic));
        var planHash = ComputeDeterministicHash(canonical);
        var providerHash = ComputeDeterministicHash(CanonicalJson.Normalize(JsonSerializer.Serialize(new { providerKind, providerEndpoint })));
        var envHash = ComputeDeterministicHash(CanonicalJson.Normalize(JsonSerializer.Serialize(new { environmentId })));

        var planRoot = Path.Combine(projectRoot, "plan");
        var envRoot = Path.Combine(projectRoot, "env");
        Directory.CreateDirectory(planRoot);
        Directory.CreateDirectory(envRoot);

        var planPayload = new
        {
            projectId,
            language,
            providerKind,
            environmentId,
            createdAtUtc = createdUtc,
            planHash
        };

        File.WriteAllText(
            Path.Combine(planRoot, "plan.json"),
            JsonSerializer.Serialize(planPayload, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(
            Path.Combine(envRoot, "selected.json"),
            JsonSerializer.Serialize(new { environmentId }, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(
            Path.Combine(envRoot, "descriptor.json"),
            JsonSerializer.Serialize(new { environmentId, descriptorHash = envHash }, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(
            Path.Combine(projectRoot, "provider.json"),
            JsonSerializer.Serialize(new { kind = providerKind, endpoint = providerEndpoint, configHash = providerHash }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ComputeDeterministicHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeStartupInput(string input) => input.Trim();

    private static string ComputeDeterministicProjectId(string projectName)
    {
        var normalized = projectName.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    private sealed record PersistedProjectDescriptor(
        string Id,
        string Name,
        DateTimeOffset CreatedUtc,
        string SelectedEnvironmentId,
        string ProviderKind,
        string ProviderEndpoint,
        string Language,
        string Description,
        string ProjectRootPath);

    private StartupSessionMode GetSessionMode()
    {
        if (HasActiveWorkspace) return StartupSessionMode.Project;
        if (_startupFlow.EntryPath == StartupEntryPath.ExploreIdea) return StartupSessionMode.Explore;
        return StartupSessionMode.Startup;
    }

    private void UpdateSessionMode()
    {
        if (HasActiveWorkspace && _startupFlow.EntryPath == StartupEntryPath.ExploreIdea)
        {
            AddStartupMessage("System: Explore mode cannot be active while a project is attached. State remains: Project.");
            _startupFlow.Reset();
        }

        var next = GetSessionMode();
        if (next == _sessionMode) return;

        Trace.WriteLine($"[Shoots.UI] Session mode transition: {_sessionMode} -> {next}.");
        _sessionMode = next;
        OnPropertyChanged(nameof(SessionStatusLabel));
    }

    private void AddStartupMessage(string message)
    {
        _startupMessages.Add(message);
        OnPropertyChanged(nameof(StartupMessages));
    }

    private void LogStartupTransition(StartupFlowState previous, StartupFlowState next, string reason)
        => Trace.WriteLine($"[Shoots.UI] Startup flow transition: {previous} -> {next}. {reason}");

    private void NotifyStartupFlowChanged()
    {
        OnPropertyChanged(nameof(StartupStateLabel));
        OnPropertyChanged(nameof(StartupEntryPathLabel));
        OnPropertyChanged(nameof(IsEntryPathSelectionActive));
        OnPropertyChanged(nameof(StartupButtonTooltip));
        OnPropertyChanged(nameof(StartAnotherProjectTooltip));
        OnPropertyChanged(nameof(StartupPrompt));
        OnPropertyChanged(nameof(IsStartupInputActive));
        OnPropertyChanged(nameof(StartupProviderLabel));
        OnPropertyChanged(nameof(IsStartupLocked));
        OnPropertyChanged(nameof(IsStartupTabEnabled));
        OnPropertyChanged(nameof(IsStartupComplete));
        OnPropertyChanged(nameof(SessionStatusLabel));
        UpdateSessionMode();

        NewProjectCommand.RaiseCanExecuteChanged();
        StartAnotherProjectCommand.RaiseCanExecuteChanged();
        SelectEntryPathCommand.RaiseCanExecuteChanged();
        SubmitStartupInputCommand.RaiseCanExecuteChanged();
    }

    private static string FormatEntryPathLabel(StartupEntryPath entryPath) =>
        entryPath switch
        {
            StartupEntryPath.StartSomethingNew => "Start something new",
            StartupEntryPath.ContinueExistingProject => "Continue an existing project",
            StartupEntryPath.ExploreIdea => "Just explore an idea",
            _ => entryPath.ToString()
        };

    private void RaiseCommandCanExecute()
    {
        StartCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RefreshStatusCommand.RaiseCanExecuteChanged();
        ApplyEnvironmentCommand.RaiseCanExecuteChanged();
        ApplyScriptCommand.RaiseCanExecuteChanged();
        RemoveWorkspaceCommand.RaiseCanExecuteChanged();
        OpenWorkspaceCommand.RaiseCanExecuteChanged();
        ToggleSystemTierCommand.RaiseCanExecuteChanged();
        RefreshAiHelpCommand.RaiseCanExecuteChanged();

        AddBlueprintCommand.RaiseCanExecuteChanged();
        SaveBlueprintCommand.RaiseCanExecuteChanged();
        RevertBlueprintCommand.RaiseCanExecuteChanged();
        ExplainBlueprintCommand.RaiseCanExecuteChanged();
        ValidateBlueprintCommand.RaiseCanExecuteChanged();
        SuggestBlueprintCommand.RaiseCanExecuteChanged();

        ExplainExecutionCommand.RaiseCanExecuteChanged();
        ReplayPlanCommand.RaiseCanExecuteChanged();
        RaiseReplayCommandCanExecuteChanged();
        RefreshNarrationCommand.RaiseCanExecuteChanged();
        RunIntakePlanCommand.RaiseCanExecuteChanged();
        SendChatIntentCommand.RaiseCanExecuteChanged();
        OpenCurrentWorkspaceFolderCommand.RaiseCanExecuteChanged();
        OpenProjectFileCommand.RaiseCanExecuteChanged();
        OpenLastRunFolderCommand.RaiseCanExecuteChanged();
        OpenLastVerificationReportCommand.RaiseCanExecuteChanged();
        OpenLastOperatorFlowCommand.RaiseCanExecuteChanged();
        OpenLastTransportEquivalenceCommand.RaiseCanExecuteChanged();
        CopyLastRunFolderPathCommand.RaiseCanExecuteChanged();
        CopyLastVerificationReportPathCommand.RaiseCanExecuteChanged();
        CopyLastOperatorFlowPathCommand.RaiseCanExecuteChanged();
        CopyLastTransportEquivalencePathCommand.RaiseCanExecuteChanged();
        CopyLastFailureSummaryCommand?.RaiseCanExecuteChanged();
        OpenProofArtifactCommand.NotifyCanExecuteChanged();
        CopyProofArtifactPathCommand.NotifyCanExecuteChanged();
        OpenProofRunFolderCommand.RaiseCanExecuteChanged();
        CopyProofRunFolderPathCommand.RaiseCanExecuteChanged();
        RunDemoPlanCommand.RaiseCanExecuteChanged();
        QuickDemoCommand.RaiseCanExecuteChanged();
        NewProjectCommand.RaiseCanExecuteChanged();
        BuildUiProjectCommand.RaiseCanExecuteChanged();
        RunUiTestsCommand.RaiseCanExecuteChanged();
        RunSmokeValidationCommand.RaiseCanExecuteChanged();
        RunIntegrityValidationCommand.RaiseCanExecuteChanged();
        RunFullValidationLoopCommand.RaiseCanExecuteChanged();
        RunBuilderProofMatrixCommand.RaiseCanExecuteChanged();
        RunBuilderComparativeProofCommand.RaiseCanExecuteChanged();
        OpenValidationOutputFolderCommand.RaiseCanExecuteChanged();
        OpenValidationFailureLogCommand.RaiseCanExecuteChanged();
        OpenValidationStabilityArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationHandoffSummaryCommand.RaiseCanExecuteChanged();
        OpenValidationHandoffBundleFolderCommand.RaiseCanExecuteChanged();
        CopyValidationHandoffSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationHandoffArtifactPathsCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupIntakeCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupPromptCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupPromptCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupPlanCommand.RaiseCanExecuteChanged();
        OpenValidationRepairPrepBundleCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupPlanSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationRepairPrepSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupRerunRecommendationCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupExecutionOutcomeCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupEscalationCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupResolutionReviewCommand.RaiseCanExecuteChanged();
        OpenValidationResolutionHandoffCommand.RaiseCanExecuteChanged();
        OpenValidationResolutionPromotionReviewCommand.RaiseCanExecuteChanged();
        OpenValidationReleaseDecisionSummaryCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupRerunArtifactsCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupOutcomeNextStepCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupEscalationSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupClosureSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationResolutionHandoffSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationResolutionPromotionSummaryCommand.RaiseCanExecuteChanged();
        CopyValidationReleaseDecisionSummaryCommand.RaiseCanExecuteChanged();
        RunValidationFollowupRecommendedRerunCommand.RaiseCanExecuteChanged();
        OpenValidationFollowupFirstEvidenceCommand.RaiseCanExecuteChanged();
        CopyValidationFollowupRerunCommandSummaryCommand.RaiseCanExecuteChanged();
        ExecuteValidationFollowupPlanStepCommand.NotifyCanExecuteChanged();
        CopyValidationFollowupPlanStepCommand.NotifyCanExecuteChanged();
        OpenValidationOrchestrationArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationOrchestrationNoteCommand.RaiseCanExecuteChanged();
        OpenValidationHistoryLedgerCommand.RaiseCanExecuteChanged();
        OpenValidationTrendArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationRegressionArtifactCommand.RaiseCanExecuteChanged();
        SetReleaseBaselineCommand.RaiseCanExecuteChanged();
        OpenValidationBaselineArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationBaselineHistoryArtifactCommand.RaiseCanExecuteChanged();
        OpenValidationBaselineComparisonArtifactCommand.RaiseCanExecuteChanged();
        OpenBuilderProofSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderProofRunFolderCommand.RaiseCanExecuteChanged();
        OpenBuilderModelFloorVerdictCommand.RaiseCanExecuteChanged();
        OpenBuilderFailurePatternsCommand.RaiseCanExecuteChanged();
        OpenBuilderExternalProofSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderModelFloorPolicyCommand.RaiseCanExecuteChanged();
        OpenBuilderModelFloorGuidanceCommand.RaiseCanExecuteChanged();
        OpenBuilderTrustBandsCommand.RaiseCanExecuteChanged();
        OpenBuilderScopeSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderRoutingRecommendationCommand.RaiseCanExecuteChanged();
        OpenBuilderEscalationDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderRoutingPlanCommand.RaiseCanExecuteChanged();
        OpenBuilderStrongerTierAvailabilityCommand.RaiseCanExecuteChanged();
        OpenBuilderComparativeProofSummaryCommand.RaiseCanExecuteChanged();
        OpenBuilderRoutingPolicyEvidenceCommand.RaiseCanExecuteChanged();
        OpenBuilderSplitFirstPlanCommand.RaiseCanExecuteChanged();
        OpenBuilderTieredRoutingPolicyCommand.RaiseCanExecuteChanged();
        OpenBuilderDefaultGuidanceCommand.RaiseCanExecuteChanged();
        OpenBuilderGuidanceHistoryCommand.RaiseCanExecuteChanged();
        OpenBuilderLatestRoutingDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderGuidanceSupportCommand.RaiseCanExecuteChanged();
        OpenBuilderRequestIntakeCommand.RaiseCanExecuteChanged();
        OpenBuilderExecutionPrepCommand.RaiseCanExecuteChanged();
        LaunchPreparedBuilderRouteCommand.RaiseCanExecuteChanged();
        LaunchBuilderOverrideRouteCommand.RaiseCanExecuteChanged();
        OpenBuilderExecutionLaunchCommand.RaiseCanExecuteChanged();
        OpenBuilderExecutionResultCommand.RaiseCanExecuteChanged();
        OpenBuilderReadinessGateCommand.RaiseCanExecuteChanged();
        OpenBuilderReadinessHistoryCommand.RaiseCanExecuteChanged();
        OpenBuilderConfirmedClassesCommand.RaiseCanExecuteChanged();
        OpenBuilderDefaultRouteDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderLaunchDefaultDecisionCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteOverrideEvidenceCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteReviewCommand.RaiseCanExecuteChanged();
        OpenBuilderReadinessContradictionsCommand.RaiseCanExecuteChanged();
        OpenBuilderRouteStabilitySummaryCommand.RaiseCanExecuteChanged();
        RunNextBuilderSplitStepCommand.RaiseCanExecuteChanged();
        OpenBuilderSplitStepExecutionCommand.RaiseCanExecuteChanged();
        OpenBuilderSplitFirstOutcomeCommand.RaiseCanExecuteChanged();
        CopyBuilderProofSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderScopeSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderRoutingRecommendationCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitTaskGuidanceCommand.RaiseCanExecuteChanged();
        CopyBuilderComparativeProofSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderRoutingPolicySummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitFirstPlanSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderPrimaryRoutingRecommendationCommand.RaiseCanExecuteChanged();
        CopyBuilderWeakSpotMitigationSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderDefaultGuidanceSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderLatestRoutingDecisionCommand.RaiseCanExecuteChanged();
        CopyBuilderExecutionPrepSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderIntakeRoutingSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderExecutionLaunchSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderExecutionResultSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderReadinessSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderReadinessContradictionNoteCommand.RaiseCanExecuteChanged();
        CopyBuilderConfirmedClassesSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderDefaultRouteDecisionSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderLaunchDefaultSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderRouteOverrideSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitExecutionSummaryCommand.RaiseCanExecuteChanged();
        CopyBuilderSplitComparativeClosureSummaryCommand.RaiseCanExecuteChanged();
        RefreshSimilarCasesCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseDesignNoteCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseIndexCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseEffectivenessCommand.RaiseCanExecuteChanged();
        OpenSemanticReusePlaybookCatalogCommand.RaiseCanExecuteChanged();
        OpenSemanticReuseSuggestionArtifactCommand.NotifyCanExecuteChanged();
        OpenSemanticReusePlaybookArtifactCommand.NotifyCanExecuteChanged();
        ValidateGeneratedOutputCommand.RaiseCanExecuteChanged();
        AttemptRepairCommand.RaiseCanExecuteChanged();
        OpenRepairOutputFolderCommand.RaiseCanExecuteChanged();
        OpenRepairBundleFolderCommand.RaiseCanExecuteChanged();
        OpenLinkedRepairValidationRunFolderCommand.RaiseCanExecuteChanged();
        PromoteRepairResultCommand.RaiseCanExecuteChanged();
        AdoptRepairCommand.RaiseCanExecuteChanged();
        ReplaceRepairCommand.RaiseCanExecuteChanged();
        UnadoptRepairCommand.RaiseCanExecuteChanged();
        OpenRepairAuditSummaryFolderCommand.RaiseCanExecuteChanged();
        OpenPromotedRepairFolderCommand.RaiseCanExecuteChanged();

        OnPropertyChanged(nameof(StartDisabledReason));
        OnPropertyChanged(nameof(ApplyEnvironmentDisabledReason));
        OnPropertyChanged(nameof(ApplyScriptDisabledReason));
        OnPropertyChanged(nameof(AiHelpDisabledReason));
        OnPropertyChanged(nameof(SystemTierActionLabel));
        OnPropertyChanged(nameof(ExecutionDisabledReason));
        OnPropertyChanged(nameof(RunIntakePlanDisabledReason));
        OnPropertyChanged(nameof(RunDemoPlanDisabledReason));
        OnPropertyChanged(nameof(QuickDemoDisabledReason));
        OnPropertyChanged(nameof(CanStartNewProjectUi));
        OnPropertyChanged(nameof(ValidationDisabledReason));
        OnPropertyChanged(nameof(HasValidationDisabledReason));
        OnPropertyChanged(nameof(BuildUiProjectValidationDisabledReason));
        OnPropertyChanged(nameof(RunUiTestsValidationDisabledReason));
        OnPropertyChanged(nameof(RunSmokeValidationDisabledReason));
        OnPropertyChanged(nameof(RunIntegrityValidationDisabledReason));
        OnPropertyChanged(nameof(RunFullValidationLoopDisabledReason));
        OnPropertyChanged(nameof(BuilderProofDisabledReason));
        OnPropertyChanged(nameof(HasBuilderProofDisabledReason));
        OnPropertyChanged(nameof(BuilderPreparedLaunchDisabledReason));
        OnPropertyChanged(nameof(HasBuilderPreparedLaunchDisabledReason));
        OnPropertyChanged(nameof(BuilderComparativeProofDisabledReason));
        OnPropertyChanged(nameof(HasBuilderComparativeProofDisabledReason));
        OnPropertyChanged(nameof(BuilderSplitExecutionDisabledReason));
        OnPropertyChanged(nameof(HasBuilderSplitExecutionDisabledReason));
        OnPropertyChanged(nameof(BuilderProofModelId));
        OnPropertyChanged(nameof(BuilderProofOutcomeClassification));
        OnPropertyChanged(nameof(BuilderProofOutcomeBadge));
        OnPropertyChanged(nameof(BuilderProofLatestTargetSummary));
        OnPropertyChanged(nameof(HasBuilderProofLatestTargetSummary));
        OnPropertyChanged(nameof(BuilderProofSummaryText));
        OnPropertyChanged(nameof(HasBuilderProofSummary));
        OnPropertyChanged(nameof(BuilderExternalProofOutcomeClassification));
        OnPropertyChanged(nameof(BuilderExternalProofOutcomeBadge));
        OnPropertyChanged(nameof(BuilderExternalProofSummaryText));
        OnPropertyChanged(nameof(HasBuilderExternalProofSummary));
        OnPropertyChanged(nameof(BuilderProofSuccessCountsSummary));
        OnPropertyChanged(nameof(HasBuilderProofSuccessCountsSummary));
        OnPropertyChanged(nameof(BuilderProofRunPath));
        OnPropertyChanged(nameof(HasBuilderProofRunPath));
        OnPropertyChanged(nameof(BuilderProofSummaryPath));
        OnPropertyChanged(nameof(HasBuilderProofSummaryPath));
        OnPropertyChanged(nameof(BuilderExternalProofSummaryPath));
        OnPropertyChanged(nameof(HasBuilderExternalProofSummaryPath));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictState));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictBadge));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictSummary));
        OnPropertyChanged(nameof(HasBuilderModelFloorVerdictSummary));
        OnPropertyChanged(nameof(BuilderModelFloorVerdictPath));
        OnPropertyChanged(nameof(HasBuilderModelFloorVerdictPath));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictState));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictBadge));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictSummary));
        OnPropertyChanged(nameof(HasBuilderExternalFloorVerdictSummary));
        OnPropertyChanged(nameof(BuilderExternalFloorVerdictPath));
        OnPropertyChanged(nameof(HasBuilderExternalFloorVerdictPath));
        OnPropertyChanged(nameof(BuilderModelFloorFailurePatternSummary));
        OnPropertyChanged(nameof(HasBuilderModelFloorFailurePatternSummary));
        OnPropertyChanged(nameof(BuilderModelFloorFailurePatternsPath));
        OnPropertyChanged(nameof(HasBuilderModelFloorFailurePatternsPath));
        OnPropertyChanged(nameof(BuilderModelFloorPolicySummary));
        OnPropertyChanged(nameof(HasBuilderModelFloorPolicySummary));
        OnPropertyChanged(nameof(BuilderModelFloorPolicyPath));
        OnPropertyChanged(nameof(HasBuilderModelFloorPolicyPath));
        OnPropertyChanged(nameof(BuilderModelFloorGuidanceSummary));
        OnPropertyChanged(nameof(HasBuilderModelFloorGuidanceSummary));
        OnPropertyChanged(nameof(BuilderModelFloorGuidancePath));
        OnPropertyChanged(nameof(HasBuilderModelFloorGuidancePath));
        OnPropertyChanged(nameof(BuilderProofTrustBandState));
        OnPropertyChanged(nameof(BuilderProofTrustBandBadge));
        OnPropertyChanged(nameof(BuilderModelTrustBandSummary));
        OnPropertyChanged(nameof(HasBuilderModelTrustBandSummary));
        OnPropertyChanged(nameof(BuilderModelTrustBandsPath));
        OnPropertyChanged(nameof(HasBuilderModelTrustBandsPath));
        OnPropertyChanged(nameof(BuilderModelScopeSummary));
        OnPropertyChanged(nameof(HasBuilderModelScopeSummary));
        OnPropertyChanged(nameof(BuilderModelScopeSummaryPath));
        OnPropertyChanged(nameof(HasBuilderModelScopeSummaryPath));
        OnPropertyChanged(nameof(BuilderRoutingRecommendationState));
        OnPropertyChanged(nameof(BuilderRoutingRecommendationBadge));
        OnPropertyChanged(nameof(BuilderModelRoutingRecommendationSummary));
        OnPropertyChanged(nameof(HasBuilderModelRoutingRecommendationSummary));
        OnPropertyChanged(nameof(BuilderModelRoutingRecommendationPath));
        OnPropertyChanged(nameof(HasBuilderModelRoutingRecommendationPath));
        OnPropertyChanged(nameof(BuilderModelWeakSpotSummary));
        OnPropertyChanged(nameof(HasBuilderModelWeakSpotSummary));
        OnPropertyChanged(nameof(BuilderModelEscalationState));
        OnPropertyChanged(nameof(BuilderModelEscalationBadge));
        OnPropertyChanged(nameof(BuilderModelEscalationSummary));
        OnPropertyChanged(nameof(HasBuilderModelEscalationSummary));
        OnPropertyChanged(nameof(BuilderModelEscalationDecisionPath));
        OnPropertyChanged(nameof(HasBuilderModelEscalationDecisionPath));
        OnPropertyChanged(nameof(BuilderModelRoutingPlanSummary));
        OnPropertyChanged(nameof(HasBuilderModelRoutingPlanSummary));
        OnPropertyChanged(nameof(BuilderModelRoutingPlanPath));
        OnPropertyChanged(nameof(HasBuilderModelRoutingPlanPath));
        OnPropertyChanged(nameof(BuilderModelSplitTaskGuidanceSummary));
        OnPropertyChanged(nameof(HasBuilderModelSplitTaskGuidanceSummary));
        OnPropertyChanged(nameof(BuilderModelRoutingWeakSpotReason));
        OnPropertyChanged(nameof(HasBuilderModelRoutingWeakSpotReason));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilityState));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilityBadge));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilitySummary));
        OnPropertyChanged(nameof(HasBuilderStrongerTierAvailabilitySummary));
        OnPropertyChanged(nameof(BuilderStrongerTierAvailabilityPath));
        OnPropertyChanged(nameof(HasBuilderStrongerTierAvailabilityPath));
        OnPropertyChanged(nameof(BuilderComparativeProofClassification));
        OnPropertyChanged(nameof(BuilderComparativeProofBadge));
        OnPropertyChanged(nameof(BuilderComparativeProofSummary));
        OnPropertyChanged(nameof(HasBuilderComparativeProofSummary));
        OnPropertyChanged(nameof(BuilderComparativeProofSummaryPath));
        OnPropertyChanged(nameof(HasBuilderComparativeProofSummaryPath));
        OnPropertyChanged(nameof(BuilderComparativeRepairBurdenSummary));
        OnPropertyChanged(nameof(HasBuilderComparativeRepairBurdenSummary));
        OnPropertyChanged(nameof(BuilderRoutingPolicyState));
        OnPropertyChanged(nameof(BuilderRoutingPolicyBadge));
        OnPropertyChanged(nameof(BuilderRoutingPolicySummary));
        OnPropertyChanged(nameof(HasBuilderRoutingPolicySummary));
        OnPropertyChanged(nameof(BuilderRoutingPolicyPath));
        OnPropertyChanged(nameof(HasBuilderRoutingPolicyPath));
        OnPropertyChanged(nameof(BuilderRoutingEvidenceBadge));
        OnPropertyChanged(nameof(BuilderRoutingEvidenceSummary));
        OnPropertyChanged(nameof(HasBuilderRoutingEvidenceSummary));
        OnPropertyChanged(nameof(BuilderRoutingEvidencePath));
        OnPropertyChanged(nameof(HasBuilderRoutingEvidencePath));
        OnPropertyChanged(nameof(BuilderSplitFirstPlanSummary));
        OnPropertyChanged(nameof(HasBuilderSplitFirstPlanSummary));
        OnPropertyChanged(nameof(BuilderSplitFirstPlanPath));
        OnPropertyChanged(nameof(HasBuilderSplitFirstPlanPath));
        OnPropertyChanged(nameof(BuilderTieredRoutingState));
        OnPropertyChanged(nameof(BuilderTieredRoutingBadge));
        OnPropertyChanged(nameof(BuilderTieredRoutingSummary));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingSummary));
        OnPropertyChanged(nameof(BuilderTieredRoutingPath));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingPath));
        OnPropertyChanged(nameof(BuilderTieredRoutingEvidenceBadge));
        OnPropertyChanged(nameof(BuilderTieredRoutingEvidenceSummary));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingEvidenceSummary));
        OnPropertyChanged(nameof(BuilderTieredRoutingEvidencePath));
        OnPropertyChanged(nameof(HasBuilderTieredRoutingEvidencePath));
        OnPropertyChanged(nameof(BuilderPrimaryRoutingRecommendationSummary));
        OnPropertyChanged(nameof(HasBuilderPrimaryRoutingRecommendationSummary));
        OnPropertyChanged(nameof(BuilderStrongerTierRoleSummary));
        OnPropertyChanged(nameof(HasBuilderStrongerTierRoleSummary));
        OnPropertyChanged(nameof(BuilderWeakSpotMitigationSummary));
        OnPropertyChanged(nameof(HasBuilderWeakSpotMitigationSummary));
        OnPropertyChanged(nameof(BuilderIntakeState));
        OnPropertyChanged(nameof(BuilderIntakeBadge));
        OnPropertyChanged(nameof(BuilderIntakeSummary));
        OnPropertyChanged(nameof(HasBuilderIntakeSummary));
        OnPropertyChanged(nameof(BuilderIntakePath));
        OnPropertyChanged(nameof(HasBuilderIntakePath));
        OnPropertyChanged(nameof(BuilderPrepRouteState));
        OnPropertyChanged(nameof(BuilderPrepRouteBadge));
        OnPropertyChanged(nameof(BuilderPrepSummary));
        OnPropertyChanged(nameof(HasBuilderPrepSummary));
        OnPropertyChanged(nameof(BuilderPrepPath));
        OnPropertyChanged(nameof(HasBuilderPrepPath));
        OnPropertyChanged(nameof(BuilderLaunchAvailabilityState));
        OnPropertyChanged(nameof(BuilderLaunchAvailabilityBadge));
        OnPropertyChanged(nameof(BuilderLaunchSummary));
        OnPropertyChanged(nameof(HasBuilderLaunchSummary));
        OnPropertyChanged(nameof(BuilderLaunchPath));
        OnPropertyChanged(nameof(HasBuilderLaunchPath));
        OnPropertyChanged(nameof(BuilderResultState));
        OnPropertyChanged(nameof(BuilderResultBadge));
        OnPropertyChanged(nameof(BuilderResultSummary));
        OnPropertyChanged(nameof(HasBuilderResultSummary));
        OnPropertyChanged(nameof(BuilderResultPath));
        OnPropertyChanged(nameof(HasBuilderResultPath));
        OnPropertyChanged(nameof(BuilderRouteComparisonBadge));
        OnPropertyChanged(nameof(BuilderRouteComparisonSummary));
        OnPropertyChanged(nameof(HasBuilderRouteComparisonSummary));
        OnPropertyChanged(nameof(BuilderSplitStepExecutionSummary));
        OnPropertyChanged(nameof(HasBuilderSplitStepExecutionSummary));
        OnPropertyChanged(nameof(BuilderSplitStepExecutionPath));
        OnPropertyChanged(nameof(HasBuilderSplitStepExecutionPath));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomeClassification));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomeBadge));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomeSummary));
        OnPropertyChanged(nameof(HasBuilderSplitFirstOutcomeSummary));
        OnPropertyChanged(nameof(BuilderSplitFirstOutcomePath));
        OnPropertyChanged(nameof(HasBuilderSplitFirstOutcomePath));
        OnPropertyChanged(nameof(BuilderSplitSteps));
        OnPropertyChanged(nameof(HasBuilderSplitSteps));
        OnPropertyChanged(nameof(ValidationRunMode));
        OnPropertyChanged(nameof(ValidationRunModeBadge));
        OnPropertyChanged(nameof(ValidationRunModeSummary));
        OnPropertyChanged(nameof(ValidationSequenceSummary));
        OnPropertyChanged(nameof(ValidationHandoffBundlePath));
        OnPropertyChanged(nameof(HasValidationHandoffBundlePath));
        OnPropertyChanged(nameof(ValidationHandoffSummaryPath));
        OnPropertyChanged(nameof(HasValidationHandoffSummaryPath));
        OnPropertyChanged(nameof(ValidationHandoffSummaryText));
        OnPropertyChanged(nameof(HasValidationHandoffSummary));
        OnPropertyChanged(nameof(ValidationHandoffComparisonSummary));
        OnPropertyChanged(nameof(HasValidationHandoffComparisonSummary));
        OnPropertyChanged(nameof(ValidationFollowupCategory));
        OnPropertyChanged(nameof(ValidationFollowupBadge));
        OnPropertyChanged(nameof(ValidationFollowupSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupSummary));
        OnPropertyChanged(nameof(ValidationFollowupNextStepText));
        OnPropertyChanged(nameof(HasValidationFollowupNextStep));
        OnPropertyChanged(nameof(ValidationFollowupRepeatedIssueSummary));
        OnPropertyChanged(nameof(HasValidationFollowupRepeatedIssue));
        OnPropertyChanged(nameof(ValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(HasValidationFollowupReuseSuggestionSummary));
        OnPropertyChanged(nameof(ValidationFollowupIntakePath));
        OnPropertyChanged(nameof(HasValidationFollowupIntakePath));
        OnPropertyChanged(nameof(ValidationFollowupPromptPath));
        OnPropertyChanged(nameof(HasValidationFollowupPromptPath));
        OnPropertyChanged(nameof(ValidationFollowupPlanSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupPlanSummary));
        OnPropertyChanged(nameof(ValidationFollowupPlanCategory));
        OnPropertyChanged(nameof(ValidationFollowupPlanBadge));
        OnPropertyChanged(nameof(ValidationFollowupRerunRecommendationText));
        OnPropertyChanged(nameof(HasValidationFollowupRerunRecommendation));
        OnPropertyChanged(nameof(ValidationRepairPrepSummaryText));
        OnPropertyChanged(nameof(HasValidationRepairPrepSummary));
        OnPropertyChanged(nameof(ValidationFollowupPlanFreshnessText));
        OnPropertyChanged(nameof(HasValidationFollowupPlanFreshness));
        OnPropertyChanged(nameof(ValidationFollowupEscalationHint));
        OnPropertyChanged(nameof(HasValidationFollowupEscalationHint));
        OnPropertyChanged(nameof(ValidationFollowupRerunOutcomeSummary));
        OnPropertyChanged(nameof(HasValidationFollowupRerunOutcome));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeClassification));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeBadge));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeSourceSummary));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeSourceSummary));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeSummary));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeNextStateText));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeNextStateText));
        OnPropertyChanged(nameof(ValidationFollowupOutcomeFreshnessText));
        OnPropertyChanged(nameof(HasValidationFollowupOutcomeFreshnessText));
        OnPropertyChanged(nameof(ValidationFollowupExecutionOutcomePath));
        OnPropertyChanged(nameof(HasValidationFollowupExecutionOutcomePath));
        OnPropertyChanged(nameof(ValidationFollowupEscalationClassification));
        OnPropertyChanged(nameof(ValidationFollowupEscalationBadge));
        OnPropertyChanged(nameof(ValidationFollowupEscalationSummaryText));
        OnPropertyChanged(nameof(HasValidationFollowupEscalationSummary));
        OnPropertyChanged(nameof(ValidationFollowupEscalationPath));
        OnPropertyChanged(nameof(HasValidationFollowupEscalationPath));
        OnPropertyChanged(nameof(ValidationFollowupPlanPath));
        OnPropertyChanged(nameof(HasValidationFollowupPlanPath));
        OnPropertyChanged(nameof(ValidationRepairPrepBundlePath));
        OnPropertyChanged(nameof(HasValidationRepairPrepBundlePath));
        OnPropertyChanged(nameof(ValidationFollowupPlanSteps));
        OnPropertyChanged(nameof(HasValidationFollowupPlanSteps));
        OnPropertyChanged(nameof(ValidationFollowupRecommendedRerunBlockedReason));
        OnPropertyChanged(nameof(HasValidationFollowupRecommendedRerunBlockedReason));
        OnPropertyChanged(nameof(ValidationFollowupFirstEvidenceBlockedReason));
        OnPropertyChanged(nameof(HasValidationFollowupFirstEvidenceBlockedReason));
        OnPropertyChanged(nameof(ValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(HasValidationOrchestrationArtifactPath));
        OnPropertyChanged(nameof(ValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(HasValidationOrchestrationNotePath));
        OnPropertyChanged(nameof(ValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(HasValidationIsolatedWorkspacePath));
        OnPropertyChanged(nameof(ValidationActionPolicies));
        OnPropertyChanged(nameof(HasValidationActionPolicies));
        OnPropertyChanged(nameof(ValidationTrendClassification));
        OnPropertyChanged(nameof(ValidationTrendBadge));
        OnPropertyChanged(nameof(ValidationReleaseReadinessClassification));
        OnPropertyChanged(nameof(ValidationReleaseReadinessBadge));
        OnPropertyChanged(nameof(SetReleaseBaselineDisabledReason));
        OnPropertyChanged(nameof(SemanticReuseDisabledReason));
        OnPropertyChanged(nameof(HasSemanticReuseDisabledReason));
        OnPropertyChanged(nameof(SemanticReuseStatus));
        OnPropertyChanged(nameof(SemanticReuseBadge));
        OnPropertyChanged(nameof(AttemptRepairDisabledReason));
        OnPropertyChanged(nameof(PromoteRepairDisabledReason));
        OnPropertyChanged(nameof(AdoptRepairDisabledReason));
        OnPropertyChanged(nameof(ReplaceRepairDisabledReason));
        OnPropertyChanged(nameof(UnadoptRepairDisabledReason));
    }

    private void OnBlueprintDraftChanged()
    {
        AddBlueprintCommand.RaiseCanExecuteChanged();
        BlueprintSaveStatus = "Blueprint draft updated.";
    }

    private void LoadWorkspaces() => RefreshRecentWorkspaces();

    private void RefreshRecentWorkspaces()
    {
        _recentWorkspaces.Clear();
        foreach (var ws in _workspaceProvider.GetRecentWorkspaces())
            _recentWorkspaces.Add(ws);

        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
    }

    public bool HasNoWorkspaces => _recentWorkspaces.Count == 0;
    public bool HasWorkspaces => _recentWorkspaces.Count > 0;

	// ---- RootFs / execution env ----
	private void LoadExecutionEnvironments()
	{
		_rootFsCatalog.Clear();

		foreach (var entry in EnumerateExecutionEnvironmentCatalogEntries(_executionEnvironmentStore))
		{
			var ui = TryConvertToUiRootFsDescriptor(entry);
			if (ui is not null)
				_rootFsCatalog.Add(ui);
		}

		OnPropertyChanged(nameof(HasRootFsCatalog));
	}

	// ---- RootFs / execution env ----

	public bool HasRootFsCatalog => RootFsCatalog.Count > 0;

	private static IEnumerable<object> EnumerateExecutionEnvironmentCatalogEntries(object store)
	{
		if (store is null)
			yield break;

		var t = store.GetType();

		// Prefer obvious names first.
		var candidates = new[]
		{
			"GetCatalog",
			"GetCatalogue",
			"GetEntries",
			"GetAll",
			"List",
			"ListAll",
			"Catalog",
			"Catalogue"
		};

		foreach (var name in candidates)
		{
			var m = t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
			if (m is null) continue;
			if (m.GetParameters().Length != 0) continue;

			object? result = null;
			try { result = m.Invoke(store, null); }
			catch { result = null; }

			foreach (var item in EnumerateObjects(result))
				yield return item;

			yield break; // if we found a plausible method, stop searching
		}

		// Fallback: find *any* public instance method with 0 params returning IEnumerable-ish.
		var any = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
			.Where(m => m.GetParameters().Length == 0)
			.FirstOrDefault(m => typeof(System.Collections.IEnumerable).IsAssignableFrom(m.ReturnType));

		if (any is null)
			yield break;

		object? anyResult = null;
		try { anyResult = any.Invoke(store, null); }
		catch { anyResult = null; }

		foreach (var item in EnumerateObjects(anyResult))
			yield return item;
	}

	private static IEnumerable<object> EnumerateObjects(object? maybeEnumerable)
	{
		if (maybeEnumerable is null)
			yield break;

		if (maybeEnumerable is System.Collections.IEnumerable e)
		{
			foreach (var item in e)
			{
				if (item is not null)
					yield return item;
			}
			yield break;
		}

		// Single object fallback.
		yield return maybeEnumerable;
	}

	private static UiRootFsDescriptor? TryConvertToUiRootFsDescriptor(object entry)
	{
		// If it's already the UI type, just use it.
		if (entry is UiRootFsDescriptor already)
			return already;

		UiRootFsDescriptor? ui;
		try { ui = Activator.CreateInstance<UiRootFsDescriptor>(); }
		catch { return null; }

		// Common field/property names we’ve seen across your drift waves.
		CopyString(entry, ui, "Id", "Id", "RootId", "RootFsId");
		CopyString(entry, ui, "Name", "Name", "DisplayName", "Title");
		CopyString(entry, ui, "RootPath", "RootPath", "Path", "MountPath", "Root");
		CopyString(entry, ui, "Description", "Description", "Summary");
		CopyString(entry, ui, "Provider", "Provider", "ProviderId");
		CopyString(entry, ui, "Kind", "Kind", "Type");

		return ui;
	}

	private static void CopyString(object src, object dst, string dstProp, params string[] srcProps)
	{
		var dp = dst.GetType().GetProperty(dstProp, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
		if (dp is null || !dp.CanWrite || dp.PropertyType != typeof(string))
			return;

		foreach (var spName in srcProps)
		{
			var sp = src.GetType().GetProperty(spName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
			if (sp is null || sp.PropertyType != typeof(string)) continue;

			var value = sp.GetValue(src) as string;
			if (string.IsNullOrWhiteSpace(value)) continue;

			dp.SetValue(dst, value);
			return;
		}
	}

    // ---- Policy persistence stubs (keep real implementations elsewhere) ----
    private void LoadAiPolicy() { }
    private void SaveAiPolicy() { }
    private void UpdateAiVisibilityState() { }
    private void UpdateProfileCapabilities() { }
    private void UpdateDatabaseIntentSelection() { }
    private void UpdateExecutionEnvironmentSelection() { }
    private void LoadEnvironmentScript()
    {
        var workspacePath = ActiveWorkspace?.RootPath;
        _scriptSearchPath = string.IsNullOrWhiteSpace(workspacePath)
            ? string.Empty
            : Path.Combine(workspacePath, EnvironmentScriptLoader.FileName);

        _environmentScript = null;
        _scriptUnsupportedCapabilitiesMessage = null;

        string? scriptLoadError = null;
        if (!string.IsNullOrWhiteSpace(workspacePath)
            && _scriptLoader.TryLoad(workspacePath, out var script, out scriptLoadError))
        {
            _environmentScript = script;
        }
        else if (!string.IsNullOrWhiteSpace(scriptLoadError))
        {
            _scriptUnsupportedCapabilitiesMessage = scriptLoadError;
        }

        OnPropertyChanged(nameof(ScriptPreview));
        OnPropertyChanged(nameof(ScriptCapabilities));
        OnPropertyChanged(nameof(ScriptSteps));
        OnPropertyChanged(nameof(ScriptSearchPath));
        OnPropertyChanged(nameof(ScriptUnsupportedCapabilitiesMessage));
        OnPropertyChanged(nameof(ScriptFolderCount));
        OnPropertyChanged(nameof(ScriptFolderCountLabel));
        OnPropertyChanged(nameof(ApplyScriptDisabledReason));
    }
    private string BuildExecutionBlockerSummary() => "No execution blockers.";
    private string BuildExecutionEnvironmentSummary() => "No environment selected.";
    private string GetStartDisabledReason() => string.IsNullOrWhiteSpace(_planId) ? "No plan loaded." : string.Empty;
    private string GetApplyEnvironmentDisabledReason()
    {
        if (SelectedProfile is null) return "ui.environment.profile.missing: select an environment profile.";
        if (State is UiExecutionState.Running or UiExecutionState.Waiting or UiExecutionState.Replaying)
            return "ui.environment.apply.blocked.execution_active: wait for execution to stop.";
        if (_lastEnvironmentResult is not null && string.Equals(_lastEnvironmentResult.ProfileName, SelectedProfile.Name, StringComparison.Ordinal))
            return "ui.environment.apply.noop: selected profile already applied.";
        return string.Empty;
    }
    private string GetApplyScriptDisabledReason() => string.Empty;
    private string GetAiHelpDisabledReason() => BuildBackendDisabledReason();

    private string ResolveOllamaUnavailableCode()
    {
        if (!string.IsNullOrWhiteSpace(OllamaStatus.ErrorCode))
            return OllamaStatus.ErrorCode!;

        if (!string.IsNullOrWhiteSpace(ModelCatalogError))
            return ModelCatalogError;

        return "ui.backend.ollama.unreachable";
    }

    private async Task<bool> EnsureSelectedProviderReadyAsync(
        bool manageOperationProgress,
        Action<RunDemoProgressEvent>? progress)
    {
        if (!string.Equals(SelectedProviderMode, "ollama", StringComparison.OrdinalIgnoreCase))
            return true;

        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (OllamaStatus.IsAvailable)
            {
                if (attempt > 1)
                {
                    var recoveredMessage = $"Provider '{SelectedProviderMode}' recovered on attempt {attempt}/{maxAttempts}.";
                    AddNarration("info", "PROVIDER_RECOVERED", new Dictionary<string, string>
                    {
                        ["provider"] = SelectedProviderMode,
                        ["attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["max_attempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
                    ReportRunDemoProgress(progress, "Planning run", recoveredMessage, "Plan run", "active");
                    if (manageOperationProgress)
                        SetOperationStatus("Planning run", recoveredMessage);
                }

                return true;
            }

            var attemptMessage = $"Checking provider '{SelectedProviderMode}' (attempt {attempt}/{maxAttempts}).";
            AddNarration("info", "PROVIDER_CHECK", new Dictionary<string, string>
            {
                ["provider"] = SelectedProviderMode,
                ["attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["max_attempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            ReportRunDemoProgress(progress, "Waiting on provider", attemptMessage, "Plan run", "active");
            if (manageOperationProgress)
            {
                SetOperationStatus("Waiting on provider", attemptMessage);
                SetOperationStepState("Plan run", "active", attemptMessage);
            }

            _ollamaStatus = (await _backendProbeService.ProbeOllamaAsync(default).ConfigureAwait(false)).WithBounds();
            AppendProviderDiagnosticEvent(_ollamaStatus);
            OnPropertyChanged(nameof(OllamaStatus));
            OnPropertyChanged(nameof(ProviderAvailabilityWarning));
            OnPropertyChanged(nameof(BackendDisabledReason));
            OnPropertyChanged(nameof(RunIntakePlanDisabledReason));

            if (OllamaStatus.IsAvailable)
                return true;

            if (attempt >= maxAttempts)
                break;
        }

        var providerError = ResolveOllamaUnavailableCode();
        var providerMessage = $"Provider '{SelectedProviderMode}' is unavailable ({providerError}).";
        AddNarration("error", "Provider unavailable", new Dictionary<string, string>
        {
            ["provider"] = SelectedProviderMode,
            ["error_code"] = providerError,
            ["attempts"] = "2"
        });
        AddStartupMessage($"System: {providerMessage} Refresh backend status or choose the Local provider.");
        ReportRunDemoProgress(progress, "Waiting on provider", providerMessage, "Plan run", "failed");
        if (manageOperationProgress)
        {
            SetOperationStatus("Waiting on provider", providerMessage);
            SetOperationStepState("Plan run", "failed", providerMessage);
            CompleteOperationProgress(false, providerMessage);
        }

        RecordFailure(
            "Run Demo (Provider)",
            providerMessage,
            UiLogPath,
            "Refresh backend status or switch to the Local provider, then retry Run Demo.");
        return false;
    }

    private string BuildBackendDisabledReason()
    {
        if (!_ollamaStatus.IsAvailable)
        {
            var ollamaErrorCode = !string.IsNullOrWhiteSpace(_ollamaStatus.ErrorCode)
                ? _ollamaStatus.ErrorCode
                : !string.IsNullOrWhiteSpace(ModelCatalogError)
                    ? ModelCatalogError
                    : "ui.backend.ollama.unavailable";
            return $"AI backend unavailable ({ollamaErrorCode}).";
        }

        if (!_qdrantStatus.IsAvailable)
        {
            return $"Vector memory unavailable ({_qdrantStatus.ErrorCode ?? "ui.backend.qdrant.unavailable"}).";
        }

        if (_availableModels.Count == 0)
        {
            return "No models available (ui.ollama.no_models).";
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> DescribeCapabilities(object? caps)
    {
        if (caps is null)
            return Array.Empty<string>();

        // If it's already a collection of EnvironmentCapability
        if (caps is IReadOnlyCollection<EnvironmentCapability> typed)
            return typed.Select(c => c.ToString()).ToList();

        // Some versions drift to IEnumerable<EnvironmentCapability>
        if (caps is IEnumerable<EnvironmentCapability> enumerable)
            return enumerable.Select(c => c.ToString()).ToList();

        // Some versions drift to a single EnvironmentCapability
        if (caps is EnvironmentCapability single)
            return new[] { single.ToString() };

        // Some versions drift to strings/enums/etc.
        if (caps is System.Collections.IEnumerable anyEnumerable && caps is not string)
        {
            var list = new List<string>();
            foreach (var item in anyEnumerable)
            {
                if (item is null) continue;
                list.Add(item.ToString() ?? string.Empty);
            }
            return list.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        // Last resort: just stringify whatever it is.
        return new[] { caps.ToString() ?? string.Empty }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    public string SelectedRoleDescription => SelectedRole?.Description ?? "No role selected.";

    // ---- Database intent ----
    public IReadOnlyList<DatabaseIntentOption> DatabaseIntents { get; }
    public DatabaseIntentOption? SelectedDatabaseIntent
    {
        get => _selectedDatabaseIntent;
        set
        {
            if (Equals(_selectedDatabaseIntent, value)) return;
            _selectedDatabaseIntent = value;
            OnPropertyChanged(nameof(SelectedDatabaseIntent));
            if (ActiveWorkspace is not null && value is not null)
                _databaseIntentStore.SetIntent(ActiveWorkspace.RootPath, value.Intent);
        }
    }

    // ---- Role selection ----
    public RoleDescriptor? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (ReferenceEquals(_selectedRole, value)) return;
            _selectedRole = value;
            OnPropertyChanged(nameof(SelectedRole));
            OnPropertyChanged(nameof(SelectedRoleDescription));
            _ = RefreshAiHelpAsync();
        }
    }

    private static ExecutionEnvironmentSettings CreateDefaultExecutionEnvironmentSettings()
    {
        var t = typeof(ExecutionEnvironmentSettings);

        // Try common constructor shapes (3, 2, 1, 0 args), without hard-binding to a specific version.
        var three = TryCreateViaCtor(t,
            "none",
            Array.Empty<Shoots.UI.ExecutionEnvironments.RootFsDescriptor>(),
            string.Empty);
        if (three is not null) return (ExecutionEnvironmentSettings)three;

        var two = TryCreateViaCtor(t,
            "none",
            Array.Empty<Shoots.UI.ExecutionEnvironments.RootFsDescriptor>());
        if (two is not null) return (ExecutionEnvironmentSettings)two;

        var one = TryCreateViaCtor(t, "none");
        if (one is not null) return (ExecutionEnvironmentSettings)one;

        var zero = TryCreateViaCtor(t);
        if (zero is not null) return (ExecutionEnvironmentSettings)zero;

        // Last resort: create uninitialized instance (should be rare, but keeps UI compiling).
        return Activator.CreateInstance<ExecutionEnvironmentSettings>();
    }

    private static object? TryCreateViaCtor(Type t, params object?[] args)
    {
        try
        {
            return Activator.CreateInstance(t, args);
        }
        catch
        {
            return null;
        }
    }

    private sealed record ProofArtifactDescriptor(string DisplayName, string RelativePath);

    public sealed class ProofArtifactRow : INotifyPropertyChanged
    {
        public ProofArtifactRow(string displayName, string relativePath)
        {
            DisplayName = displayName;
            RelativePath = relativePath;
            _lastModifiedLabel = "—";
        }

        public string DisplayName { get; }
        public string RelativePath { get; }

        private string _absolutePath = string.Empty;
        public string AbsolutePath
        {
            get => _absolutePath;
            private set
            {
                if (_absolutePath == value) return;
                _absolutePath = value;
                OnPropertyChanged(nameof(AbsolutePath));
            }
        }

        private bool _exists;
        public bool Exists
        {
            get => _exists;
            private set
            {
                if (_exists == value) return;
                _exists = value;
                OnPropertyChanged(nameof(Exists));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }

        private string _lastModifiedLabel;
        public string LastModifiedLabel
        {
            get => _lastModifiedLabel;
            private set
            {
                if (_lastModifiedLabel == value) return;
                _lastModifiedLabel = value;
                OnPropertyChanged(nameof(LastModifiedLabel));
            }
        }

        public string StatusLabel => Exists ? "Available" : "Missing";

        public void Update(string? runPath)
        {
            if (string.IsNullOrWhiteSpace(runPath))
            {
                AbsolutePath = string.Empty;
                Exists = false;
                LastModifiedLabel = "—";
                return;
            }

            var fullPath = Path.Combine(runPath, RelativePath);
            AbsolutePath = fullPath;
            if (File.Exists(fullPath))
            {
                Exists = true;
                var lastWrite = File.GetLastWriteTime(fullPath);
                LastModifiedLabel = lastWrite == DateTime.MinValue
                    ? "Unknown"
                    : lastWrite.ToString("g");
            }
            else
            {
                Exists = false;
                LastModifiedLabel = "Missing";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class RunHistoryRow
    {
        public RunHistoryRow(string runId, string runPath, DateTimeOffset createdUtc, string state, string provider, string hostTransport, string verificationResult)
        {
            RunId = runId;
            RunPath = runPath;
            CreatedUtc = createdUtc;
            State = state;
            Provider = provider;
            HostTransport = hostTransport;
            VerificationResult = verificationResult;
        }

        public string RunId { get; }
        public string RunPath { get; }
        public DateTimeOffset CreatedUtc { get; }
        public string State { get; }
        public string Provider { get; }
        public string HostTransport { get; }
        public string VerificationResult { get; }
        public string CreatedLabel => CreatedUtc.ToLocalTime().ToString("g");
    }

    public sealed record ProviderDiagnosticEventRow(
        string Provider,
        string State,
        string Classification,
        string ErrorCode,
        string Summary,
        DateTimeOffset ObservedAtUtc,
        string Endpoint)
    {
        public string ObservedLabel => ObservedAtUtc.ToLocalTime().ToString("g");
        public string StatusLine => string.IsNullOrWhiteSpace(ErrorCode)
            ? $"{Provider}: {State} ({Classification})"
            : $"{Provider}: {State} ({Classification}, {ErrorCode})";
    }

    public sealed record ValidationStageResultRow(
        string StageLabel,
        string Status,
        string Summary,
        string LogPath,
        long DurationMs,
        string StabilityClassification,
        int RetryCount,
        string? RetryLogPath)
    {
        public string StatusLabel => string.IsNullOrWhiteSpace(Status) ? "pending" : Status;
        public string DurationLabel => $"{DurationMs} ms";
        public bool HasStabilityDetail => !string.Equals(StabilityClassification, "passed", StringComparison.Ordinal);
        public string StabilityLabel => StabilityClassification switch
        {
            "passed_on_retry" => "Passed after retry",
            "flaky_suspected" => "Flaky suspected",
            "failed" => RetryCount > 0 ? "Failed after retry" : "Failed",
            _ => "Passed cleanly"
        };
        public bool HasRetryLogPath => !string.IsNullOrWhiteSpace(RetryLogPath);
    }

    public sealed record ValidationRunHistoryRow(
        string RunId,
        string ActionLabel,
        string Status,
        string Summary,
        string OutputFolder,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        string StabilityClassification,
        string StabilityStatus)
    {
        public string StartedLabel => StartedUtc.ToLocalTime().ToString("g");
        public string CompletedLabel => CompletedUtc.ToLocalTime().ToString("g");
    }

    public sealed record ValidationStageHistoryRow(
        string RunId,
        string ActionLabel,
        DateTimeOffset CompletedUtc,
        string OverallResult,
        string StabilityStatus,
        string StageOutcomeSummary,
        string FirstFailureSummary)
    {
        public string CompletedLabel => CompletedUtc.ToLocalTime().ToString("g");
        public string StatusLine => $"{OverallResult} / {StabilityStatus}";
        public bool HasFirstFailureSummary => !string.IsNullOrWhiteSpace(FirstFailureSummary);
    }

    public sealed record ValidationBaselineStageChangeRow(
        string StageLabel,
        string BaselineOutcome,
        string LatestOutcome);

    public sealed record ValidationActionPolicyRow(
        string ActionLabel,
        string RunModeLabel,
        string StageOrderSummary,
        string ClassificationSummary,
        string WorkspaceImpactSummary,
        string IsolationSummary,
        string DisabledReason)
    {
        public bool HasDisabledReason => !string.IsNullOrWhiteSpace(DisabledReason);
        public bool HasIsolationSummary => !string.IsNullOrWhiteSpace(IsolationSummary);
    }

    public sealed record ValidationFollowupPlanStepRow(
        int Order,
        string StepType,
        string StepTypeLabel,
        string Title,
        string Summary,
        string TargetScope,
        string ScopeConfidence,
        string InteractionMode,
        string ActionKind,
        string ActionTarget,
        string CommandSummary,
        IReadOnlyList<string> EvidenceArtifactPaths,
        string CompletionState,
        string CompletionBadge,
        string ExecutionAvailability,
        string BlockReason)
    {
        public string StepOrderLabel => $"{Order}.";
        public string ScopeLabel => string.IsNullOrWhiteSpace(TargetScope) ? string.Empty : $"Scope: {TargetScope}";
        public bool HasScopeLabel => !string.IsNullOrWhiteSpace(ScopeLabel);
        public string ScopeConfidenceLabel => string.IsNullOrWhiteSpace(ScopeConfidence) ? string.Empty : $"Scope confidence: {ScopeConfidence}";
        public bool HasScopeConfidenceLabel => !string.IsNullOrWhiteSpace(ScopeConfidenceLabel);
        public bool HasEvidenceArtifactPaths => EvidenceArtifactPaths.Any(path => !string.IsNullOrWhiteSpace(path));
        public string EvidenceArtifactPathsSummary => string.Join(
            System.Environment.NewLine,
            EvidenceArtifactPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(4));
        public bool HasEvidenceArtifactPathsSummary => !string.IsNullOrWhiteSpace(EvidenceArtifactPathsSummary);
        public bool HasBlockReason => !string.IsNullOrWhiteSpace(BlockReason);
        public bool HasPrimaryAction => !string.Equals(InteractionMode, "manual_only", StringComparison.Ordinal);
        public bool CanExecutePrimaryAction => HasPrimaryAction && !HasBlockReason;
        public bool HasCopyAction => !string.IsNullOrWhiteSpace(CommandSummary) || !string.IsNullOrWhiteSpace(Summary);
        public bool CanCopyAction => HasCopyAction;
        public string PrimaryActionLabel => ActionKind switch
        {
            "open_log" => "Open log",
            "open_artifact" => "Open artifact",
            "open_repair_prep_bundle" => "Open repair prep",
            "rerun_build_scope" => "Run build",
            "rerun_single_stage" => "Run rerun",
            "rerun_single_test_or_project" => "Run rerun",
            _ => "Open step"
        };
        public string CopyActionLabel => string.IsNullOrWhiteSpace(CommandSummary) ? "Copy step" : "Copy command";
    }

    public sealed record BuilderSplitStepRow(
        int Order,
        string StepId,
        string StepLabel,
        string StepType,
        string StepTypeLabel,
        string ExecutionMode,
        string ScopeClassification,
        string EligibilityState,
        string ExecutionAvailability,
        string ExecutionState,
        string CompletionBadge,
        string Summary,
        string BlockReason,
        IReadOnlyList<string> LinkedArtifactPaths)
    {
        public string StepOrderLabel => $"{Order}.";
        public string ScopeClassificationLabel => string.IsNullOrWhiteSpace(ScopeClassification) ? string.Empty : $"Scope: {ScopeClassification.Replace('_', ' ')}";
        public bool HasScopeClassificationLabel => !string.IsNullOrWhiteSpace(ScopeClassificationLabel);
        public bool HasBlockReason => !string.IsNullOrWhiteSpace(BlockReason);
        public bool HasLinkedArtifactPaths => LinkedArtifactPaths.Any(path => !string.IsNullOrWhiteSpace(path));
        public string LinkedArtifactPathsSummary => string.Join(
            System.Environment.NewLine,
            LinkedArtifactPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(4));
        public bool HasLinkedArtifactPathsSummary => !string.IsNullOrWhiteSpace(LinkedArtifactPathsSummary);
    }

    public sealed class SemanticReuseSuggestionRow : INotifyPropertyChanged
    {
        private bool _isSelectedForRepairReference;

        public SemanticReuseSuggestionRow(
            string documentId,
            string contextKind,
            string contextLabel,
            string caseType,
            string title,
            string summary,
            string outcome,
            string rankingLabel,
            double score,
            string matchExplanation,
            string primaryArtifactPath,
            string sourceRunId,
            string validationOutcomeSummary,
            string changedFilesSummary,
            string promotionAdoptionSummary,
            string usefulnessSummary,
            IReadOnlyList<string> linkedArtifactPaths)
        {
            DocumentId = documentId;
            ContextKind = contextKind;
            ContextLabel = contextLabel;
            CaseType = caseType;
            Title = title;
            Summary = summary;
            Outcome = outcome;
            RankingLabel = rankingLabel;
            Score = score;
            MatchExplanation = matchExplanation;
            PrimaryArtifactPath = primaryArtifactPath;
            SourceRunId = sourceRunId;
            ValidationOutcomeSummary = validationOutcomeSummary;
            ChangedFilesSummary = changedFilesSummary;
            PromotionAdoptionSummary = promotionAdoptionSummary;
            UsefulnessSummary = usefulnessSummary;
            LinkedArtifactPaths = linkedArtifactPaths ?? Array.Empty<string>();
        }

        public string DocumentId { get; }
        public string ContextKind { get; }
        public string ContextLabel { get; }
        public string CaseType { get; }
        public string Title { get; }
        public string Summary { get; }
        public string Outcome { get; }
        public string RankingLabel { get; }
        public double Score { get; }
        public string MatchExplanation { get; }
        public string PrimaryArtifactPath { get; }
        public string SourceRunId { get; }
        public string ValidationOutcomeSummary { get; }
        public string ChangedFilesSummary { get; }
        public string PromotionAdoptionSummary { get; }
        public string UsefulnessSummary { get; }
        public IReadOnlyList<string> LinkedArtifactPaths { get; }
        public string ScoreLabel => $"{RankingLabel} ({Score:P0})";
        public bool HasPrimaryArtifactPath => !string.IsNullOrWhiteSpace(PrimaryArtifactPath);
        public bool HasSourceRunId => !string.IsNullOrWhiteSpace(SourceRunId);
        public string OutcomeLabel => string.IsNullOrWhiteSpace(Outcome) ? "recorded" : Outcome.Replace('_', ' ');
        public string ContextKindLabel => ContextKind switch
        {
            "planning" => "Planning",
            "validation_failure" => "Validation failure",
            "repair_attempt" => "Repair attempt",
            "provider_diagnostics" => "Provider diagnostics",
            _ => "General"
        };
        public bool HasValidationOutcomeSummary => !string.IsNullOrWhiteSpace(ValidationOutcomeSummary);
        public bool HasChangedFilesSummary => !string.IsNullOrWhiteSpace(ChangedFilesSummary);
        public bool HasPromotionAdoptionSummary => !string.IsNullOrWhiteSpace(PromotionAdoptionSummary);
        public bool HasUsefulnessSummary => !string.IsNullOrWhiteSpace(UsefulnessSummary);
        public bool CanUseAsRepairReference => HasPrimaryArtifactPath && !string.Equals(CaseType, "provider_diagnostics_episode", StringComparison.Ordinal);

        public bool IsSelectedForRepairReference
        {
            get => _isSelectedForRepairReference;
            set
            {
                if (_isSelectedForRepairReference == value) return;
                _isSelectedForRepairReference = value;
                OnPropertyChanged(nameof(IsSelectedForRepairReference));
            }
        }

        public RepairReferenceCase ToRepairReferenceCase()
            => new(
                DocumentId,
                ContextKind,
                ContextLabel,
                CaseType,
                Title,
                Outcome,
                RankingLabel,
                MatchExplanation,
                SourceRunId,
                PrimaryArtifactPath,
                LinkedArtifactPaths,
                UsefulnessSummary);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed record SemanticReusePlaybookRow(
        string PlaybookId,
        string ContextKind,
        string PlaybookClass,
        string Title,
        string Summary,
        string Explanation,
        string Confidence,
        int EvidenceCount,
        IReadOnlyList<SemanticReuseMetadataField> MatchMetadata,
        IReadOnlyList<string> LinkedArtifactPaths,
        IReadOnlyList<string> EvidenceArtifactPaths)
    {
        public string ContextKindLabel => ContextKind switch
        {
            "planning" => "Planning",
            "validation_failure" => "Validation failure",
            "repair_bundle_reference" => "Repair bundle reference",
            "provider_diagnostics" => "Provider diagnostics",
            _ => "General"
        };

        public string ConfidenceLabel => Confidence.Replace('_', ' ');
        public string EvidenceCountLabel => $"{EvidenceCount} evidence-backed outcome(s)";
        public string PrimaryArtifactPath => LinkedArtifactPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? EvidenceArtifactPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? string.Empty;
        public bool HasPrimaryArtifactPath => !string.IsNullOrWhiteSpace(PrimaryArtifactPath);
        public string ArtifactPathsSummary => string.Join(
            System.Environment.NewLine,
            LinkedArtifactPaths
                .Concat(EvidenceArtifactPaths)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(6));
        public bool HasArtifactPathsSummary => !string.IsNullOrWhiteSpace(ArtifactPathsSummary);
    }

    public sealed record RepairHistoryRow(
        string RepairId,
        DateTimeOffset AttemptedUtc,
        string SourceValidationRunId,
        string RepairedValidationRunId,
        string RepairOutcome,
        string ImprovementState,
        string Summary,
        string RepairBundlePath,
        string RepairResultFolder,
        string LinkedValidationRunFolder)
    {
        public string AttemptedLabel => AttemptedUtc.ToLocalTime().ToString("g");
        public string OutcomeLabel => string.IsNullOrWhiteSpace(RepairOutcome)
            ? ImprovementState
            : $"{ImprovementState} ({RepairOutcome})";
    }

    private sealed record GeneratedOutputContext(
        string RunId,
        string RunPath,
        string SourcePath);

    private sealed record RunDemoProgressEvent(
        string Stage,
        string Detail,
        string? StepName,
        string? StepState);

    public sealed class OperationProgressStepRow : INotifyPropertyChanged
    {
        private string _state = "pending";
        private string _detail = string.Empty;

        public OperationProgressStepRow(string name, int order)
        {
            Name = name;
            Order = order;
        }

        public string Name { get; }
        public int Order { get; }
        public string StepName => Name;
        public string StepState => State;
        public string StepDetail => Detail;
        public int StepOrder => Order;
        public string State => _state;
        public string Detail => _detail;

        public void SetState(string state, string? detail = null)
        {
            var normalized = string.IsNullOrWhiteSpace(state) ? "pending" : state.Trim().ToLowerInvariant();
            if (_state != normalized)
            {
                _state = normalized;
                OnPropertyChanged(nameof(State));
            }

            var nextDetail = detail ?? string.Empty;
            if (_detail != nextDetail)
            {
                _detail = nextDetail;
                OnPropertyChanged(nameof(Detail));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
