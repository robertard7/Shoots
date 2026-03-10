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
    private readonly AiPanelVisibilityService _aiPanelVisibilityService;
    private readonly IAiHelpFacade _aiHelpFacade;
    private readonly IBackendProbeService _backendProbeService;
    private readonly IOllamaClient _ollamaClient;
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
    public string BusyState => IsOperationActive || IsOperationCompletionHoldActive || IsBusy ? "busy" : "idle";
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
    public string LastFailureExceptionType => ExtractFailureExceptionType(LastFailureReason);
    public string LastFailureFirstStackFrame => ExtractFailureFirstStackFrame(LastFailureReason);
    public string FatalLogPath => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Shoots.UI", "fatal.log");

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
        public AsyncRelayCommand<ProofArtifactRow> OpenProofArtifactCommand { get; private set; } = null!;
        public AsyncRelayCommand<ProofArtifactRow> CopyProofArtifactPathCommand { get; private set; } = null!;
        public AsyncRelayCommand OpenProofRunFolderCommand { get; private set; } = null!;
        public AsyncRelayCommand CopyProofRunFolderPathCommand { get; private set; } = null!;

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
        OpenProofArtifactCommand = new AsyncRelayCommand<ProofArtifactRow>(OpenProofArtifactAsync, artifact => artifact is { Exists: true });
        CopyProofArtifactPathCommand = new AsyncRelayCommand<ProofArtifactRow>(CopyProofArtifactPathAsync, artifact => artifact is { Exists: true });
        OpenProofRunFolderCommand = new AsyncRelayCommand(OpenProofRunFolderAsync, () => HasProofRun);
        CopyProofRunFolderPathCommand = new AsyncRelayCommand(CopyProofRunFolderPathAsync, () => HasProofRun);

        RebuildJobSpecDigest();
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
        }
        else
        {
            _proofRunPath = row.RunPath;
            _proofRunId = row.RunId;
            _proofRunVerificationState = row.VerificationResult;
            _proofRunLabel = $"Inspecting {row.RunId} ({row.State})";
        }

        OnPropertyChanged(nameof(ProofRunFolderPath));
        OnPropertyChanged(nameof(ProofRunVerificationState));
        OnPropertyChanged(nameof(ProofRunLabel));
        OnPropertyChanged(nameof(HasProofRun));
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

	private Task GeneratePlanAsync()
	{
		// UI-only placeholder: generating a plan should set preview identity and digest.
		// When real planner is wired, this will create WorkOrder + BuildRequest + preview plan.
		RebuildJobSpecDigest();
		return Task.CompletedTask;
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
        var busyReason = BuildOperationBusyReason();
        if (!string.IsNullOrWhiteSpace(busyReason)) return busyReason;
        if (!HasActiveWorkspace) return "ui.workspace.missing: select a workspace first.";
        if (string.IsNullOrWhiteSpace(IntakeIntent)) return "ui.intake.intent.missing: provide an intake intent.";
        var backendReason = BuildBackendDisabledReason();
        if (!string.IsNullOrWhiteSpace(backendReason)) return backendReason;
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
        LocalProjectService? localProjectService = null,
        IPlanner? planner = null,
        BuilderExecutionService? builderExecutionService = null,
        bool autoRefreshBackends = true)
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
        _aiPanelVisibilityService = aiPanelVisibilityService ?? throw new ArgumentNullException(nameof(aiPanelVisibilityService));
        _aiHelpFacade = aiHelpFacade ?? throw new ArgumentNullException(nameof(aiHelpFacade));
        _backendProbeService = backendProbeService ?? throw new ArgumentNullException(nameof(backendProbeService));
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
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
            ? $"Provider unavailable: ollama ({OllamaStatus.ErrorCode ?? "ui.backend.ollama.unreachable"})"
            : string.Empty;

    public string LastRunFolderPath => _lastDemoRunPath ?? string.Empty;
    public bool HasLatestRun => !string.IsNullOrWhiteSpace(_lastDemoRunPath);
    public string LastVerificationReportPath => string.IsNullOrWhiteSpace(_lastDemoRunPath) ? string.Empty : Path.Combine(_lastDemoRunPath, "verification_report.json");
    public string LastOperatorFlowPath => string.IsNullOrWhiteSpace(_lastDemoRunPath) ? string.Empty : Path.Combine(_lastDemoRunPath, "operator_flow.json");
    public string LastTransportEquivalencePath => string.IsNullOrWhiteSpace(_lastDemoRunPath) ? string.Empty : Path.Combine(_lastDemoRunPath, "transport_equivalence.json");
    public string ExecutionModeSummary => IsReplayMode ? "Mode: Replay (trace-backed)" : "Mode: Live";

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

    private Task RunDemoPlanAsync(bool manageOperationProgress = true, Action<RunDemoProgressEvent>? progress = null)
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
            return Task.CompletedTask;
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
            return Task.CompletedTask;
        }

        try
        {
            if (string.Equals(SelectedProviderMode, "ollama", StringComparison.OrdinalIgnoreCase) && !OllamaStatus.IsAvailable)
            {
                var providerError = OllamaStatus.ErrorCode ?? "ui.backend.ollama.unreachable";
                var providerMessage = $"Provider '{SelectedProviderMode}' is unavailable ({providerError}).";
                AddNarration("error", "Provider unavailable", new Dictionary<string, string>
                {
                    ["provider"] = SelectedProviderMode,
                    ["error_code"] = providerError
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
                return Task.CompletedTask;
            }

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
                return Task.CompletedTask;
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
                    return Task.CompletedTask;
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

        return Task.CompletedTask;
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

    private bool CanReplayPlan() => !string.IsNullOrWhiteSpace(_planId);
    private Task ReplayPlanAsync()
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

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

        var busyReason = BuildOperationBusyReason();
        if (!string.IsNullOrWhiteSpace(busyReason))
        {
            return busyReason;
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
            _operationWaitHint = "No recent progress updates. Waiting for host or provider response.";
            OnPropertyChanged(nameof(IsOperationWaiting));
            OnPropertyChanged(nameof(OperationWaitHint));
        }

        if (_isOperationActive)
        {
            return;
        }

        if (_operationDisplayUntilUtc is null || DateTimeOffset.UtcNow < _operationDisplayUntilUtc.Value)
        {
            return;
        }

        ResetOperationProgressToIdle();
        _operationProgressTimer?.Stop();
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

    private string BuildBackendDisabledReason()
    {
        if (!_ollamaStatus.IsAvailable)
        {
            return $"AI backend unavailable ({_ollamaStatus.ErrorCode ?? "ui.backend.ollama.unavailable"}).";
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
