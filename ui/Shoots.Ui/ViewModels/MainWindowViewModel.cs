using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.Blueprints;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Environment;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
using Shoots.UI.Roles;
using Shoots.UI.Services;

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
    private readonly IAiHelpFacade _aiHelpFacade;
    private readonly ObservableCollection<ProjectWorkspace> _recentWorkspaces;
    private readonly ObservableCollection<SystemBlueprint> _blueprints;
    private readonly ObservableCollection<RootFsDescriptor> _rootFsCatalog;
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
    private ExecutionEnvironmentSettings _executionSettings = new("none", Array.Empty<RootFsDescriptor>(), string.Empty);

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
        _aiHelpFacade = aiHelpFacade ?? throw new ArgumentNullException(nameof(aiHelpFacade));
        _state = ExecutionState.Idle;

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
        Profiles = new ReadOnlyCollection<IEnvironmentProfile>(_environmentService.Profiles.ToList());
        SelectedProfile = Profiles.FirstOrDefault();
        EnvironmentCapabilities = Array.Empty<string>();
        EnvironmentCreatedPaths = Array.Empty<string>();
        EnvironmentAppliedCapabilities = Array.Empty<string>();

        _recentWorkspaces = new ObservableCollection<ProjectWorkspace>();
        RecentWorkspaces = new ReadOnlyObservableCollection<ProjectWorkspace>(_recentWorkspaces);
        _blueprints = new ObservableCollection<SystemBlueprint>();
        Blueprints = new ReadOnlyObservableCollection<SystemBlueprint>(_blueprints);
        _rootFsCatalog = new ObservableCollection<RootFsDescriptor>();
        RootFsCatalog = new ReadOnlyObservableCollection<RootFsDescriptor>(_rootFsCatalog);
        RefreshRecentWorkspaces();
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
        LoadExecutionEnvironments();
        RoleOptions = new ReadOnlyCollection<RoleDescriptor>(RoleCatalog.GetDefaultRoles().ToList());
        SelectedRole = RoleOptions.FirstOrDefault();
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
            RaiseCommandCanExecute();
        }
    }

    public string StateLabel => State.ToString();

    public bool IsReplayMode => State == ExecutionState.Replaying;

    public BuildPlan? Plan
    {
        get => _plan;
        private set
        {
            if (Equals(_plan, value))
                return;

            _plan = value;
            OnPropertyChanged(nameof(Plan));
            RaiseCommandCanExecute();
        }
    }

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

    public ReadOnlyObservableCollection<SystemBlueprint> Blueprints { get; }

    public ReadOnlyObservableCollection<RootFsDescriptor> RootFsCatalog { get; }

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
            OnPropertyChanged(nameof(SelectedWorkspace));
            OnPropertyChanged(nameof(WindowTitle));
            LoadEnvironmentScript();
            UpdateProfileCapabilities();
            UpdateDatabaseIntentSelection();
            UpdateExecutionEnvironmentSelection();
            LoadBlueprints();
            OnToolpackTierChanged();
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

    public RootFsDescriptor? SelectedRootFs
    {
        get => ActiveRootFs;
        set => UpdateRootFsSelection(value);
    }

    public RootFsDescriptor? ActiveRootFs
    {
        get
        {
            if (ActiveWorkspace is null)
                return GetRootFsById(_executionSettings.ActiveRootFsId);

            return GetRootFsById(ActiveWorkspace.RootFsId ?? _executionSettings.ActiveRootFsId);
        }
    }

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
            RaiseCommandCanExecute();
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

    public void SetPlan(BuildPlan? plan)
    {
        Plan = plan;
        _ = RefreshAiHelpAsync();
    }

    public void SetState(ExecutionState state) => State = state;

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

    private Task StartAsync()
    {
        if (Plan is null)
            return Task.CompletedTask;

        return _commandService.StartAsync(Plan);
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
        var scriptRoot = ActiveWorkspace is null
            ? baseDirectory
            : ActiveWorkspace.RootPath ?? string.Empty;
        _scriptSearchPath = Path.Combine(scriptRoot, EnvironmentScriptLoader.FileName);

        if (_scriptLoader.TryLoad(scriptRoot, out var script, out var error))
        {
            _environmentScript = script;
            EnvironmentInfoMessage = $"Environment script loaded from {_scriptSearchPath}.";
            EnvironmentErrorMessage = null;
            _scriptUnsupportedCapabilitiesMessage = DescribeUnsupportedCapabilities(script.DeclaredCapabilities);
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            _environmentScript = null;
            EnvironmentErrorMessage = error;
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
        OnPropertyChanged(nameof(StartDisabledReason));
        OnPropertyChanged(nameof(ApplyEnvironmentDisabledReason));
        OnPropertyChanged(nameof(ApplyScriptDisabledReason));
        OnPropertyChanged(nameof(AiHelpDisabledReason));
        OnPropertyChanged(nameof(SystemTierActionLabel));
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
        return HasActiveWorkspace && Plan is not null;
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
        return CanManageBlueprints && !string.IsNullOrWhiteSpace(NewBlueprintName);
    }

    private Task AddBlueprintAsync()
    {
        if (!CanAddBlueprint() || _activeWorkspace is null)
            return Task.CompletedTask;

        var blueprint = new SystemBlueprint(
            NewBlueprintName.Trim(),
            NewBlueprintDescription.Trim(),
            ParseBlueprintLines(NewBlueprintIntents),
            ParseBlueprintLines(NewBlueprintArtifacts),
            DateTimeOffset.UtcNow);

        _blueprints.Add(blueprint);
        SaveBlueprints();

        NewBlueprintName = string.Empty;
        NewBlueprintDescription = string.Empty;
        NewBlueprintIntents = string.Empty;
        NewBlueprintArtifacts = string.Empty;

        return Task.CompletedTask;
    }

    private async Task RefreshAiHelpAsync()
    {
        if (!CanRefreshAiHelp())
        {
            AiHelpContextSummary = "AI Help is descriptive only.";
            AiHelpStateExplanation = "No runtime context is available.";
            AiHelpNextSteps = "Select a workspace and load a plan to view help.";
            OnPropertyChanged(nameof(AiHelpContextSummary));
            OnPropertyChanged(nameof(AiHelpStateExplanation));
            OnPropertyChanged(nameof(AiHelpNextSteps));
            return;
        }

        var request = BuildAiHelpRequest();
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

    private AiHelpRequest BuildAiHelpRequest()
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
            snapshot,
            Plan,
            null,
            StateLabel,
            SelectedProfile?.Name,
            _lastEnvironmentResult?.ProfileName,
            SelectedRole);
    }

    private void LoadBlueprints()
    {
        _blueprints.Clear();

        if (_activeWorkspace is null || string.IsNullOrWhiteSpace(_activeWorkspace.RootPath))
            return;

        foreach (var blueprint in _blueprintStore.LoadForWorkspace(_activeWorkspace.RootPath))
            _blueprints.Add(blueprint);
    }

    private void SaveBlueprints()
    {
        if (_activeWorkspace is null || string.IsNullOrWhiteSpace(_activeWorkspace.RootPath))
            return;

        _blueprintStore.SaveForWorkspace(_activeWorkspace.RootPath, _blueprints);
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
        OnPropertyChanged(nameof(HasRootFsCatalog));
        OnPropertyChanged(nameof(RootFsNotice));
        OnPropertyChanged(nameof(RootFsFallbackNotice));
    }

    private void UpdateRootFsSelection(RootFsDescriptor? descriptor)
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

    private RootFsDescriptor? GetRootFsById(string? id)
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

        if (Plan is null)
            return "Load a plan to view AI Help.";

        return "Refresh AI Help.";
    }

    public ISourceControlIntent? SourceControlIntent => null;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
