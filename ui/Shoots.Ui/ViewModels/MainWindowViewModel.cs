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
    private IDatabaseIntent? _selectedDatabaseIntent;

    public MainWindowViewModel(
        IExecutionCommandService commandService,
        IEnvironmentProfileService environmentService,
        IEnvironmentCapabilityProvider capabilityProvider,
        IEnvironmentProfilePrompt profilePrompt,
        EnvironmentScriptLoader scriptLoader,
        IProjectWorkspaceProvider workspaceProvider,
        IWorkspaceShellService workspaceShell)
    {
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
        _profilePrompt = profilePrompt ?? throw new ArgumentNullException(nameof(profilePrompt));
        _scriptLoader = scriptLoader ?? throw new ArgumentNullException(nameof(scriptLoader));
        _workspaceProvider = workspaceProvider ?? throw new ArgumentNullException(nameof(workspaceProvider));
        _workspaceShell = workspaceShell ?? throw new ArgumentNullException(nameof(workspaceShell));
        _state = ExecutionState.Idle;

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new AsyncRelayCommand(CancelAsync, CanCancel);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        ApplyEnvironmentCommand = new AsyncRelayCommand(ApplyEnvironmentAsync, CanApplyEnvironment);
        ApplyScriptCommand = new AsyncRelayCommand(ApplyScriptAsync, CanApplyScript);
        RemoveWorkspaceCommand = new AsyncRelayCommand(RemoveWorkspaceAsync);
        OpenWorkspaceCommand = new AsyncRelayCommand(OpenWorkspaceAsync);
        Profiles = new ReadOnlyCollection<IEnvironmentProfile>(_environmentService.Profiles.ToList());
        SelectedProfile = Profiles.FirstOrDefault();
        EnvironmentCapabilities = Array.Empty<string>();
        EnvironmentCreatedPaths = Array.Empty<string>();
        EnvironmentAppliedCapabilities = Array.Empty<string>();

        RecentWorkspaces = new ObservableCollection<ProjectWorkspace>();
        RefreshRecentWorkspaces();
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
        DatabaseIntents = new ReadOnlyCollection<IDatabaseIntent>(new IDatabaseIntent[]
        {
            new DatabaseIntentNone(),
            new DatabaseIntentLocalFile(),
            new DatabaseIntentExternalService()
        });
        SelectedDatabaseIntent = DatabaseIntents.FirstOrDefault();
        LoadEnvironmentScript();
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

    public string ScriptFolderCountLabel => $"Folders to create: {ScriptFolderCount}";

    public int ScriptFolderCount => _environmentScript?.SandboxSteps.Count(step => step.Kind == SandboxPreparationKind.CreateDirectory) ?? 0;

    public ObservableCollection<ProjectWorkspace> RecentWorkspaces { get; }

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
            OnPropertyChanged(nameof(SelectedWorkspace));
            LoadEnvironmentScript();
            UpdateProfileCapabilities();
        }
    }

    public string ActiveWorkspaceName => ActiveWorkspace?.Name ?? "No project selected";

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

    public IReadOnlyList<IDatabaseIntent> DatabaseIntents { get; }

    public IDatabaseIntent? SelectedDatabaseIntent
    {
        get => _selectedDatabaseIntent;
        set
        {
            if (Equals(_selectedDatabaseIntent, value))
                return;

            _selectedDatabaseIntent = value;
            OnPropertyChanged(nameof(SelectedDatabaseIntent));
        }
    }

    public void SetPlan(BuildPlan? plan) => Plan = plan;

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
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            _environmentScript = null;
            EnvironmentErrorMessage = error;
            EnvironmentInfoMessage = null;
        }
        else
        {
            _environmentScript = null;
            EnvironmentInfoMessage = null;
            EnvironmentErrorMessage = null;
        }

        OnPropertyChanged(nameof(ScriptPreview));
        OnPropertyChanged(nameof(ScriptCapabilities));
        OnPropertyChanged(nameof(ScriptSteps));
        OnPropertyChanged(nameof(ScriptSearchPath));
        OnPropertyChanged(nameof(ScriptFolderCount));
        OnPropertyChanged(nameof(ScriptFolderCountLabel));
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

    private Task OpenWorkspaceAsync(object? parameter)
    {
        if (parameter is ProjectWorkspace workspace && !string.IsNullOrWhiteSpace(workspace.RootPath))
            _workspaceShell.OpenFolder(workspace.RootPath);

        return Task.CompletedTask;
    }

    public void SelectWorkspace(ProjectWorkspace workspace)
    {
        _workspaceProvider.SetActiveWorkspace(workspace);
        ActiveWorkspace = _workspaceProvider.GetActiveWorkspace();
        RefreshRecentWorkspaces();
    }

    private void RefreshRecentWorkspaces()
    {
        RecentWorkspaces.Clear();
        foreach (var workspace in _workspaceProvider.GetRecentWorkspaces())
            RecentWorkspaces.Add(workspace);
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
