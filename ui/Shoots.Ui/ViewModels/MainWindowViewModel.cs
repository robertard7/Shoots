using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.Environment;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
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
    private readonly IAiHelpFacade _aiHelpFacade;
    private readonly ObservableCollection<ProjectWorkspace> _recentWorkspaces;
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

    public MainWindowViewModel(
        IExecutionCommandService commandService,
        IEnvironmentProfileService environmentService,
        IEnvironmentCapabilityProvider capabilityProvider,
        IEnvironmentProfilePrompt profilePrompt,
        EnvironmentScriptLoader scriptLoader,
        IProjectWorkspaceProvider workspaceProvider,
        IWorkspaceShellService workspaceShell,
        IDatabaseIntentStore databaseIntentStore,
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
        _aiHelpFacade = aiHelpFacade ?? throw new ArgumentNullException(nameof(aiHelpFacade));
        _state = ExecutionState.Idle;

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new AsyncRelayCommand(CancelAsync, CanCancel);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        ApplyEnvironmentCommand = new AsyncRelayCommand(ApplyEnvironmentAsync, CanApplyEnvironment);
        ApplyScriptCommand = new AsyncRelayCommand(ApplyScriptAsync, CanApplyScript);
        RemoveWorkspaceCommand = new AsyncRelayCommand(RemoveWorkspaceAsync, CanRemoveWorkspace);
        OpenWorkspaceCommand = new AsyncRelayCommand(OpenWorkspaceAsync, CanOpenWorkspace);
        RefreshAiHelpCommand = new AsyncRelayCommand(RefreshAiHelpAsync, CanRefreshAiHelp);
        Profiles = new ReadOnlyCollection<IEnvironmentProfile>(_environmentService.Profiles.ToList());
        SelectedProfile = Profiles.FirstOrDefault();
        EnvironmentCapabilities = Array.Empty<string>();
        EnvironmentCreatedPaths = Array.Empty<string>();
        EnvironmentAppliedCapabilities = Array.Empty<string>();

        _recentWorkspaces = new ObservableCollection<ProjectWorkspace>();
        RecentWorkspaces = new ReadOnlyObservableCollection<ProjectWorkspace>(_recentWorkspaces);
        RefreshRecentWorkspaces();
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
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

    public AsyncRelayCommand RefreshAiHelpCommand { get; }

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

    public IReadOnlyList<string> ScriptSteps => _environmentScript?.SandboxSteps.Select(step => step.RelativePath).ToList()
        ?? Array.Empty<string>();

    public string ScriptSearchPath => _scriptSearchPath;

    public string? ScriptUnsupportedCapabilitiesMessage => _scriptUnsupportedCapabilitiesMessage;

    public string ScriptFolderCountLabel => $"Folders to create: {ScriptFolderCount}";

    public int ScriptFolderCount => _environmentScript?.SandboxSteps.Count(step => step.Kind == SandboxPreparationKind.CreateDirectory) ?? 0;

    public ReadOnlyObservableCollection<ProjectWorkspace> RecentWorkspaces { get; }

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

    public string AiHelpContextSummary { get; private set; } = "AI Help is informational only.";

    public string AiHelpStateExplanation { get; private set; } = "No runtime context is available.";

    public string AiHelpNextSteps { get; private set; } = "Select a workspace to view help.";

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
        RefreshAiHelpCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(StartDisabledReason));
        OnPropertyChanged(nameof(ApplyEnvironmentDisabledReason));
        OnPropertyChanged(nameof(ApplyScriptDisabledReason));
        OnPropertyChanged(nameof(AiHelpDisabledReason));
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

    private async Task RefreshAiHelpAsync()
    {
        if (!CanRefreshAiHelp())
        {
            AiHelpContextSummary = "AI Help is informational only.";
            AiHelpStateExplanation = "No runtime context is available.";
            AiHelpNextSteps = "Select a workspace and load a plan to view help.";
            OnPropertyChanged(nameof(AiHelpContextSummary));
            OnPropertyChanged(nameof(AiHelpStateExplanation));
            OnPropertyChanged(nameof(AiHelpNextSteps));
            return;
        }

        var context = BuildAiHelpContext();
        AiHelpContextSummary = await _aiHelpFacade.GetContextSummaryAsync(context).ConfigureAwait(true);
        AiHelpStateExplanation = await _aiHelpFacade.ExplainStateAsync(context).ConfigureAwait(true);
        AiHelpNextSteps = await _aiHelpFacade.SuggestNextStepsAsync(context).ConfigureAwait(true);
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

    private AiHelpContext BuildAiHelpContext()
    {
        return new AiHelpContext(
            ActiveWorkspace?.Name,
            ActiveWorkspace?.RootPath,
            StateLabel,
            null,
            SelectedProfile?.Name,
            _lastEnvironmentResult?.ProfileName);
    }

    private void UpdateDatabaseIntentSelection()
    {
        if (ActiveWorkspace is null)
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
