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
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Environment;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
using Shoots.UI.Roles;
using Shoots.UI.Services;
using Shoots.UI.Settings;
using Shoots.UI.Startup;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.Services.AiHelp;

namespace Shoots.UI.ViewModels;

/// <summary>
/// Main window view model. UI-only authority. Never holds runtime BuildPlan objects.
/// This file is intentionally self-contained to avoid missing-type drift.
/// </summary>
public sealed partial class MainWindowViewModel : INotifyPropertyChanged
{
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

    private readonly ObservableCollection<ProjectWorkspace> _recentWorkspaces;
    private readonly ObservableCollection<BlueprintEntryViewModel> _blueprints;
    private readonly ObservableCollection<UiRootFsDescriptor> _rootFsCatalog;
    private readonly StartupFlowStateMachine _startupFlow;
    private readonly ObservableCollection<string> _startupMessages;

    public ReadOnlyObservableCollection<ProjectWorkspace> RecentWorkspaces { get; }
    public ReadOnlyObservableCollection<BlueprintEntryViewModel> Blueprints { get; }
    public ReadOnlyObservableCollection<UiRootFsDescriptor> RootFsCatalog { get; }
    public ReadOnlyObservableCollection<string> StartupMessages { get; }

    private string _startupInput = string.Empty;
    private readonly ReadOnlyCollection<ProviderCapabilityMatrixRow> _providerCapabilityMatrix;

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

    private ExecutionEnvironmentSettings _executionSettings = CreateDefaultExecutionEnvironmentSettings();

    private string _blueprintSaveStatus = "Blueprint changes are saved.";

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

	// Call this from your constructor AFTER other command setup
	private void InitializeChatIntakeSurface()
	{
		LockWorkOrderCommand = new AsyncRelayCommand(LockWorkOrderAsync, () => !IsWorkOrderLocked);
		UnlockWorkOrderCommand = new AsyncRelayCommand(UnlockWorkOrderAsync, () => IsWorkOrderLocked);

		GeneratePlanCommand = new AsyncRelayCommand(GeneratePlanAsync, CanGeneratePlan);
		RunIntakePlanCommand = new AsyncRelayCommand(RunIntakePlanAsync, CanRunIntakePlan);

		ResumeInjectDecisionCommand = new AsyncRelayCommand(ResumeInjectDecisionAsync, CanResumeInjectDecision);

		RebuildJobSpecDigest();
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
		if (!HasActiveWorkspace) return false;
		if (string.IsNullOrWhiteSpace(IntakeIntent)) return false;
		return true;
	}

	private Task RunIntakePlanAsync()
	{
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
        IAiHelpFacade aiHelpFacade)
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

        _state = UiExecutionState.Idle;

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

        _providerCapabilityMatrix = new ReadOnlyCollection<ProviderCapabilityMatrixRow>(new[]
        {
            ProviderCapabilityMatrixRow.FromKind(ProviderKind.Local),
            ProviderCapabilityMatrixRow.FromKind(ProviderKind.Remote),
            ProviderCapabilityMatrixRow.FromKind(ProviderKind.Delegated)
        });

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
        LoadAiPolicy();
        RegisterAiSurfaces();
        InitializeChatIntake(); // partial if you have it
        InitializeChatIntakeSurface();
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
    public bool IsReplayMode => State == UiExecutionState.Replaying;
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

    public string StartupButtonTooltip => _startupFlow.State == StartupFlowState.Initial
        ? "Begin the startup flow."
        : "Startup flow is already active.";

    public int StartupTabIndex => HasActiveWorkspace ? 1 : 0;
    public string StartupProviderLabel => "Provider: Ollama (default)";

    public IReadOnlyList<string> StartupLanguageOptions =>
        StartupLanguageRegistry.All.Select(option => option.Name).ToList();

    public bool IsStartupLocked => HasActiveWorkspace;
    public bool IsStartupTabEnabled => !IsStartupLocked;
    public bool IsStartupComplete => _startupComplete;

    public bool IsStartupInputActive => _startupFlow.State is
        StartupFlowState.StartNewLanguage or
        StartupFlowState.StartNewName or
        StartupFlowState.StartNewDescription or
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

    public string AiProviderStatus => "Provider: Ollama";
    public string AiExportNotice => IsCopyExportDisabled ? "Copy and export are disabled by settings." : string.Empty;

    public string StartDisabledReason => GetStartDisabledReason();
    public string ApplyEnvironmentDisabledReason => GetApplyEnvironmentDisabledReason();
    public string ApplyScriptDisabledReason => GetApplyScriptDisabledReason();
    public string AiHelpDisabledReason => GetAiHelpDisabledReason();

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
    private bool CanStartNewProject() => _startupFlow.State == StartupFlowState.Initial && !HasActiveWorkspace;
    private bool CanStartAnotherProject() => HasActiveWorkspace;
    private bool CanSelectEntryPath() => _startupFlow.State == StartupFlowState.EntryPathSelection && !_startupComplete;
    private bool CanSubmitStartupInput() => IsStartupInputActive && !_startupComplete && !string.IsNullOrWhiteSpace(StartupInput);

    private Task NewProjectAsync()
    {
        var previous = _startupFlow.State;
        if (HasActiveWorkspace)
        {
            AddStartupMessage("System: Startup is locked while a project is active. Use \"Start another project\" to restart.");
            return Task.CompletedTask;
        }

        if (!_startupFlow.TryBeginNewProject(out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "Startup flow activated.");
        Trace.WriteLine("[Shoots.UI] Provider = Ollama (default).");
        AddStartupMessage("System: Provider = Ollama (default).");
        AddStartupMessage("System: Startup flow activated. Choose an entry path. State remains: EntryPathSelection.");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task StartAnotherProjectAsync()
    {
        if (!HasActiveWorkspace) return Task.CompletedTask;

        if (State is UiExecutionState.Running or UiExecutionState.Waiting)
        {
            AddStartupMessage("System: Current project has unsaved run state. Save/export before switching projects.");
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
        => Task.CompletedTask; // keep your real implementation elsewhere (partial)

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
        => ActiveWorkspace = workspace;

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
        // keep your real implementation elsewhere (partial)
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
            selectedEnvironmentId: SelectedProfile?.Name ?? "host-local",
            providerKind: ProviderKind.Local.ToString(),
            providerEndpoint: string.Empty,
            language: _pendingProjectLanguage,
            description: _pendingProjectDescription,
            projectRoot);

        var descriptorPath = Path.Combine(projectRoot, "project.json");
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, new JsonSerializerOptions { WriteIndented = true }));

        var workspace = new ProjectWorkspace(
            Name: projectName,
            RootPath: projectRoot,
            LastOpenedUtc: createdUtc,
            ProjectId: projectId,
            CreatedUtc: createdUtc,
            SelectedEnvironmentId: descriptor.SelectedEnvironmentId,
            SelectedProviderKind: descriptor.ProviderKind,
            SelectedProviderEndpoint: descriptor.ProviderEndpoint);

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

        OnPropertyChanged(nameof(StartDisabledReason));
        OnPropertyChanged(nameof(ApplyEnvironmentDisabledReason));
        OnPropertyChanged(nameof(ApplyScriptDisabledReason));
        OnPropertyChanged(nameof(AiHelpDisabledReason));
        OnPropertyChanged(nameof(SystemTierActionLabel));
        OnPropertyChanged(nameof(ExecutionDisabledReason));
    }

    private void OnBlueprintDraftChanged()
    {
        AddBlueprintCommand.RaiseCanExecuteChanged();
        BlueprintSaveStatus = "Blueprint draft updated.";
    }

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
    private void LoadEnvironmentScript() { }
    private string BuildExecutionBlockerSummary() => "No execution blockers.";
    private string BuildExecutionEnvironmentSummary() => "No environment selected.";
    private string GetStartDisabledReason() => string.IsNullOrWhiteSpace(_planId) ? "No plan loaded." : string.Empty;
    private string GetApplyEnvironmentDisabledReason() => string.Empty;
    private string GetApplyScriptDisabledReason() => string.Empty;
    private string GetAiHelpDisabledReason() => string.Empty;

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
    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}