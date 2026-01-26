using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.AiHelp;
using Shoots.UI.Blueprints;
using Shoots.UI.ExecutionRecords;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Environment;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
using Shoots.UI.Roles;
using Shoots.UI.Services;
using Shoots.UI.Settings;
using Shoots.UI.Startup;
using UiRootFsDescriptor = Shoots.UI.ExecutionEnvironments.RootFsDescriptor;

namespace Shoots.UI.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IExecutionCommandService _commandService;
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
    private readonly ObservableCollection<ToolExecutionSessionViewModel> _toolExecutionSessions;
    private readonly ObservableCollection<ToolExecutionRecordViewModel> _toolExecutionRecords;
    private readonly ObservableCollection<ToolExecutionRecordViewModel> _comparisonToolExecutionRecords;
	private readonly ObservableCollection<UiRootFsDescriptor> _rootFsCatalog;
	public ReadOnlyObservableCollection<UiRootFsDescriptor> RootFsCatalog { get; }
    private readonly StartupFlowStateMachine _startupFlow;
    private readonly ObservableCollection<string> _startupMessages;
    private string _startupInput = string.Empty;
    private readonly ReadOnlyCollection<ProviderCapabilityMatrixRow> _providerCapabilityMatrix;
    private ExecutionState _state;
    private BuildPlan? _plan;
    private IEnvironmentProfile? _selectedProfile;
    private EnvironmentProfileResult? _lastEnvironmentResult;
    private bool _restartRequired;
    private EnvironmentScript? _environmentScript;
    private string? _environmentErrorMessage;
    private string? _environmentInfoMessage;
    private ProjectWorkspace? _activeWorkspace;
    private string _scriptSearchPath = string.Empty;
    private string? _scriptUnsupportedCapabilitiesMessage;
    private DatabaseIntentOption? _selectedDatabaseIntent;
    private ToolpackTier _lastNonSystemTier = ToolpackTier.Public;
    private RoleDescriptor? _selectedRole;
    private string _newBlueprintName = string.Empty;
    private string _newBlueprintDescription = string.Empty;
    private string _newBlueprintIntents = string.Empty;
    private string _newBlueprintArtifacts = string.Empty;
    private string _newBlueprintVersion = "1.0";
    private string _newBlueprintDefinition = string.Empty;
    private StartupSessionMode _sessionMode = StartupSessionMode.Startup;
    private bool _startupComplete;
	private ExecutionEnvironmentSettings _executionSettings = new(
		"none",
		Array.Empty<Shoots.UI.ExecutionEnvironments.RootFsDescriptor>(),
		string.Empty
	);
    private ToolExecutionRecordViewModel? _selectedToolExecutionRecord;
    private ToolExecutionRecordViewModel? _comparisonToolExecutionRecord;
    private ToolExecutionSessionViewModel? _selectedToolExecutionSession;
    private ToolExecutionSessionViewModel? _comparisonToolExecutionSession;
    private string _blueprintSaveStatus = "Blueprint changes are saved.";
    private AiPresentationPolicy _aiPresentationPolicy =
        new(AiVisibilityMode.Visible, AllowAiPanelToggle: true, AllowCopyExport: true, EnterpriseMode: false);
    private AiAccessRole _aiAccessRole = AiAccessRole.Developer;
    private AiPanelVisibilityState _aiPanelVisibilityState = new(true, true, true);

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
        _state = ExecutionState.Idle;

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
        ExplainSelectedToolExecutionCommand = new AsyncRelayCommand(ExplainSelectedToolExecutionAsync, CanExplainToolExecution);
        Profiles = new ReadOnlyCollection<IEnvironmentProfile>(_environmentService.Profiles.ToList());
        SelectedProfile = Profiles.FirstOrDefault();
        EnvironmentCapabilities = Array.Empty<string>();
        EnvironmentCreatedPaths = Array.Empty<string>();
        EnvironmentAppliedCapabilities = Array.Empty<string>();

        _recentWorkspaces = new ObservableCollection<ProjectWorkspace>();
        RecentWorkspaces = new ReadOnlyObservableCollection<ProjectWorkspace>(_recentWorkspaces);
        _blueprints = new ObservableCollection<BlueprintEntryViewModel>();
        Blueprints = new ReadOnlyObservableCollection<BlueprintEntryViewModel>(_blueprints);
        _toolExecutionSessions = new ObservableCollection<ToolExecutionSessionViewModel>();
        ToolExecutionSessions = new ReadOnlyObservableCollection<ToolExecutionSessionViewModel>(_toolExecutionSessions);
        _toolExecutionRecords = new ObservableCollection<ToolExecutionRecordViewModel>();
        ToolExecutionRecords = new ReadOnlyObservableCollection<ToolExecutionRecordViewModel>(_toolExecutionRecords);
        _comparisonToolExecutionRecords = new ObservableCollection<ToolExecutionRecordViewModel>();
        ComparisonToolExecutionRecords = new ReadOnlyObservableCollection<ToolExecutionRecordViewModel>(_comparisonToolExecutionRecords);
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
        _ = RefreshAiHelpAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExecutionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;

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

    public bool IsReplayMode => State == ExecutionState.Replaying;

    public string ExecutionModeSummary => IsReplayMode ? "Mode: Replay (trace-backed)" : "Mode: Live";

    public string ExecutionProviderSummary => Plan is null
        ? "Provider: none"
        : $"Provider: {Plan.Authority.ProviderId.Value} ({Plan.Authority.Kind})";

    public string ExecutionGraphSummary => Plan is null
        ? "Execution graph: no plan loaded."
        : $"Execution graph hash: {Plan.GraphStructureHash}. Nodes: {Plan.NodeSetHash}. Edges: {Plan.EdgeSetHash}.";

    public IReadOnlyList<ExecutionStepSummaryViewModel> ExecutionPlanSteps =>
        BuildExecutionStepSummaries(Plan);

    public IReadOnlyList<ExecutionStepSummaryViewModel> ExecutionPreviewSteps => ExecutionPlanSteps;

    public string ExecutionDisabledReason => StartDisabledReason;

    public string ExecutionBlockerSummary => BuildExecutionBlockerSummary();

    public string ResolvedExecutionEnvironmentSummary => BuildExecutionEnvironmentSummary();

    public string ExecutionPreviewSummary => Plan is null
        ? "No execution preview is available."
        : $"Preview ready for {Plan.Steps.Count} steps (graph {Plan.GraphStructureHash}).";

    public BuildPlan? Plan
    {
        get => _plan;
        private set
        {
            if (Equals(_plan, value))
                return;

            _plan = value;
            OnPropertyChanged(nameof(Plan));
            OnPropertyChanged(nameof(ExecutionGraphSummary));
            OnPropertyChanged(nameof(ExecutionProviderSummary));
            OnPropertyChanged(nameof(ExecutionPlanSteps));
            OnPropertyChanged(nameof(ExecutionPreviewSteps));
            OnPropertyChanged(nameof(ExecutionPreviewSummary));
            OnPropertyChanged(nameof(ExecutionDisabledReason));
            OnPropertyChanged(nameof(ExecutionBlockerSummary));
            RefreshToolExecutionRecords();
            RaiseCommandCanExecute();
        }
    }

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

    public AsyncRelayCommand ExplainSelectedToolExecutionCommand { get; }

    public string StartupStateLabel => _startupFlow.State.ToString();

    public string StartupEntryPathLabel => _startupFlow.EntryPath is null
        ? "None selected"
        : FormatEntryPathLabel(_startupFlow.EntryPath.Value);

    public bool IsEntryPathSelectionActive => _startupFlow.State == StartupFlowState.EntryPathSelection;

    public IReadOnlyList<string> StartupMessages { get; }

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

    public bool IsStartNewLanguageActive => _startupFlow.State == StartupFlowState.StartNewLanguage;

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
            if (_startupInput == value)
                return;

            _startupInput = value;
            OnPropertyChanged(nameof(StartupInput));
            SubmitStartupInputCommand.RaiseCanExecuteChanged();
        }
    }

    public string SessionStatusLabel
    {
        get
        {
            return _sessionMode switch
            {
                StartupSessionMode.Project => $"Project: {ActiveWorkspace?.Name}",
                StartupSessionMode.Explore => "Explore (no writes)",
                _ => "Startup: No project active"
            };
        }
    }

    public IReadOnlyList<IEnvironmentProfile> Profiles { get; }

    public IEnvironmentProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value))
                return;

            _selectedProfile = value;
            OnPropertyChanged(nameof(SelectedProfile));
            OnPropertyChanged(nameof(SelectedProfileDescription));
            UpdateProfileCapabilities();
            RaiseCommandCanExecute();
        }
    }

    public string SelectedProfileDescription => SelectedProfile?.Description ?? string.Empty;

    public IReadOnlyList<string> EnvironmentCapabilities { get; private set; }

    public IReadOnlyList<string> EnvironmentCreatedPaths { get; private set; }

    public IReadOnlyList<string> EnvironmentAppliedCapabilities { get; private set; } = Array.Empty<string>();

    public string EnvironmentAppliedAtUtc { get; private set; } = "Not applied";

    public string EnvironmentAppliedProfileName { get; private set; } = "None";

    public string? EnvironmentErrorMessage
    {
        get => _environmentErrorMessage;
        private set
        {
            if (_environmentErrorMessage == value)
                return;

            _environmentErrorMessage = value;
            OnPropertyChanged(nameof(EnvironmentErrorMessage));
        }
    }

    public string? EnvironmentInfoMessage
    {
        get => _environmentInfoMessage;
        private set
        {
            if (_environmentInfoMessage == value)
                return;

            _environmentInfoMessage = value;
            OnPropertyChanged(nameof(EnvironmentInfoMessage));
        }
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

    public ReadOnlyObservableCollection<ProjectWorkspace> RecentWorkspaces { get; }

    public ReadOnlyObservableCollection<BlueprintEntryViewModel> Blueprints { get; }

    public bool HasBlueprints => _blueprints.Count > 0;

    public ReadOnlyObservableCollection<ToolExecutionSessionViewModel> ToolExecutionSessions { get; }

    public ReadOnlyObservableCollection<ToolExecutionRecordViewModel> ToolExecutionRecords { get; }

    public bool HasToolExecutionRecords => _toolExecutionRecords.Count > 0;

    public ReadOnlyObservableCollection<ToolExecutionRecordViewModel> ComparisonToolExecutionRecords { get; }

    public ToolExecutionSessionViewModel? SelectedToolExecutionSession
    {
        get => _selectedToolExecutionSession;
        set
        {
            if (ReferenceEquals(_selectedToolExecutionSession, value))
                return;

            _selectedToolExecutionSession = value;
            RefreshToolExecutionRecordsForSession();
            if (_comparisonToolExecutionSession is null && value is not null)
                ComparisonToolExecutionSession = value;
            OnPropertyChanged(nameof(SelectedToolExecutionSession));
        }
    }

    public ToolExecutionSessionViewModel? ComparisonToolExecutionSession
    {
        get => _comparisonToolExecutionSession;
        set
        {
            if (ReferenceEquals(_comparisonToolExecutionSession, value))
                return;

            _comparisonToolExecutionSession = value;
            RefreshComparisonToolExecutionRecords();
            OnPropertyChanged(nameof(ComparisonToolExecutionSession));
            OnPropertyChanged(nameof(ToolExecutionDiffSummary));
        }
    }

    public ToolExecutionRecordViewModel? SelectedToolExecutionRecord
    {
        get => _selectedToolExecutionRecord;
        set
        {
            if (ReferenceEquals(_selectedToolExecutionRecord, value))
                return;

            _selectedToolExecutionRecord = value;
            OnPropertyChanged(nameof(SelectedToolExecutionRecord));
            OnPropertyChanged(nameof(SelectedToolExecutionInputs));
            OnPropertyChanged(nameof(SelectedToolExecutionOutputs));
            OnPropertyChanged(nameof(SelectedToolExecutionError));
            OnPropertyChanged(nameof(ToolExecutionDetailsTitle));
            OnPropertyChanged(nameof(ToolExecutionDiffSummary));
            ExplainSelectedToolExecutionCommand.RaiseCanExecuteChanged();
        }
    }

    public ToolExecutionRecordViewModel? ComparisonToolExecutionRecord
    {
        get => _comparisonToolExecutionRecord;
        set
        {
            if (ReferenceEquals(_comparisonToolExecutionRecord, value))
                return;

            _comparisonToolExecutionRecord = value;
            OnPropertyChanged(nameof(ComparisonToolExecutionRecord));
            OnPropertyChanged(nameof(ToolExecutionDiffSummary));
        }
    }

    public string ToolExecutionDetailsTitle =>
        SelectedToolExecutionRecord is null
            ? "Tool execution details"
            : $"Tool execution: {SelectedToolExecutionRecord.DisplayName} ({SelectedToolExecutionSession?.DisplayName ?? "no session"})";

    public string SelectedToolExecutionInputs => SelectedToolExecutionRecord?.InputsSummary ?? "Select a tool execution record.";

    public string SelectedToolExecutionOutputs => SelectedToolExecutionRecord?.OutputsSummary ?? "Outputs are shown after a record is selected.";

    public string SelectedToolExecutionError => SelectedToolExecutionRecord?.ErrorSummary ?? "Errors are shown after a record is selected.";

    public string ToolExecutionDiffSummary =>
        BuildToolExecutionDiffSummary(SelectedToolExecutionRecord, ComparisonToolExecutionRecord);

    public ReadOnlyCollection<ProviderCapabilityMatrixRow> ProviderCapabilityMatrix => _providerCapabilityMatrix;

    public ReadOnlyCollection<RoleDescriptor> RoleOptions { get; }

    public ProjectWorkspace? ActiveWorkspace
    {
        get => _activeWorkspace;
        private set
        {
            if (Equals(_activeWorkspace, value))
                return;

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

    public RoleDescriptor? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (ReferenceEquals(_selectedRole, value))
                return;

            _selectedRole = value;
            OnPropertyChanged(nameof(SelectedRole));
            OnPropertyChanged(nameof(SelectedRoleDescription));
            _ = RefreshAiHelpAsync();
        }
    }

    public string SelectedRoleDescription => SelectedRole?.Description ?? "No role selected.";

    public ReadOnlyCollection<AiVisibilityMode> AiVisibilityModes { get; }

    public ReadOnlyCollection<AiAccessRole> AiAccessRoles { get; }

    public AiVisibilityMode SelectedAiVisibilityMode
    {
        get => _aiPresentationPolicy.Visibility;
        set
        {
            if (_aiPresentationPolicy.Visibility == value)
                return;

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
            if (_aiAccessRole == value)
                return;

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
            if (_aiPresentationPolicy.AllowAiPanelToggle == value)
                return;

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
            if (_aiPresentationPolicy.AllowCopyExport == value)
                return;

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
            if (_aiPresentationPolicy.EnterpriseMode == value)
                return;

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
        : "AI is running in the background but hidden by policy.";

    public string AiProviderStatus => "Provider: Ollama";

    public string AiExportNotice => IsCopyExportDisabled
        ? "Copy and export are disabled by policy."
        : string.Empty;

    public string AiVisibilityWatermark => "Admin-only visibility";

    public string ActiveRootFsLabel => ActiveRootFs?.DisplayName ?? "No rootfs selected";

    public string ActiveRootFsSource => string.IsNullOrWhiteSpace(ResolvedRootFsSource)
        ? "No source set"
        : ResolvedRootFsSource;

    public string RootFsSourceOverride
    {
        get => GetRootFsSourceOverride() ?? string.Empty;
        set => UpdateRootFsSourceOverride(value);
    }

    public string ActiveRootFsLicense => ActiveRootFs?.License ?? "No license details";

    public string ActiveRootFsNotes => ActiveRootFs?.Notes ?? "No notes";

    public bool HasRootFsCatalog => RootFsCatalog.Count > 0;

    public string RootFsNotice => "Shoots does not provide or modify Linux. Rootfs entries are replaceable.";

    public string RootFsFallbackNotice => "If the source is unavailable, Shoots stays idle and keeps your selection.";

    public ProjectWorkspace? SelectedWorkspace
    {
        get => _activeWorkspace;
        set
        {
            if (value is null || Equals(_activeWorkspace, value))
                return;

            SelectWorkspace(value);
        }
    }

    public bool RestartRequired
    {
        get => _restartRequired;
        private set
        {
            if (_restartRequired == value)
                return;

            _restartRequired = value;
            OnPropertyChanged(nameof(RestartRequired));
        }
    }

    public bool HasSourceControl => _capabilityProvider.HasCapability(EnvironmentCapability.SourceControl);

    public bool HasDatabase => _capabilityProvider.HasCapability(EnvironmentCapability.Database);

    public bool HasArtifactExport => _capabilityProvider.HasCapability(EnvironmentCapability.ArtifactExport);

    public IReadOnlyList<DatabaseIntentOption> DatabaseIntents { get; }

    public DatabaseIntentOption? SelectedDatabaseIntent
    {
        get => _selectedDatabaseIntent;
        set
        {
            if (Equals(_selectedDatabaseIntent, value))
                return;

            _selectedDatabaseIntent = value;
            OnPropertyChanged(nameof(SelectedDatabaseIntent));
            if (ActiveWorkspace is not null && value is not null)
                _databaseIntentStore.SetIntent(ActiveWorkspace.RootPath, value.Intent);
        }
    }

    public string WindowTitle => ActiveWorkspace is null
        ? "Shoots"
        : $"Shoots — {ActiveWorkspace.Name}";

    public string WorkspaceIsolationTooltip => "Workspace selection is UI-only. It never executes scripts or changes runtime determinism.";

    public bool HasNoWorkspaces => _recentWorkspaces.Count == 0;

    public bool HasWorkspaces => _recentWorkspaces.Count > 0;

    public string StartDisabledReason => GetStartDisabledReason();

    public string ApplyEnvironmentDisabledReason => GetApplyEnvironmentDisabledReason();

    public string ApplyScriptDisabledReason => GetApplyScriptDisabledReason();

    public string AiHelpDisabledReason => GetAiHelpDisabledReason();

    public string AiHelpContextSummary { get; private set; } = "AI Help is descriptive only.";

    public string AiHelpStateExplanation { get; private set; } = "No runtime context is available.";

    public string AiHelpNextSteps { get; private set; } = "Select a workspace to view help.";

    public string NewBlueprintName
    {
        get => _newBlueprintName;
        set
        {
            if (_newBlueprintName == value)
                return;

            _newBlueprintName = value;
            OnPropertyChanged(nameof(NewBlueprintName));
            OnBlueprintDraftChanged();
        }
    }

    public string NewBlueprintDescription
    {
        get => _newBlueprintDescription;
        set
        {
            if (_newBlueprintDescription == value)
                return;

            _newBlueprintDescription = value;
            OnPropertyChanged(nameof(NewBlueprintDescription));
            OnBlueprintDraftChanged();
        }
    }

    public string NewBlueprintIntents
    {
        get => _newBlueprintIntents;
        set
        {
            if (_newBlueprintIntents == value)
                return;

            _newBlueprintIntents = value;
            OnPropertyChanged(nameof(NewBlueprintIntents));
            OnBlueprintDraftChanged();
        }
    }

    public string NewBlueprintArtifacts
    {
        get => _newBlueprintArtifacts;
        set
        {
            if (_newBlueprintArtifacts == value)
                return;

            _newBlueprintArtifacts = value;
            OnPropertyChanged(nameof(NewBlueprintArtifacts));
            OnBlueprintDraftChanged();
        }
    }

    public string NewBlueprintVersion
    {
        get => _newBlueprintVersion;
        set
        {
            if (_newBlueprintVersion == value)
                return;

            _newBlueprintVersion = value;
            OnPropertyChanged(nameof(NewBlueprintVersion));
            OnBlueprintDraftChanged();
        }
    }

    public string NewBlueprintDefinition
    {
        get => _newBlueprintDefinition;
        set
        {
            if (_newBlueprintDefinition == value)
                return;

            _newBlueprintDefinition = value;
            OnPropertyChanged(nameof(NewBlueprintDefinition));
            OnBlueprintDraftChanged();
        }
    }

    public IReadOnlyList<ToolpackTier> ToolpackTierOptions { get; } =
        new[] { ToolpackTier.Public, ToolpackTier.Developer };

    public ToolpackTier SelectedToolpackTier
    {
        get => IsSystemTierEnabled ? _lastNonSystemTier : ActiveToolpackTier;
        set => UpdateToolpackTier(value);
    }

    public string ActiveToolpackTierLabel => $"Active Tier: {ActiveToolpackTier}";

    public string ActiveToolpackTierTooltip => ActiveToolpackTier switch
    {
        ToolpackTier.Public => "Public tools only.",
        ToolpackTier.Developer => "Public and developer toolpacks.",
        ToolpackTier.System => "All tiers, including system toolpacks.",
        _ => "Tier status."
    };

    public ToolpackTier ActiveToolpackTier => _activeWorkspace?.AllowedTier ?? ToolpackTier.Public;

    public bool IsSystemTierEnabled => ActiveToolpackTier == ToolpackTier.System;

    public string SystemTierActionLabel => IsSystemTierEnabled
        ? "Disable System Tier"
        : "Enable System Tier";

    public string SystemTierNote => IsSystemTierEnabled
        ? "System tier is enabled for this workspace."
        : "System tier is restricted to prevent privileged tools from appearing by default.";

    public bool CanSelectToolpackTier => HasActiveWorkspace && !IsSystemTierEnabled;

    public bool CanManageBlueprints => HasActiveWorkspace && IsSystemTierEnabled;

    public string BlueprintStatusNote => CanManageBlueprints
        ? "Blueprints are available for this workspace."
        : "Blueprints are available when System tier is enabled.";

    public string BlueprintSaveStatus
    {
        get => _blueprintSaveStatus;
        private set
        {
            if (_blueprintSaveStatus == value)
                return;

            _blueprintSaveStatus = value;
            OnPropertyChanged(nameof(BlueprintSaveStatus));
        }
    }

    public void SetPlan(BuildPlan? plan)
    {
        Plan = plan;
        _ = RefreshAiHelpAsync();
    }

    public void SetState(ExecutionState state) => State = state;

    private bool CanStartNewProject() => _startupFlow.State == StartupFlowState.Initial && !HasActiveWorkspace;

    private bool CanStartAnotherProject() => HasActiveWorkspace;

    private bool CanSelectEntryPath() => _startupFlow.State == StartupFlowState.EntryPathSelection && !_startupComplete;

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
        if (!HasActiveWorkspace)
            return Task.CompletedTask;

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

    private bool CanSubmitStartupInput() => IsStartupInputActive && !_startupComplete && !string.IsNullOrWhiteSpace(StartupInput);

    private Task SubmitStartupInputAsync()
    {
        if (_startupComplete)
        {
            AddStartupMessage("System: Startup is complete. Use \"Start another project\" to re-enter startup.");
            return Task.CompletedTask;
        }

        var input = NormalizeStartupInput(StartupInput);
        if (string.IsNullOrWhiteSpace(input))
            return Task.CompletedTask;

        AddStartupMessage($"You: {input}");
        StartupInput = string.Empty;

        switch (_startupFlow.State)
        {
            case StartupFlowState.StartNewLanguage:
                return HandleStartupLanguageAsync(input);
            case StartupFlowState.StartNewName:
                return HandleStartupProjectNameAsync(input);
            case StartupFlowState.StartNewDescription:
                return HandleStartupDescriptionAsync(input);
            case StartupFlowState.StartNewConfirm:
                return HandleStartupConfirmAsync(input);
            case StartupFlowState.ContinueExistingPath:
                return HandleContinueExistingPathAsync(input);
            case StartupFlowState.ContinueExistingReview:
                return HandleContinueExistingConfirmAsync(input);
            case StartupFlowState.ExploreMode:
                return HandleExploreModeAsync(input);
            default:
                AddStartupMessage($"System: {StartupPrompt}");
                return Task.CompletedTask;
        }
    }

    private bool CanStart() => Plan is not null && State is ExecutionState.Idle or ExecutionState.Completed or ExecutionState.Halted;

    private bool CanCancel() => State is ExecutionState.Running or ExecutionState.Waiting;

    private bool CanApplyEnvironment()
    {
        if (SelectedProfile is null)
            return false;

        if (State is ExecutionState.Running or ExecutionState.Waiting or ExecutionState.Replaying)
            return false;

        if (_lastEnvironmentResult is null)
            return true;

        return !string.Equals(_lastEnvironmentResult.ProfileName, SelectedProfile.Name, StringComparison.Ordinal);
    }

    private bool CanApplyScript()
    {
        if (_environmentScript is null)
            return false;

        if (State is ExecutionState.Running or ExecutionState.Waiting or ExecutionState.Replaying)
            return false;

        if (_lastEnvironmentResult is null)
            return true;

        return !string.Equals(_lastEnvironmentResult.ProfileName, _environmentScript.Name, StringComparison.Ordinal);
    }

    private async Task StartAsync()
    {
        if (Plan is null)
            return;

        var result = await _commandService.StartAsync(Plan).ConfigureAwait(true);
        if (!result.Ok)
            return;

        RecordExecutionSession(result);
    }

    private Task CancelAsync() => _commandService.CancelAsync();

    private async Task RefreshStatusAsync()
    {
        var snapshot = await _commandService.RefreshStatusAsync().ConfigureAwait(true);
        ApplyStatusSnapshot(snapshot);
    }

    private void ApplyStatusSnapshot(IRuntimeStatusSnapshot snapshot)
    {
        _ = snapshot;
    }

    private async Task ApplyEnvironmentAsync()
    {
        if (SelectedProfile is null)
            return;

        EnvironmentErrorMessage = null;
        EnvironmentInfoMessage = null;

        var sandboxRoot = GetSandboxRoot();
        if (!Directory.Exists(sandboxRoot))
        {
            EnvironmentErrorMessage = $"Sandbox root missing: {sandboxRoot}";
            return;
        }

        var previousCapabilities = _capabilityProvider.AvailableCapabilities;
        if (previousCapabilities != SelectedProfile.DeclaredCapabilities)
        {
            if (!_profilePrompt.ConfirmCapabilityChange(previousCapabilities, SelectedProfile.DeclaredCapabilities))
                return;
        }

        try
        {
            var result = _environmentService.ApplyProfile(sandboxRoot, SelectedProfile);
            ApplyEnvironmentResult(result, previousCapabilities);
        }
        catch (Exception ex)
        {
            EnvironmentErrorMessage = ex.Message;
            EnvironmentInfoMessage = null;
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task ApplyScriptAsync()
    {
        if (_environmentScript is null)
            return;

        EnvironmentErrorMessage = null;
        EnvironmentInfoMessage = null;

        var sandboxRoot = GetSandboxRoot();
        if (!Directory.Exists(sandboxRoot))
        {
            EnvironmentErrorMessage = $"Sandbox root missing: {sandboxRoot}";
            return;
        }

        var previousCapabilities = _capabilityProvider.AvailableCapabilities;
        if (previousCapabilities != _environmentScript.DeclaredCapabilities)
        {
            if (!_profilePrompt.ConfirmCapabilityChange(previousCapabilities, _environmentScript.DeclaredCapabilities))
                return;
        }

        try
        {
            var result = _environmentService.ApplyProfile(sandboxRoot, new EnvironmentScriptProfile(_environmentScript));
            ApplyEnvironmentResult(result, previousCapabilities);
        }
        catch (Exception ex)
        {
            EnvironmentErrorMessage = ex.Message;
            EnvironmentInfoMessage = null;
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void ApplyEnvironmentResult(EnvironmentProfileResult result, EnvironmentCapability previousCapabilities)
    {
        _lastEnvironmentResult = result;
        _capabilityProvider.Update(result.DeclaredCapabilities);

        EnvironmentCreatedPaths = result.CreatedPaths.ToList();
        EnvironmentAppliedCapabilities = DescribeCapabilities(result.DeclaredCapabilities);
        EnvironmentAppliedAtUtc = $"{result.AppliedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        EnvironmentAppliedProfileName = result.ProfileName;
        RestartRequired = previousCapabilities != result.DeclaredCapabilities;
        EnvironmentInfoMessage = "Environment applied successfully.";

        OnPropertyChanged(nameof(EnvironmentCreatedPaths));
        OnPropertyChanged(nameof(EnvironmentAppliedCapabilities));
        OnPropertyChanged(nameof(EnvironmentAppliedAtUtc));
        OnPropertyChanged(nameof(EnvironmentAppliedProfileName));
        OnPropertyChanged(nameof(HasSourceControl));
        OnPropertyChanged(nameof(HasDatabase));
        OnPropertyChanged(nameof(HasArtifactExport));

        UpdateProfileCapabilities();
        RaiseCommandCanExecute();
    }

    private void UpdateProfileCapabilities()
    {
        EnvironmentCapabilities = SelectedProfile is null
            ? Array.Empty<string>()
            : DescribeCapabilities(SelectedProfile.DeclaredCapabilities);
        OnPropertyChanged(nameof(EnvironmentCapabilities));
    }

    private static IReadOnlyList<string> DescribeCapabilities(EnvironmentCapability capabilities)
    {
        if (capabilities == EnvironmentCapability.None)
            return new[] { "None" };

        var labels = new List<string>();

        if ((capabilities & EnvironmentCapability.SourceControl) == EnvironmentCapability.SourceControl)
            labels.Add("SourceControl");
        if ((capabilities & EnvironmentCapability.Database) == EnvironmentCapability.Database)
            labels.Add("Database");
        if ((capabilities & EnvironmentCapability.ArtifactExport) == EnvironmentCapability.ArtifactExport)
            labels.Add("ArtifactExport");

        return labels;
    }

	private void LoadEnvironmentScript()
	{
		var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

		var scriptRoot = ActiveWorkspace?.RootPath;
		if (string.IsNullOrWhiteSpace(scriptRoot))
			scriptRoot = baseDirectory;

		_scriptSearchPath = Path.Combine(scriptRoot, EnvironmentScriptLoader.FileName);

		EnvironmentScript? loadedScript = null;
		string? loadError = null;

		var loaded = _scriptLoader.TryLoad(
			scriptRoot,
			out loadedScript,
			out loadError);

		if (loaded && loadedScript is not null)
		{
			_environmentScript = loadedScript;
			EnvironmentInfoMessage = $"Environment script loaded from {_scriptSearchPath}.";
			EnvironmentErrorMessage = null;
			_scriptUnsupportedCapabilitiesMessage =
				DescribeUnsupportedCapabilities(loadedScript.DeclaredCapabilities);
		}
		else if (!string.IsNullOrWhiteSpace(loadError))
		{
			_environmentScript = null;
			EnvironmentErrorMessage = loadError;
			EnvironmentInfoMessage = null;
			_scriptUnsupportedCapabilitiesMessage = null;
		}
		else
		{
			_environmentScript = null;
			EnvironmentInfoMessage = null;
			EnvironmentErrorMessage = null;
			_scriptUnsupportedCapabilitiesMessage = null;
		}

		OnPropertyChanged(nameof(ScriptPreview));
		OnPropertyChanged(nameof(ScriptCapabilities));
		OnPropertyChanged(nameof(ScriptSteps));
		OnPropertyChanged(nameof(ScriptSearchPath));
		OnPropertyChanged(nameof(ScriptUnsupportedCapabilitiesMessage));
		OnPropertyChanged(nameof(ScriptFolderCount));
		OnPropertyChanged(nameof(ScriptFolderCountLabel));
		OnPropertyChanged(nameof(ApplyScriptDisabledReason));

		RaiseCommandCanExecute();
	}

    private static string GetSandboxRoot()
    {
        return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Shoots",
            "Sandbox");
    }

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
        ExplainSelectedToolExecutionCommand.RaiseCanExecuteChanged();
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

    private bool CanRemoveWorkspace()
    {
        return SelectedWorkspace is not null;
    }

    private Task RemoveWorkspaceAsync(object? parameter)
    {
        if (parameter is ProjectWorkspace workspace)
        {
            _workspaceProvider.RemoveWorkspace(workspace);
            RefreshRecentWorkspaces();
            ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
        }

        return Task.CompletedTask;
    }

    private bool CanOpenWorkspace()
    {
        return SelectedWorkspace is not null && !string.IsNullOrWhiteSpace(SelectedWorkspace.RootPath);
    }

    private Task OpenWorkspaceAsync(object? parameter)
    {
        if (parameter is ProjectWorkspace workspace && !string.IsNullOrWhiteSpace(workspace.RootPath))
            _workspaceShell.OpenFolder(workspace.RootPath);

        return Task.CompletedTask;
    }

    private bool CanRefreshAiHelp()
    {
        return HasActiveWorkspace;
    }

    private bool CanToggleSystemTier()
    {
        return HasActiveWorkspace;
    }

    private Task ToggleSystemTierAsync()
    {
        if (!HasActiveWorkspace || _activeWorkspace is null)
            return Task.CompletedTask;

        if (IsSystemTierEnabled)
        {
            UpdateToolpackTier(_lastNonSystemTier);
            return Task.CompletedTask;
        }

        if (!_toolTierPrompt.ConfirmSystemTier(ActiveToolpackTier))
            return Task.CompletedTask;

        UpdateToolpackTier(ToolpackTier.System);
        return Task.CompletedTask;
    }

    private bool CanAddBlueprint()
    {
        return CanManageBlueprints
            && !string.IsNullOrWhiteSpace(NewBlueprintName)
            && !string.IsNullOrWhiteSpace(NewBlueprintVersion)
            && !string.IsNullOrWhiteSpace(NewBlueprintDefinition)
            && !string.IsNullOrWhiteSpace(NewBlueprintIntents)
            && !string.IsNullOrWhiteSpace(NewBlueprintArtifacts);
    }

    private Task AddBlueprintAsync()
    {
        if (!CanAddBlueprint() || _activeWorkspace is null)
            return Task.CompletedTask;

		var blueprint = new SystemBlueprint(
			Name: NewBlueprintName.Trim(),
			Description: NewBlueprintDescription.Trim(),
			Intents: ParseBlueprintLines(NewBlueprintIntents),
			Artifacts: ParseBlueprintLines(NewBlueprintArtifacts),
			Version: NewBlueprintVersion.Trim(),
			Definition: NewBlueprintDefinition.Trim(),
			CreatedUtc: DateTimeOffset.UtcNow);

        var entry = new BlueprintEntryViewModel(blueprint, RequestBlueprintSave);
        _blueprints.Add(entry);
        SaveBlueprints();
        OnPropertyChanged(nameof(HasBlueprints));

        NewBlueprintName = string.Empty;
        NewBlueprintDescription = string.Empty;
        NewBlueprintIntents = string.Empty;
        NewBlueprintArtifacts = string.Empty;
        NewBlueprintVersion = "1.0";
        NewBlueprintDefinition = string.Empty;

        return Task.CompletedTask;
    }

    private bool CanSaveBlueprint()
    {
        return CanManageBlueprints;
    }

    private Task SaveBlueprintAsync(object? parameter)
    {
        if (parameter is not BlueprintEntryViewModel entry)
            return Task.CompletedTask;

        if (!entry.TrySave())
        {
            BlueprintSaveStatus = "Blueprint changes are not saved because validation failed.";
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    private bool CanRevertBlueprint()
    {
        return CanManageBlueprints;
    }

    private Task RevertBlueprintAsync(object? parameter)
    {
        if (parameter is not BlueprintEntryViewModel entry)
            return Task.CompletedTask;

        entry.RevertToLastSaved();
        BlueprintSaveStatus = "Blueprint changes were reverted.";
        return Task.CompletedTask;
    }

    private bool CanExplainBlueprint()
    {
        return CanManageBlueprints;
    }

    private Task ExplainBlueprintAsync(object? parameter)
        => ExplainBlueprintWithIntentAsync(parameter, AiIntentType.Explain);

    private Task ValidateBlueprintAsync(object? parameter)
        => ExplainBlueprintWithIntentAsync(parameter, AiIntentType.Validate);

    private Task SuggestBlueprintAsync(object? parameter)
        => ExplainBlueprintWithIntentAsync(parameter, AiIntentType.Suggest);

    private Task ExplainExecutionAsync()
        => ExplainSurfaceAsync(
            new AiHelpScope("execution", "Execution overview", BuildExecutionScopeData()),
            new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.Execution));

    private bool CanReplayPlan()
        => Plan is not null;

    private Task ReplayPlanAsync()
    {
        if (Plan is null)
            return Task.CompletedTask;

        var records = BuildToolExecutionRecords(Plan, Plan.ToolResult);
        var session = ToolExecutionSessionViewModel.CreateReplay(Plan, records);
        _toolExecutionSessions.Insert(0, session);
        SelectedToolExecutionSession = session;
        ComparisonToolExecutionSession ??= session;
        State = ExecutionState.Replaying;
        return Task.CompletedTask;
    }

    private bool CanExplainToolExecution()
        => SelectedToolExecutionRecord is not null;

    private Task ExplainSelectedToolExecutionAsync()
    {
        if (SelectedToolExecutionRecord is null)
            return Task.CompletedTask;

        var scopeData = new Dictionary<string, string>
        {
            ["tool"] = SelectedToolExecutionRecord.ToolId,
            ["record"] = SelectedToolExecutionRecord.RecordId,
            ["owner"] = SelectedToolExecutionRecord.Owner.ToString()
        };

        return ExplainSurfaceAsync(
            new AiHelpScope(
                SelectedToolExecutionRecord.SurfaceId,
                "Tool execution record",
                scopeData),
            new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.ToolExecution));
    }

    private async Task RefreshAiHelpAsync()
    {
        if (!CanRefreshAiHelp())
        {
            AiHelpContextSummary = "AI Help is descriptive only.";
            AiHelpStateExplanation = "No runtime context is available.";
            AiHelpNextSteps = "Select a workspace to view help.";
            OnPropertyChanged(nameof(AiHelpContextSummary));
            OnPropertyChanged(nameof(AiHelpStateExplanation));
            OnPropertyChanged(nameof(AiHelpNextSteps));
            return;
        }

        var contextRequest = BuildAiHelpRequest(
            new AiHelpScope("workspace", "Workspace overview", BuildWorkspaceScopeData()),
            new AiIntentDescriptor(AiIntentType.Explain, AiIntentScope.UI));
        var stateRequest = BuildAiHelpRequest(
            new AiHelpScope("execution", "Execution state", BuildExecutionScopeData()),
            new AiIntentDescriptor(AiIntentType.Diagnose, AiIntentScope.Execution));
        var nextStepsRequest = BuildAiHelpRequest(
            new AiHelpScope("workspace", "Workspace next steps", BuildWorkspaceScopeData()),
            new AiIntentDescriptor(AiIntentType.Suggest, AiIntentScope.UI));
        try
        {
            AiHelpContextSummary = await _aiHelpFacade.GetContextSummaryAsync(contextRequest).ConfigureAwait(true);
            AiHelpStateExplanation = await _aiHelpFacade.ExplainStateAsync(stateRequest).ConfigureAwait(true);
            AiHelpNextSteps = await _aiHelpFacade.SuggestNextStepsAsync(nextStepsRequest).ConfigureAwait(true);
        }
        catch (Exception)
        {
            AiHelpContextSummary = "AI Help is offline.";
            AiHelpStateExplanation = "No runtime context is available.";
            AiHelpNextSteps = "AI Help is descriptive only.";
        }
        OnPropertyChanged(nameof(AiHelpContextSummary));
        OnPropertyChanged(nameof(AiHelpStateExplanation));
        OnPropertyChanged(nameof(AiHelpNextSteps));
    }

    public void SelectWorkspace(ProjectWorkspace workspace)
    {
        _workspaceProvider.SetActiveWorkspace(workspace);
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
        RefreshRecentWorkspaces();
        _ = RefreshAiHelpAsync();
    }

    private void RefreshRecentWorkspaces()
    {
        _recentWorkspaces.Clear();
        foreach (var workspace in _workspaceProvider.GetRecentWorkspaces())
            _recentWorkspaces.Add(workspace);

        OnPropertyChanged(nameof(HasNoWorkspaces));
        OnPropertyChanged(nameof(HasWorkspaces));
    }

    private AiHelpRequest BuildAiHelpRequest(AiHelpScope scope, AiIntentDescriptor intent)
    {
        var workspace = ActiveWorkspace;
        var allowedCapabilities = workspace?.AllowedCapabilities
            ?? ToolpackPolicyDefaults.GetAllowedCapabilities(ActiveToolpackTier);

        var snapshot = new AiWorkspaceSnapshot(
            workspace?.Name,
            workspace?.RootPath,
            ActiveToolpackTier,
            allowedCapabilities);

        return new AiHelpRequest(
            scope,
            intent,
            snapshot,
            Plan,
            null,
            StateLabel,
            SelectedProfile?.Name,
            _lastEnvironmentResult?.ProfileName,
            SelectedRole,
            BuildAiHelpSurfaces());
    }

	private IReadOnlyList<IAiHelpSurface> BuildAiHelpSurfaces()
	{
		var surfaces = new List<IAiHelpSurface>();

		// Workspace + execution context
		surfaces.Add(new WorkspaceAiHelpSurface(ActiveWorkspace, ActiveToolpackTier));

		surfaces.Add(new ExecutionAiHelpSurface(
			Plan,
			State.ToString(),
			StartDisabledReason));

		// Execution environment (UI-safe RootFs)
		surfaces.Add(new ExecutionEnvironmentAiHelpSurface(
			ActiveRootFs,
			RootFsSourceOverride,
			RootFsFallbackNotice));

		// Blueprint + planning surfaces
		surfaces.Add(new BlueprintCatalogAiHelpSurface(
			_blueprints.Select(entry => entry.Name).ToList()));

		surfaces.Add(new PlannerAiHelpSurface(Plan));

		// Tool execution surfaces
		surfaces.Add(new ToolExecutionCatalogAiHelpSurface(
			_toolExecutionSessions,
			_toolExecutionRecords,
			_comparisonToolExecutionRecords));

		// Individual blueprint surfaces
		foreach (var entry in _blueprints)
			surfaces.Add(entry.ToBlueprint());

		// Tool execution record surfaces
		foreach (var record in _toolExecutionRecords)
			surfaces.Add(record);

		foreach (var record in _comparisonToolExecutionRecords)
			surfaces.Add(record);

		AiSurfaceRegistry.Current.Register(surfaces);
		return surfaces;
	}

    private void RegisterAiSurfaces()
    {
        _ = BuildAiHelpSurfaces();
    }

    private void LoadAiPolicy()
    {
        var settings = _aiPolicyStore.Load(ActiveWorkspace?.RootPath);
        _aiAccessRole = settings.AccessRole;
        _aiPresentationPolicy = settings.Policy;
        UpdateAiVisibilityState();
        OnPropertyChanged(nameof(SelectedAiAccessRole));
        OnPropertyChanged(nameof(SelectedAiVisibilityMode));
        OnPropertyChanged(nameof(AllowAiPanelToggle));
        OnPropertyChanged(nameof(AllowCopyExport));
        OnPropertyChanged(nameof(EnterpriseMode));
        OnPropertyChanged(nameof(CanConfigureAiPolicy));
        OnPropertyChanged(nameof(CanToggleAiVisibility));
        OnPropertyChanged(nameof(IsCopyExportDisabled));
        OnPropertyChanged(nameof(AiExportNotice));
        OnPropertyChanged(nameof(ShowAdminOnlyWatermark));
    }

    private void SaveAiPolicy()
    {
        var settings = new AiPolicySettings(_aiAccessRole, _aiPresentationPolicy);
        _aiPolicyStore.Save(ActiveWorkspace?.RootPath, settings);
    }

    private void UpdateAiVisibilityState()
    {
        _aiPanelVisibilityState = _aiPanelVisibilityService.Evaluate(_aiPresentationPolicy, _aiAccessRole);
        OnPropertyChanged(nameof(CanRenderAiPanel));
        OnPropertyChanged(nameof(CanRenderAiExplainButtons));
        OnPropertyChanged(nameof(CanRenderAiProviderStatus));
        OnPropertyChanged(nameof(IsAiPanelHidden));
        OnPropertyChanged(nameof(AiPanelVisibilityNote));
        OnPropertyChanged(nameof(ShowAdminOnlyWatermark));
    }

    private void LoadBlueprints()
    {
        _blueprints.Clear();

        if (_activeWorkspace is null || string.IsNullOrWhiteSpace(_activeWorkspace.RootPath))
            return;

        foreach (var blueprint in _blueprintStore.LoadForWorkspace(_activeWorkspace.RootPath))
            _blueprints.Add(new BlueprintEntryViewModel(blueprint, RequestBlueprintSave));

        OnPropertyChanged(nameof(HasBlueprints));
        BlueprintSaveStatus = "Blueprint changes are saved.";
    }

    private void SaveBlueprints()
    {
        if (_activeWorkspace is null || string.IsNullOrWhiteSpace(_activeWorkspace.RootPath))
            return;

        if (_blueprints.Any(entry => !entry.IsValid))
        {
            BlueprintSaveStatus = "Blueprint changes are not saved because some entries are invalid.";
            return;
        }

        _blueprintStore.SaveForWorkspace(_activeWorkspace.RootPath, _blueprints.Select(entry => entry.ToBlueprint()));
        BlueprintSaveStatus = "Blueprint changes are saved.";
    }

    private void RequestBlueprintSave(BlueprintEntryViewModel entry)
    {
        _ = entry;
        SaveBlueprints();
    }

    private void RefreshToolExecutionRecords()
    {
        if (Plan is null)
        {
            _toolExecutionSessions.Clear();
            SelectedToolExecutionSession = null;
            ComparisonToolExecutionSession = null;
            RefreshToolExecutionRecordsForSession();
            return;
        }

        for (var i = _toolExecutionSessions.Count - 1; i >= 0; i--)
        {
            var session = _toolExecutionSessions[i];
            if (session.IsPreview && string.Equals(session.PlanId, Plan.PlanId, StringComparison.Ordinal))
                _toolExecutionSessions.RemoveAt(i);
        }

        var previewRecords = BuildToolExecutionRecords(Plan, Plan.ToolResult);
        var previewSession = ToolExecutionSessionViewModel.CreatePreview(Plan, previewRecords);
        _toolExecutionSessions.Insert(0, previewSession);
        SelectedToolExecutionSession = previewSession;
        ComparisonToolExecutionSession ??= previewSession;
    }

    private void RefreshToolExecutionRecordsForSession()
    {
        _toolExecutionRecords.Clear();

        if (SelectedToolExecutionSession is not null)
        {
            foreach (var record in SelectedToolExecutionSession.Records)
                _toolExecutionRecords.Add(record);
        }

        SelectedToolExecutionRecord = _toolExecutionRecords.FirstOrDefault();
        OnPropertyChanged(nameof(HasToolExecutionRecords));
        OnPropertyChanged(nameof(ToolExecutionDiffSummary));
    }

    private void RefreshComparisonToolExecutionRecords()
    {
        _comparisonToolExecutionRecords.Clear();

        if (ComparisonToolExecutionSession is not null)
        {
            foreach (var record in ComparisonToolExecutionSession.Records)
                _comparisonToolExecutionRecords.Add(record);
        }

        ComparisonToolExecutionRecord = _comparisonToolExecutionRecords.FirstOrDefault();
        OnPropertyChanged(nameof(ToolExecutionDiffSummary));
    }

    private void LoadExecutionEnvironments()
    {
        _executionSettings = _executionEnvironmentStore.Load();
        _rootFsCatalog.Clear();
        foreach (var entry in _executionSettings.RootFsCatalog)
            _rootFsCatalog.Add(entry);

        UpdateExecutionEnvironmentSelection();
    }

    private void UpdateExecutionEnvironmentSelection()
    {
        OnPropertyChanged(nameof(SelectedRootFs));
        OnPropertyChanged(nameof(ActiveRootFs));
        OnPropertyChanged(nameof(ActiveRootFsLabel));
        OnPropertyChanged(nameof(ActiveRootFsSource));
        OnPropertyChanged(nameof(RootFsSourceOverride));
        OnPropertyChanged(nameof(ActiveRootFsLicense));
        OnPropertyChanged(nameof(ActiveRootFsNotes));
        OnPropertyChanged(nameof(ResolvedExecutionEnvironmentSummary));
        OnPropertyChanged(nameof(HasRootFsCatalog));
        OnPropertyChanged(nameof(RootFsNotice));
        OnPropertyChanged(nameof(RootFsFallbackNotice));
    }

	public UiRootFsDescriptor? ActiveRootFs
	{
		get
		{
			var id = ActiveWorkspace?.RootFsId ?? _executionSettings.ActiveRootFsId;
			return GetRootFsById(id);
		}
	}

	public UiRootFsDescriptor? SelectedRootFs
	{
		get => ActiveRootFs;
		set => UpdateRootFsSelection(value);
	}


	private void UpdateRootFsSelection(UiRootFsDescriptor? descriptor)
	{
		if (descriptor is null)
			return;

		if (ActiveWorkspace is null)
		{
			_executionSettings = _executionSettings with { ActiveRootFsId = descriptor.Id };
			_executionEnvironmentStore.Save(_executionSettings);
			UpdateExecutionEnvironmentSelection();
			return;
		}

		var updated = ActiveWorkspace with { RootFsId = descriptor.Id };
		_workspaceProvider.UpdateWorkspace(updated);
		ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
	}

    private void UpdateRootFsSourceOverride(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (ActiveWorkspace is null)
        {
            if (string.Equals(_executionSettings.RootFsSourceOverride ?? string.Empty, normalized, StringComparison.Ordinal))
                return;

            _executionSettings = _executionSettings with { RootFsSourceOverride = normalized };
            _executionEnvironmentStore.Save(_executionSettings);
            UpdateExecutionEnvironmentSelection();
            return;
        }

        if (string.Equals(ActiveWorkspace.RootFsSourceOverride ?? string.Empty, normalized, StringComparison.Ordinal))
            return;

        var updated = ActiveWorkspace with { RootFsSourceOverride = normalized };
        _workspaceProvider.UpdateWorkspace(updated);
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
    }

    private string? GetRootFsSourceOverride()
    {
        return ActiveWorkspace?.RootFsSourceOverride ?? _executionSettings.RootFsSourceOverride;
    }

    private string? ResolvedRootFsSource
    {
        get
        {
            var overrideValue = GetRootFsSourceOverride();
            if (!string.IsNullOrWhiteSpace(overrideValue))
                return overrideValue;

            return ActiveRootFs?.DefaultUrl;
        }
    }

	private UiRootFsDescriptor? GetRootFsById(string? id)
	{
		if (string.IsNullOrWhiteSpace(id))
			return null;

		return _rootFsCatalog.FirstOrDefault(entry =>
			string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
	}

    private static IReadOnlyList<string> ParseBlueprintLines(string value)
    {
        return value
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private void UpdateToolpackTier(ToolpackTier tier)
    {
        if (_activeWorkspace is null)
            return;

        if (tier != ToolpackTier.System)
            _lastNonSystemTier = tier;

        if (_activeWorkspace.AllowedTier == tier && _activeWorkspace.AllowedCapabilities is not null)
            return;

        var capabilities = ToolpackPolicyDefaults.GetAllowedCapabilities(tier);
        var updated = _activeWorkspace with
        {
            AllowedTier = tier,
            AllowedCapabilities = capabilities
        };
        _workspaceProvider.UpdateWorkspace(updated);
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
    }

    private void OnToolpackTierChanged()
    {
        if (_activeWorkspace is null)
            _lastNonSystemTier = ToolpackTier.Public;
        else if (_activeWorkspace.AllowedTier != ToolpackTier.System)
            _lastNonSystemTier = _activeWorkspace.AllowedTier;

        OnPropertyChanged(nameof(ActiveToolpackTier));
        OnPropertyChanged(nameof(ActiveToolpackTierLabel));
        OnPropertyChanged(nameof(ActiveToolpackTierTooltip));
        OnPropertyChanged(nameof(IsSystemTierEnabled));
        OnPropertyChanged(nameof(SelectedToolpackTier));
        OnPropertyChanged(nameof(CanSelectToolpackTier));
        OnPropertyChanged(nameof(SystemTierActionLabel));
        OnPropertyChanged(nameof(SystemTierNote));
        OnPropertyChanged(nameof(CanManageBlueprints));
        OnPropertyChanged(nameof(BlueprintStatusNote));
        RaiseCommandCanExecute();
    }

    private void UpdateDatabaseIntentSelection()
    {
        if (ActiveWorkspace is null || string.IsNullOrWhiteSpace(ActiveWorkspace.RootPath))
        {
            SelectedDatabaseIntent = DatabaseIntents.LastOrDefault(option => option.Intent == DatabaseIntent.Undecided)
				?? DatabaseIntents.FirstOrDefault();
			return;
		}

        var intent = _databaseIntentStore.GetIntent(ActiveWorkspace.RootPath);

        SelectedDatabaseIntent = DatabaseIntents.FirstOrDefault(option => option.Intent == intent)
            ?? DatabaseIntents.LastOrDefault(option => option.Intent == DatabaseIntent.Undecided)
            ?? DatabaseIntents.FirstOrDefault();
    }

    private static string BuildToolExecutionDiffSummary(
        ToolExecutionRecordViewModel? left,
        ToolExecutionRecordViewModel? right)
    {
        if (left is null || right is null)
            return "Select two tool execution records to compare.";

        if (ReferenceEquals(left, right))
            return "Select two different tool execution records to compare.";

        var inputDiff = left.FormatDiff(left.Inputs, right.Inputs, "Inputs");
        var outputDiff = left.FormatDiff(left.Outputs, right.Outputs, "Outputs");
        var statusDiff = left.FormatStatusDiff(right);

        return string.Join(" ", new[] { inputDiff, outputDiff, statusDiff }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IReadOnlyList<ExecutionStepSummaryViewModel> BuildExecutionStepSummaries(BuildPlan? plan)
    {
        if (plan is null)
            return Array.Empty<ExecutionStepSummaryViewModel>();

        return plan.Steps
            .Select(step => ExecutionStepSummaryViewModel.FromStep(step))
            .ToList();
    }

    private string BuildExecutionBlockerSummary()
    {
        if (Plan is null)
            return "No execution plan is loaded.";

        var notes = new List<string>
        {
            $"Provider: {Plan.Authority.ProviderId.Value} ({Plan.Authority.Kind})."
        };

        if (State is ExecutionState.Running or ExecutionState.Waiting or ExecutionState.Replaying)
            notes.Add("Execution is already in progress.");

        if (!HasActiveWorkspace)
            notes.Add("Workspace is not selected.");

        if (!Plan.Authority.AllowsDelegation)
            notes.Add("Policy: delegation is not allowed for this plan.");

        if (notes.Count == 1)
            notes.Add("No execution blockers are detected.");

        return string.Join(" ", notes);
    }

    private string BuildExecutionEnvironmentSummary()
    {
        if (ActiveRootFs is null)
            return "Resolved environment: none selected.";

        var source = ResolvedRootFsSource ?? "no source";
        return $"Resolved environment: {ActiveRootFs.DisplayName} ({ActiveRootFs.SourceType}, {source}).";
    }

    private Dictionary<string, string> BuildWorkspaceScopeData()
    {
        var data = new Dictionary<string, string>
        {
            ["tier"] = ActiveToolpackTier.ToString()
        };

        if (ActiveWorkspace is not null)
        {
            data["workspace"] = ActiveWorkspace.Name;
            data["path"] = ActiveWorkspace.RootPath;
        }

        return data;
    }

    private Dictionary<string, string> BuildExecutionScopeData()
    {
        var data = new Dictionary<string, string>
        {
            ["state"] = StateLabel
        };

        if (Plan is not null)
        {
            data["plan"] = Plan.PlanId;
            data["provider"] = Plan.Authority.ProviderId.Value;
        }

        return data;
    }

    private Task ExplainBlueprintWithIntentAsync(object? parameter, AiIntentType intentType)
    {
        if (parameter is not BlueprintEntryViewModel entry)
            return Task.CompletedTask;

        var scopeData = new Dictionary<string, string>
        {
            ["name"] = entry.Name,
            ["version"] = entry.Version
        };

        return ExplainSurfaceAsync(
            new AiHelpScope(entry.ToBlueprint().SurfaceId, "Blueprint entry", scopeData),
            new AiIntentDescriptor(intentType, AiIntentScope.Blueprint, entry.Name));
    }

    private async Task ExplainSurfaceAsync(AiHelpScope scope, AiIntentDescriptor intent)
    {
        if (!CanRefreshAiHelp())
        {
            AiHelpContextSummary = "AI Help is descriptive only.";
            AiHelpStateExplanation = "No runtime context is available.";
            AiHelpNextSteps = "Select a workspace to view help.";
            OnPropertyChanged(nameof(AiHelpContextSummary));
            OnPropertyChanged(nameof(AiHelpStateExplanation));
            OnPropertyChanged(nameof(AiHelpNextSteps));
            return;
        }

        var request = BuildAiHelpRequest(scope, intent);
        try
        {
            AiHelpContextSummary = await _aiHelpFacade.GetContextSummaryAsync(request).ConfigureAwait(true);
            AiHelpStateExplanation = await _aiHelpFacade.ExplainStateAsync(request).ConfigureAwait(true);
            AiHelpNextSteps = await _aiHelpFacade.SuggestNextStepsAsync(request).ConfigureAwait(true);
        }
        catch (Exception)
        {
            AiHelpContextSummary = "AI Help is offline.";
            AiHelpStateExplanation = "No runtime context is available.";
            AiHelpNextSteps = "AI Help is descriptive only.";
        }

        OnPropertyChanged(nameof(AiHelpContextSummary));
        OnPropertyChanged(nameof(AiHelpStateExplanation));
        OnPropertyChanged(nameof(AiHelpNextSteps));
    }

    private void RecordExecutionSession(RuntimeResult result)
    {
        if (Plan is null)
            return;

        if (result.Output is ExecutionEnvelopeDto envelope)
        {
            var records = BuildToolExecutionRecords(Plan, envelope.ToolResults);
            var session = ToolExecutionSessionViewModel.CreateRun(Plan, DateTimeOffset.UtcNow, records);
            _toolExecutionSessions.Insert(0, session);
            SelectedToolExecutionSession = session;
            ComparisonToolExecutionSession ??= session;
            return;
        }

        if (result.Output is ExecutionEnvelope liveEnvelope)
        {
            var records = BuildToolExecutionRecords(liveEnvelope.Plan, liveEnvelope.ToolResults);
            var session = ToolExecutionSessionViewModel.CreateRun(liveEnvelope.Plan, DateTimeOffset.UtcNow, records);
            _toolExecutionSessions.Insert(0, session);
            SelectedToolExecutionSession = session;
            ComparisonToolExecutionSession ??= session;
            return;
        }

        var fallbackRecords = BuildToolExecutionRecords(Plan, Plan.ToolResult);
        var fallbackSession = ToolExecutionSessionViewModel.CreateRun(Plan, DateTimeOffset.UtcNow, fallbackRecords);
        _toolExecutionSessions.Insert(0, fallbackSession);
        SelectedToolExecutionSession = fallbackSession;
        ComparisonToolExecutionSession ??= fallbackSession;
    }

    private IReadOnlyList<ToolExecutionRecordViewModel> BuildToolExecutionRecords(
        BuildPlan plan,
        ToolResult? toolResult)
    {
        var toolSteps = plan.Steps.OfType<ToolBuildStep>().ToList();
        return toolSteps
            .Select(step =>
            {
                var owner = FindDecisionOwner(plan, step.ToolId);
                return ToolExecutionRecordViewModel.FromPlanStep(step, toolResult, owner);
            })
            .ToList();
    }

    private IReadOnlyList<ToolExecutionRecordViewModel> BuildToolExecutionRecords(
        BuildPlan plan,
        IReadOnlyList<ToolResult> results)
    {
        var toolSteps = plan.Steps.OfType<ToolBuildStep>().ToList();
        var resultMap = results
            .GroupBy(result => result.ToolId.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return toolSteps
            .Select(step =>
            {
                resultMap.TryGetValue(step.ToolId.Value, out var result);
                var owner = FindDecisionOwner(plan, step.ToolId);
                var outputs = result?.Outputs ?? new Dictionary<string, object?>();
                var success = result?.Success;
                var error = success == false ? "Recorded output indicates a failure." : null;

                return new ToolExecutionRecordViewModel(
                    step.Id,
                    step.ToolId.Value,
                    step.InputBindings,
                    outputs,
                    success,
                    error,
                    owner,
                    DateTimeOffset.UtcNow);
            })
            .ToList();
    }

    private IReadOnlyList<ToolExecutionRecordViewModel> BuildToolExecutionRecords(
        BuildPlan plan,
        IReadOnlyList<ToolResultDto> results)
    {
        var toolSteps = plan.Steps.OfType<ToolBuildStep>().ToList();
        var resultMap = results
            .GroupBy(result => result.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return toolSteps
            .Select(step =>
            {
                resultMap.TryGetValue(step.ToolId.Value, out var result);
                var owner = FindDecisionOwner(plan, step.ToolId);
                var outputs = result?.Outputs ?? new Dictionary<string, object?>();
                var success = result?.Success;
                var error = success == false ? "Recorded output indicates a failure." : null;

                return new ToolExecutionRecordViewModel(
                    step.Id,
                    step.ToolId.Value,
                    step.InputBindings,
                    outputs,
                    success,
                    error,
                    owner,
                    DateTimeOffset.UtcNow);
            })
            .ToList();
    }

    private static DecisionOwner FindDecisionOwner(BuildPlan plan, ToolId toolId)
    {
        var routeStep = plan.Steps
            .OfType<RouteStep>()
            .FirstOrDefault(step => step.ToolInvocation?.ToolId == toolId);

        return routeStep?.Owner ?? DecisionOwner.Runtime;
    }

    private string? DescribeUnsupportedCapabilities(EnvironmentCapability declared)
    {
        var supported = EnvironmentCapability.SourceControl | EnvironmentCapability.Database | EnvironmentCapability.ArtifactExport;
        var unsupported = declared & ~supported;
        if (unsupported == EnvironmentCapability.None)
            return null;

        return $"Script declares capabilities not supported by this UI: {unsupported}.";
    }

    private string GetStartDisabledReason()
    {
        if (Plan is null)
            return "No execution plan is loaded.";

        if (State is ExecutionState.Running or ExecutionState.Waiting or ExecutionState.Replaying)
            return "Execution is already in progress.";

        return "Ready to start execution.";
    }

    private string GetApplyEnvironmentDisabledReason()
    {
        if (SelectedProfile is null)
            return "Select an environment profile to apply.";

        if (State is ExecutionState.Running or ExecutionState.Waiting or ExecutionState.Replaying)
            return "Stop execution before applying an environment.";

        if (_lastEnvironmentResult is not null &&
            string.Equals(_lastEnvironmentResult.ProfileName, SelectedProfile.Name, StringComparison.Ordinal))
            return "This environment profile is already applied.";

        return "Apply the selected environment profile.";
    }

    private string GetApplyScriptDisabledReason()
    {
        if (_environmentScript is null)
            return "No script preview is loaded.";

        if (State is ExecutionState.Running or ExecutionState.Waiting or ExecutionState.Replaying)
            return "Stop execution before applying a script.";

        if (_lastEnvironmentResult is not null &&
            string.Equals(_lastEnvironmentResult.ProfileName, _environmentScript.Name, StringComparison.Ordinal))
            return "This script is already applied.";

        return "Apply the previewed script.";
    }

    private string GetAiHelpDisabledReason()
    {
        if (ActiveWorkspace is null)
            return "Select a workspace to view AI Help.";

        return "Refresh AI Help.";
    }

    private Task HandleStartupLanguageAsync(string input)
    {
        var match = StartupLanguageRegistry.All
            .Select(option => option.Name)
            .FirstOrDefault(option => string.Equals(option, input, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(match))
        {
            AddStartupMessage($"System: Unsupported language. Choose one of: {string.Join(", ", StartupLanguageOptions)}. State remains: {_startupFlow.State}.");
            return Task.CompletedTask;
        }

        var previous = _startupFlow.State;
        if (!_startupFlow.TrySetLanguage(match, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "StartNew -> LanguageSelected.");
        AddStartupMessage($"Intent: Language set to {match}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupProjectNameAsync(string input)
    {
        var normalized = input.Trim();
        var name = string.Equals(normalized, "skip", StringComparison.OrdinalIgnoreCase) || normalized.Length == 0
            ? null
            : normalized;

        var previous = _startupFlow.State;
        if (!_startupFlow.TrySetProjectName(name, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "StartNew -> NameCaptured.");
        AddStartupMessage(name is null ? "Intent: Project name skipped." : $"Intent: Project name set to {name}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupDescriptionAsync(string input)
    {
        var normalized = NormalizeDescription(input);
        var sentenceCount = CountSentences(normalized);
        if (sentenceCount == 0 || sentenceCount > 2)
        {
            AddStartupMessage($"System: Description must be 1–2 sentences. State remains: {_startupFlow.State}.");
            return Task.CompletedTask;
        }

        var previous = _startupFlow.State;
        if (!_startupFlow.TrySetDescription(normalized, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "StartNew -> DescriptionCaptured.");
        AddStartupMessage($"Intent: Description set to \"{normalized}\".");
        AddStartupMessage(BuildCreateSummary());
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleStartupConfirmAsync(string input)
    {
        if (!string.Equals(input, "confirm", StringComparison.OrdinalIgnoreCase))
        {
            AddStartupMessage($"System: Type \"confirm\" to create the project. State remains: {_startupFlow.State}.");
            return Task.CompletedTask;
        }

        var previous = _startupFlow.State;
        if (!TryCreateProjectFromStartup(out var creationResult))
        {
            AddStartupMessage(creationResult);
            return Task.CompletedTask;
        }

        AddStartupMessage(creationResult);
        if (_startupFlow.TryConfirmCreate(out _))
            LogStartupTransition(previous, _startupFlow.State, "StartNew -> Created.");

        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleContinueExistingPathAsync(string input)
    {
        if (!Directory.Exists(input))
        {
            AddStartupMessage($"System: Path does not exist. Provide an existing folder path. State remains: {_startupFlow.State}.");
            return Task.CompletedTask;
        }

        var sandboxRoot = GetSandboxRoot();
        var fullSandbox = Path.GetFullPath(sandboxRoot);
        var fullPath = Path.GetFullPath(input);
        if (!fullPath.StartsWith(fullSandbox, StringComparison.OrdinalIgnoreCase))
            AddStartupMessage("Warning: Selected path is outside the sandbox.");

        var previous = _startupFlow.State;
        if (!_startupFlow.TrySetExistingProjectPath(input, out var error))
        {
            AddStartupMessage($"System: {error}");
            return Task.CompletedTask;
        }

        LogStartupTransition(previous, _startupFlow.State, "ContinueExisting -> PathCaptured.");
        AddStartupMessage($"Intent: Attach to {input}.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private Task HandleContinueExistingConfirmAsync(string input)
    {
        if (!string.Equals(input, "confirm", StringComparison.OrdinalIgnoreCase))
        {
            AddStartupMessage($"System: Type \"confirm\" to attach read-only. State remains: {_startupFlow.State}.");
            return Task.CompletedTask;
        }

        var path = _startupFlow.ExistingProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            AddStartupMessage("System: No path is available to attach.");
            return Task.CompletedTask;
        }

        AddStartupMessage($"System: Read-only attach requested for {path}. No files were modified.");
        return Task.CompletedTask;
    }

    private Task HandleExploreModeAsync(string input)
    {
        if (!string.Equals(input, "promote", StringComparison.OrdinalIgnoreCase))
        {
            AddStartupMessage($"System: Explore mode active. Type \"promote\" to start a project. State remains: {_startupFlow.State}.");
            return Task.CompletedTask;
        }

        _startupFlow.Reset();
        _startupFlow.TryBeginNewProject(out _);
        AddStartupMessage("System: Promotion requested. Restarting startup flow.");
        AddStartupMessage($"System: {StartupPrompt}");
        NotifyStartupFlowChanged();
        return Task.CompletedTask;
    }

    private static string NormalizeStartupInput(string input) =>
        input.Trim();

    private static string NormalizeDescription(string input) =>
        string.Join(" ", input.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

    private static int CountSentences(string input)
    {
        var count = 0;
        var buffer = new StringBuilder();
        foreach (var ch in input)
        {
            buffer.Append(ch);
            if (ch is '.' or '!' or '?')
            {
                if (buffer.ToString().Trim().Length > 1)
                    count++;
                buffer.Clear();
            }
        }

        if (buffer.ToString().Trim().Length > 0)
            count++;

        return count;
    }

    private string BuildCreateSummary()
    {
        var language = _startupFlow.SelectedLanguage ?? "Unknown";
        var folderName = BuildProjectFolderName(language, _startupFlow.ProjectName, _startupFlow.Description ?? string.Empty);
        var projectName = _startupFlow.ProjectName ?? folderName;
        var sandboxRoot = GetSandboxRoot();
        var projectPath = Path.Combine(sandboxRoot, folderName);
        var structure = BuildStructureSummary(language);
        var builder = new StringBuilder();
        builder.AppendLine("Summary:");
        builder.AppendLine("Provider: Ollama (default)");
        builder.AppendLine($"Mode: {GetSessionMode()}");
        builder.AppendLine($"Language: {language}");
        builder.AppendLine($"Project name: {projectName}");
        builder.AppendLine($"Sandbox path: {projectPath}");
        builder.AppendLine("Files:");
        foreach (var entry in structure.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            builder.AppendLine($"- {entry}");
        return builder.ToString().TrimEnd();
    }

    private string BuildStructureSummary(string language)
    {
        var definition = StartupLanguageRegistry.Find(language);
        if (definition is null)
            return "No structure defined.";

        var items = definition.Structure.Select(item => item.RelativePath).ToList();
        items.Add("README.intent.md");
        return string.Join(", ", items);
    }

    private static string BuildProjectFolderName(string language, string? projectName, string description)
    {
        if (!string.IsNullOrWhiteSpace(projectName))
            return Slugify(projectName);

        var raw = $"{language}|{description}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var suffix = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return $"idea-{suffix}";
    }

    private static string Slugify(string name)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else if (ch is ' ' or '-' or '_')
                builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }

    private bool TryCreateProjectFromStartup(out string message)
    {
        var language = _startupFlow.SelectedLanguage;
        var description = _startupFlow.Description;
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(description))
        {
            message = "System: Startup details are incomplete. Project creation aborted.";
            return false;
        }

        var definition = StartupLanguageRegistry.Find(language);
        if (definition is null)
        {
            message = $"System: No base structure defined for {language}.";
            return false;
        }

        var folderName = BuildProjectFolderName(language, _startupFlow.ProjectName, description);
        var sandboxRoot = GetSandboxRoot();
        if (!Directory.Exists(sandboxRoot))
        {
            message = $"System: Sandbox root missing: {sandboxRoot}";
            return false;
        }

        var projectRoot = Path.Combine(sandboxRoot, folderName);
        var fullSandbox = Path.GetFullPath(sandboxRoot);
        var fullProject = Path.GetFullPath(projectRoot);
        if (!fullProject.StartsWith(fullSandbox, StringComparison.OrdinalIgnoreCase))
        {
            message = "System: Project path escapes the sandbox.";
            return false;
        }

        if (Directory.Exists(fullProject))
        {
            message = "System: Project folder already exists.";
            return false;
        }

        Directory.CreateDirectory(fullProject);
        foreach (var item in definition.Structure)
        {
            var target = Path.Combine(fullProject, item.RelativePath);
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(target, item.Content ?? string.Empty);
        }

        var intentPath = Path.Combine(fullProject, "README.intent.md");
        File.WriteAllText(intentPath, BuildIntentContent(language, _startupFlow.ProjectName, description));

        var workspace = new ProjectWorkspace(
            _startupFlow.ProjectName ?? folderName,
            fullProject,
            DateTimeOffset.UtcNow);
        _workspaceProvider.SetActiveWorkspace(workspace);
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
        RefreshRecentWorkspaces();
        message = $"System: Project created at {fullProject}. README.intent.md written.";
        return true;
    }

    private static string BuildIntentContent(string language, string? projectName, string description)
    {
        var nameLine = string.IsNullOrWhiteSpace(projectName) ? "Name: (none)" : $"Name: {projectName}";
        return $"# Project Intent{System.Environment.NewLine}{System.Environment.NewLine}" +
               $"Language: {language}{System.Environment.NewLine}" +
               $"{nameLine}{System.Environment.NewLine}" +
               $"Description: {description}{System.Environment.NewLine}";
    }

    private StartupSessionMode GetSessionMode()
    {
        if (HasActiveWorkspace)
            return StartupSessionMode.Project;

        if (_startupFlow.EntryPath == StartupEntryPath.ExploreIdea)
            return StartupSessionMode.Explore;

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
        if (next == _sessionMode)
            return;

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
        OnPropertyChanged(nameof(IsStartNewLanguageActive));
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

    public ISourceControlIntent? SourceControlIntent => null;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
