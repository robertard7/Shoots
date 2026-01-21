using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.Environment;
using Shoots.UI.Services;

namespace Shoots.UI.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IExecutionCommandService _commandService;
    private readonly EnvironmentProfileService _environmentService;
    private readonly IEnvironmentCapabilityProvider _capabilityProvider;
    private ExecutionState _state;
    private BuildPlan? _plan;
    private IEnvironmentProfile? _selectedProfile;
    private EnvironmentProfileResult? _lastEnvironmentResult;
    private bool _restartRequired;

    public MainWindowViewModel(
        IExecutionCommandService commandService,
        EnvironmentProfileService environmentService,
        IEnvironmentCapabilityProvider capabilityProvider)
    {
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
        _state = ExecutionState.Idle;

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new AsyncRelayCommand(CancelAsync, CanCancel);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        ApplyEnvironmentCommand = new AsyncRelayCommand(ApplyEnvironmentAsync, CanApplyEnvironment);

        Profiles = new ReadOnlyCollection<IEnvironmentProfile>(_environmentService.Profiles.ToList());
        SelectedProfile = Profiles.FirstOrDefault();
        EnvironmentCapabilities = Array.Empty<string>();
        EnvironmentCreatedPaths = Array.Empty<string>();
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

        var sandboxRoot = EnsureSandboxRoot();
        var previousCapabilities = _capabilityProvider.AvailableCapabilities;
        var result = _environmentService.ApplyProfile(sandboxRoot, SelectedProfile);
        _lastEnvironmentResult = result;
        _capabilityProvider.Update(result.DeclaredCapabilities);

        EnvironmentCreatedPaths = result.CreatedPaths.ToList();
        EnvironmentAppliedCapabilities = DescribeCapabilities(result.DeclaredCapabilities);
        EnvironmentAppliedAtUtc = $"{result.AppliedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        EnvironmentAppliedProfileName = result.ProfileName;
        RestartRequired = previousCapabilities != result.DeclaredCapabilities;

        OnPropertyChanged(nameof(EnvironmentCreatedPaths));
        OnPropertyChanged(nameof(EnvironmentAppliedCapabilities));
        OnPropertyChanged(nameof(EnvironmentAppliedAtUtc));
        OnPropertyChanged(nameof(EnvironmentAppliedProfileName));
        OnPropertyChanged(nameof(HasSourceControl));
        OnPropertyChanged(nameof(HasDatabase));
        OnPropertyChanged(nameof(HasArtifactExport));

        UpdateProfileCapabilities();
        RaiseCommandCanExecute();

        await Task.CompletedTask.ConfigureAwait(true);
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

    private static string EnsureSandboxRoot()
    {
        var root = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Shoots",
            "Sandbox");
        Directory.CreateDirectory(root);
        return root;
    }

    private void RaiseCommandCanExecute()
    {
        StartCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RefreshStatusCommand.RaiseCanExecuteChanged();
        ApplyEnvironmentCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
