using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Host.Core.ModelCatalog;
using Shoots.Runtime.Abstractions;
using Shoots.UI.Interop;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<ChatSessionViewModel> _chatSessions = new();
    private readonly ObservableCollection<string> _chatMessages = new();
    private readonly ObservableCollection<TraceEntryViewModel> _traceEntries = new();
    private readonly ObservableCollection<ArtifactViewModel> _artifacts = new();
    private readonly LocalModelCatalog _modelCatalog = new();

    private ChatSessionViewModel? _selectedChatSession;
    private string _intakeIntent = string.Empty;
    private string _intakeTarget = "New Project";
    private string _intakeAttachments = string.Empty;
    private string _intakeStack = "dotnet";
    private bool _isWorkOrderLocked;
    private string _jobSpecDigest = string.Empty;
    private string _planHashLabel = "(none)";
    private string _planIdLabel = "(none)";
    private DecisionGateWaitingInfoViewModel? _lastWaitingInfo;
    private string _decisionToolId = string.Empty;
    private string _decisionBindingsJson = "{}";
    private string _injectedDecisionDigest = string.Empty;
    private string _selectedModelId = "local.default";
    private ResumeMode _selectedRunMode = ResumeMode.None;
    private string _lastResumePayload = string.Empty;
    private string _traceFilterText = string.Empty;
    private string _traceEventFilter = "All";
    private string _defaultModelId = "local.default";
    private string _lastTracePayload = string.Empty;
    private string _traceLogPath = string.Empty;
    private string _artifactsOutputPath = string.Empty;
    private string _modelCatalogError = string.Empty;
    private string _catalogHash = string.Empty;
    private string _lastSmokeRunId = string.Empty;

    public ReadOnlyObservableCollection<ChatSessionViewModel> ChatSessions { get; private set; } = null!;

    public ReadOnlyObservableCollection<string> ChatMessages { get; private set; } = null!;

    public ReadOnlyObservableCollection<TraceEntryViewModel> TraceEntries { get; private set; } = null!;

    public ReadOnlyObservableCollection<ArtifactViewModel> Artifacts { get; private set; } = null!;

    public ChatSessionViewModel? SelectedChatSession
    {
        get => _selectedChatSession;
        set
        {
            if (Equals(_selectedChatSession, value))
                return;

            _selectedChatSession = value;
            OnPropertyChanged(nameof(SelectedChatSession));

            if (_selectedChatSession is not null)
            {
                PlanIdLabel = _selectedChatSession.PlanId;
                PlanHashLabel = _selectedChatSession.PlanHash;
                LastWaitingInfo = _selectedChatSession.LastWaitingInfo;
                OnPropertyChanged(nameof(LastWorkOrderId));
                OnPropertyChanged(nameof(LastRunStatus));
            }
        }
    }

    public string IntakeIntent
    {
        get => _intakeIntent;
        set
        {
            if (_intakeIntent == value)
                return;

            _intakeIntent = value;
            OnPropertyChanged(nameof(IntakeIntent));
            OnPropertyChanged(nameof(CanLockWorkOrder));
        }
    }

    public string IntakeTarget
    {
        get => _intakeTarget;
        set
        {
            if (_intakeTarget == value)
                return;

            _intakeTarget = value;
            OnPropertyChanged(nameof(IntakeTarget));
        }
    }

    public string IntakeAttachments
    {
        get => _intakeAttachments;
        set
        {
            if (_intakeAttachments == value)
                return;

            _intakeAttachments = value;
            OnPropertyChanged(nameof(IntakeAttachments));
        }
    }

    public string IntakeStack
    {
        get => _intakeStack;
        set
        {
            if (_intakeStack == value)
                return;

            _intakeStack = value;
            OnPropertyChanged(nameof(IntakeStack));
        }
    }


    public IReadOnlyList<string> AvailableModels => _modelCatalog.ListModels().Select(x => x.ModelId).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public string DefaultModelId => _defaultModelId;

    public string ModelCatalogError
    {
        get => _modelCatalogError;
        private set
        {
            if (_modelCatalogError == value)
                return;

            _modelCatalogError = value;
            OnPropertyChanged(nameof(ModelCatalogError));
            OnPropertyChanged(nameof(HasModelCatalogError));
        }
    }

    public bool HasModelCatalogError => !string.IsNullOrWhiteSpace(ModelCatalogError);


    public string CatalogHash => _catalogHash;

    public string LastWorkOrderId => SelectedChatSession?.WorkOrderId ?? string.Empty;

    public string LastRunStatus => SelectedChatSession?.LastStatus ?? string.Empty;

    public string LastSmokeRunId
    {
        get => _lastSmokeRunId;
        private set
        {
            if (_lastSmokeRunId == value)
                return;

            _lastSmokeRunId = value;
            OnPropertyChanged(nameof(LastSmokeRunId));
        }
    }

    public string SelectedModelId
    {
        get => _selectedModelId;
        set
        {
            if (_selectedModelId == value)
                return;

            _selectedModelId = value;
            OnPropertyChanged(nameof(SelectedModelId));
            JobSpecDigest = JobSpecDigestBuilder.Compute(new JobSpecDigestInput(IntakeIntent, IntakeTarget, ParseList(IntakeAttachments), IntakeStack, Array.Empty<string>(), SelectedModelId));
        }
    }

    public bool IsWorkOrderLocked
    {
        get => _isWorkOrderLocked;
        private set
        {
            if (_isWorkOrderLocked == value)
                return;

            _isWorkOrderLocked = value;
            OnPropertyChanged(nameof(IsWorkOrderLocked));
            OnPropertyChanged(nameof(CanLockWorkOrder));
            LockWorkOrderCommand.RaiseCanExecuteChanged();
            UnlockWorkOrderCommand.RaiseCanExecuteChanged();
            GeneratePlanCommand.RaiseCanExecuteChanged();
            ResumeInjectDecisionCommand.RaiseCanExecuteChanged();
        }
    }

    public string JobSpecDigest
    {
        get => _jobSpecDigest;
        private set
        {
            if (_jobSpecDigest == value)
                return;

            _jobSpecDigest = value;
            OnPropertyChanged(nameof(JobSpecDigest));
        }
    }

    public string PlanHashLabel
    {
        get => _planHashLabel;
        private set
        {
            if (_planHashLabel == value)
                return;

            _planHashLabel = value;
            OnPropertyChanged(nameof(PlanHashLabel));
        }
    }

    public string PlanIdLabel
    {
        get => _planIdLabel;
        private set
        {
            if (_planIdLabel == value)
                return;

            _planIdLabel = value;
            OnPropertyChanged(nameof(PlanIdLabel));
        }
    }


    public string DecisionToolId
    {
        get => _decisionToolId;
        set
        {
            if (_decisionToolId == value)
                return;

            _decisionToolId = value;
            OnPropertyChanged(nameof(DecisionToolId));
            RefreshInjectedDecisionDigest();
            OnPropertyChanged(nameof(ToolCatalogEntries));
        }
    }

    public string DecisionBindingsJson
    {
        get => _decisionBindingsJson;
        set
        {
            if (_decisionBindingsJson == value)
                return;

            _decisionBindingsJson = value;
            OnPropertyChanged(nameof(DecisionBindingsJson));
            RefreshInjectedDecisionDigest();
            OnPropertyChanged(nameof(ToolCatalogEntries));
        }
    }

    public string InjectedDecisionDigest
    {
        get => _injectedDecisionDigest;
        private set
        {
            if (_injectedDecisionDigest == value)
                return;

            _injectedDecisionDigest = value;
            OnPropertyChanged(nameof(InjectedDecisionDigest));
            ResumeInjectDecisionCommand.RaiseCanExecuteChanged();
        }
    }


    public ResumeMode SelectedRunMode
    {
        get => _selectedRunMode;
        set
        {
            if (_selectedRunMode == value)
                return;

            _selectedRunMode = value;
            OnPropertyChanged(nameof(SelectedRunMode));
            ResumeInjectDecisionCommand.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<ResumeMode> RunModes { get; } = Enum.GetValues<ResumeMode>();

    public string WaitingExplanation
    {
        get
        {
            if (LastWaitingInfo is null)
                return "No waiting gate is active.";

            if (LastWaitingInfo.Policy == DecisionPolicy.Bypass.ToString() && LastWaitingInfo.FallbackPresent)
                return $"Gate {LastWaitingInfo.RouteGateId} at node {LastWaitingInfo.CurrentNodeId} is waiting: bypass policy has fallback available but needs explicit host resume intent.";

            if (LastWaitingInfo.AllowedNextNodes.Count > 1)
                return $"Gate {LastWaitingInfo.RouteGateId} at node {LastWaitingInfo.CurrentNodeId} is waiting for explicit selection among multiple graph-derived candidates.";

            return $"Gate {LastWaitingInfo.RouteGateId} at node {LastWaitingInfo.CurrentNodeId} is waiting for explicit decision input.";
        }
    }


    public string TraceFilterText
    {
        get => _traceFilterText;
        set
        {
            if (_traceFilterText == value)
                return;
            _traceFilterText = value;
            OnPropertyChanged(nameof(TraceFilterText));
            OnPropertyChanged(nameof(FilteredTraceEntries));
        }
    }

    public string TraceEventFilter
    {
        get => _traceEventFilter;
        set
        {
            if (_traceEventFilter == value)
                return;
            _traceEventFilter = value;
            OnPropertyChanged(nameof(TraceEventFilter));
            OnPropertyChanged(nameof(FilteredTraceEntries));
        }
    }

    public IReadOnlyList<string> TraceEventFilters => new[] { "All", "Host", "DecisionGate", "Tool", "Error" };

    public IReadOnlyList<TraceEntryViewModel> FilteredTraceEntries => TraceEntries
        .Where(entry => (TraceEventFilter == "All"
            || (TraceEventFilter == "Host" && entry.Event.StartsWith("Host", StringComparison.Ordinal))
            || (TraceEventFilter == "DecisionGate" && entry.Event.Contains("DecisionGate", StringComparison.Ordinal))
            || (TraceEventFilter == "Tool" && entry.Event.Contains("Tool", StringComparison.Ordinal))
            || (TraceEventFilter == "Error" && (entry.Event.Contains("Error", StringComparison.Ordinal) || entry.Event.Contains("Halted", StringComparison.Ordinal))))
            && (string.IsNullOrWhiteSpace(TraceFilterText)
                || entry.Event.Contains(TraceFilterText, StringComparison.OrdinalIgnoreCase)
                || (entry.Detail?.Contains(TraceFilterText, StringComparison.OrdinalIgnoreCase) ?? false)))
        .ToList();

    public string LastResumePayload
    {
        get => _lastResumePayload;
        private set
        {
            if (_lastResumePayload == value)
                return;

            _lastResumePayload = value;
            OnPropertyChanged(nameof(LastResumePayload));
        }
    }


    public string LastTracePayload
    {
        get => _lastTracePayload;
        private set
        {
            if (_lastTracePayload == value)
                return;

            _lastTracePayload = value;
            OnPropertyChanged(nameof(LastTracePayload));
        }
    }

    public string TraceLogPath
    {
        get => _traceLogPath;
        private set
        {
            if (_traceLogPath == value)
                return;

            _traceLogPath = value;
            OnPropertyChanged(nameof(TraceLogPath));
        }
    }

    public string ArtifactsOutputPath
    {
        get => _artifactsOutputPath;
        private set
        {
            if (_artifactsOutputPath == value)
                return;

            _artifactsOutputPath = value;
            OnPropertyChanged(nameof(ArtifactsOutputPath));
        }
    }

    public DecisionGateWaitingInfoViewModel? LastWaitingInfo
    {
        get => _lastWaitingInfo;
        private set
        {
            if (Equals(_lastWaitingInfo, value))
                return;

            _lastWaitingInfo = value;
            OnPropertyChanged(nameof(LastWaitingInfo));
            OnPropertyChanged(nameof(HasWaitingInfo));
            OnPropertyChanged(nameof(WaitingExplanation));

            if (_lastWaitingInfo is not null && string.IsNullOrWhiteSpace(_decisionToolId) && _lastWaitingInfo.AllowedNextNodes.Count > 0)
            {
                _decisionToolId = _lastWaitingInfo.AllowedNextNodes[0];
                OnPropertyChanged(nameof(DecisionToolId));
            }

            RefreshInjectedDecisionDigest();
            OnPropertyChanged(nameof(ToolCatalogEntries));
        }
    }

    public bool HasWaitingInfo => LastWaitingInfo is not null;

    public bool CanResumeInjectDecision => HasWaitingInfo && IsWorkOrderLocked && Plan is not null && !string.IsNullOrWhiteSpace(InjectedDecisionDigest);


    public IReadOnlyList<ToolCatalogItemViewModel> ToolCatalogEntries
    {
        get
        {
            var fromPlan = Plan?.Steps
                .OfType<ToolBuildStep>()
                .Select(step => step.ToolId.Value)
                .Distinct(StringComparer.Ordinal)
                .Select(id => new ToolCatalogItemViewModel(id, id, "runtime", Plan?.Authority.ProviderId.Value ?? "unknown"))
                .ToList() ?? new List<ToolCatalogItemViewModel>();

            if (LastWaitingInfo is not null)
            {
                foreach (var node in LastWaitingInfo.AllowedNextNodes)
                {
                    if (!fromPlan.Any(x => string.Equals(x.ToolId, node, StringComparison.Ordinal)))
                        fromPlan.Add(new ToolCatalogItemViewModel(node, node, "waiting", Plan?.Authority.ProviderId.Value ?? "unknown"));
                }
            }

            return fromPlan;
        }
    }

    public bool CanLockWorkOrder => !IsWorkOrderLocked && !string.IsNullOrWhiteSpace(IntakeIntent);

    public AsyncRelayCommand LockWorkOrderCommand { get; private set; } = null!;

    public AsyncRelayCommand UnlockWorkOrderCommand { get; private set; } = null!;

    public AsyncRelayCommand GeneratePlanCommand { get; private set; } = null!;

    public AsyncRelayCommand RunIntakePlanCommand { get; private set; } = null!;

    public AsyncRelayCommand QuickStartCommand { get; private set; } = null!;

    public AsyncRelayCommand ResumeInjectDecisionCommand { get; private set; } = null!;

    public AsyncRelayCommand UseFallbackToolCommand { get; private set; } = null!;

    public AsyncRelayCommand CopyResumePayloadCommand { get; private set; } = null!;

    public AsyncRelayCommand CopyTraceCommand { get; private set; } = null!;

    public AsyncRelayCommand CopyTracePathCommand { get; private set; } = null!;

    public AsyncRelayCommand CopyArtifactsPathCommand { get; private set; } = null!;

    public AsyncRelayCommand RefreshModelCatalogCommand { get; private set; } = null!;

    public AsyncRelayCommand ResetModelCatalogCommand { get; private set; } = null!;

    public AsyncRelayCommand OpenStateFolderCommand { get; private set; } = null!;

    private void InitializeChatIntake()
    {
        ChatSessions = new ReadOnlyObservableCollection<ChatSessionViewModel>(_chatSessions);
        ChatMessages = new ReadOnlyObservableCollection<string>(_chatMessages);
        TraceEntries = new ReadOnlyObservableCollection<TraceEntryViewModel>(_traceEntries);
        Artifacts = new ReadOnlyObservableCollection<ArtifactViewModel>(_artifacts);

        LoadPersistedSessions();

        LockWorkOrderCommand = new AsyncRelayCommand(LockWorkOrderAsync, () => CanLockWorkOrder);
        UnlockWorkOrderCommand = new AsyncRelayCommand(UnlockWorkOrderAsync, () => IsWorkOrderLocked);
        GeneratePlanCommand = new AsyncRelayCommand(GeneratePlanFromIntakeAsync, () => IsWorkOrderLocked);
        RunIntakePlanCommand = new AsyncRelayCommand(StartAsync, CanStart);
        QuickStartCommand = new AsyncRelayCommand(QuickStartAsync, () => !string.IsNullOrWhiteSpace(IntakeIntent));
        ResumeInjectDecisionCommand = new AsyncRelayCommand(ResumeWithInjectedDecisionAsync, () => CanResumeInjectDecision);
        UseFallbackToolCommand = new AsyncRelayCommand(UseFallbackToolAsync, () => HasWaitingInfo && LastWaitingInfo?.FallbackPresent == true);
        CopyResumePayloadCommand = new AsyncRelayCommand(CopyResumePayloadAsync, () => HasWaitingInfo && !string.IsNullOrWhiteSpace(InjectedDecisionDigest));
        CopyTraceCommand = new AsyncRelayCommand(CopyTraceAsync, () => _traceEntries.Count > 0);
        CopyTracePathCommand = new AsyncRelayCommand(CopyTracePathAsync, () => !string.IsNullOrWhiteSpace(TraceLogPath));
        CopyArtifactsPathCommand = new AsyncRelayCommand(CopyArtifactsPathAsync, () => !string.IsNullOrWhiteSpace(ArtifactsOutputPath));
        RefreshModelCatalogCommand = new AsyncRelayCommand(RefreshModelCatalogAsync);
        ResetModelCatalogCommand = new AsyncRelayCommand(ResetModelCatalogAsync, () => HasModelCatalogError);
        OpenStateFolderCommand = new AsyncRelayCommand(OpenStateFolderAsync);

        LoadModelCatalogState();
        LastSmokeRunId = ResolveLastSmokeRunId();

        _chatMessages.Add("System: Start a new work order from chat intake.");
    }

    private async Task QuickStartAsync()
    {
        if (!IsWorkOrderLocked)
            await LockWorkOrderAsync().ConfigureAwait(true);

        if (Plan is null)
            await GeneratePlanFromIntakeAsync().ConfigureAwait(true);

        await StartAsync().ConfigureAwait(true);
    }

    private Task LockWorkOrderAsync()
    {
        if (!CanLockWorkOrder)
            return Task.CompletedTask;

        var digest = JobSpecDigestBuilder.Compute(new JobSpecDigestInput(
            IntakeIntent,
            IntakeTarget,
            ParseList(IntakeAttachments),
            IntakeStack,
            Array.Empty<string>(),
            SelectedModelId));

        JobSpecDigest = digest;
        IsWorkOrderLocked = true;

        var workOrderId = $"wo-{digest[..12]}";
        var session = new ChatSessionViewModel(workOrderId, PlanIdLabel, PlanHashLabel, "Draft", DateTimeOffset.UtcNow, null);
        _chatSessions.Insert(0, session);
        SelectedChatSession = session;
        _chatMessages.Add($"System: WorkOrder locked ({workOrderId}).");
        SavePersistedSessions();
        return Task.CompletedTask;
    }

    private Task UnlockWorkOrderAsync()
    {
        IsWorkOrderLocked = false;
        JobSpecDigest = string.Empty;
        _chatMessages.Add("System: WorkOrder unlocked.");
        return Task.CompletedTask;
    }

    private Task GeneratePlanFromIntakeAsync()
    {
        if (!IsWorkOrderLocked)
            return Task.CompletedTask;

        var workOrderId = new WorkOrderId($"wo-{JobSpecDigest[..12]}");
        var workOrder = new WorkOrder(
            workOrderId,
            IntakeIntent,
            $"Target={IntakeTarget}; Stack={IntakeStack}",
            ParseList(IntakeAttachments),
            new[] { "Complete deterministically." });

        var routeRules = new[]
        {
            new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }, DecisionPolicy.Hard),
            new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
        };

        var request = new BuildRequest(
            workOrder,
            "chat.intake",
            new Dictionary<string, object?>
            {
                ["intake.target"] = IntakeTarget,
                ["intake.stack"] = IntakeStack,
                ["intake.digest"] = JobSpecDigest,
                ["intake.model"] = SelectedModelId
            },
            routeRules);

        var authority = new DelegationAuthority(new ProviderId("fake.local"), ProviderKind.Local, "ui-intake", true);
        var steps = new BuildStep[]
        {
            new RouteStep("select", "Select execution tool.", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrderId),
            new RouteStep("terminate", "Finish run.", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, workOrderId)
        };

        var planCanonical = JsonSerializer.Serialize(new
        {
            request.CommandId,
            workOrder.Id,
            workOrder.OriginalRequest,
            routeRules = routeRules.Select(r => new { r.NodeId, r.Intent, r.Owner, r.DecisionPolicy }),
            steps = steps.Select(s => s.Id)
        });

        var planHash = Hash(planCanonical);
        var plan = new BuildPlan(
            planHash,
            request,
            GraphStructureHash: Hash("chat.intake.graph"),
            NodeSetHash: Hash("chat.intake.nodes"),
            EdgeSetHash: Hash("chat.intake.edges"),
            authority,
            steps,
            new[] { new BuildArtifact("chat.intake.log", "Chat intake artifact") });

        SetPlan(plan);
        PlanHashLabel = planHash;
        PlanIdLabel = plan.PlanId;
        OnPropertyChanged(nameof(ToolCatalogEntries));

        if (SelectedChatSession is not null)
        {
            var updated = SelectedChatSession with { PlanId = plan.PlanId, PlanHash = planHash, LastStatus = "PlanReady", LastUpdatedUtc = DateTimeOffset.UtcNow };
            var idx = _chatSessions.IndexOf(SelectedChatSession);
            if (idx >= 0)
                _chatSessions[idx] = updated;
            SelectedChatSession = updated;
        }

        _chatMessages.Add($"System: Plan generated (hash {planHash[..12]}...).");
        SavePersistedSessions();
        return Task.CompletedTask;
    }

    private async Task ResumeWithInjectedDecisionAsync()
    {
        if (Plan is null || !CanResumeInjectDecision)
            return;

        var request = new DecisionInjectionRequest(
            LastWaitingInfo!.WorkOrderId,
            LastWaitingInfo.PlanHash,
            LastWaitingInfo.RouteGateId,
            DecisionToolId,
            CanonicalJson.Normalize(DecisionBindingsJson));

        var intent = SelectedRunMode switch
        {
            ResumeMode.OverridePlanChange => new HostResumeIntent(HostResumeIntentMode.OverridePlanChange),
            ResumeMode.DiscardWaitingStartOver => new HostResumeIntent(HostResumeIntentMode.DiscardWaitingStartOver),
            ResumeMode.InjectDecision => new HostResumeIntent(HostResumeIntentMode.InjectDecision),
            _ => new HostResumeIntent(HostResumeIntentMode.None)
        };

        var result = await _hostExecutionService.ResumeAsync(Plan, request, intent).ConfigureAwait(true);
        if (result.Ok)
            RecordExecutionSession(result);
    }

    private Task UseFallbackToolAsync()
    {
        if (LastWaitingInfo is null || !LastWaitingInfo.FallbackPresent || LastWaitingInfo.AllowedNextNodes.Count == 0)
            return Task.CompletedTask;

        DecisionToolId = LastWaitingInfo.AllowedNextNodes[0];
        return Task.CompletedTask;
    }

    private Task CopyResumePayloadAsync()
    {
        if (LastWaitingInfo is null)
            return Task.CompletedTask;

        LastResumePayload = JsonSerializer.Serialize(new DecisionInjectionRequest(
            LastWaitingInfo.WorkOrderId,
            LastWaitingInfo.PlanHash,
            LastWaitingInfo.RouteGateId,
            DecisionToolId,
            CanonicalJson.Normalize(DecisionBindingsJson)));

        return Task.CompletedTask;
    }



    private Task CopyTraceAsync()
    {
        LastTracePayload = JsonSerializer.Serialize(FilteredTraceEntries);
        return Task.CompletedTask;
    }

    private Task CopyTracePathAsync()
    {
        LastTracePayload = TraceLogPath;
        return Task.CompletedTask;
    }

    private Task CopyArtifactsPathAsync()
    {
        LastTracePayload = ArtifactsOutputPath;
        return Task.CompletedTask;
    }

    private Task RefreshModelCatalogAsync()
    {
        LoadModelCatalogState();
        return Task.CompletedTask;
    }

    private Task ResetModelCatalogAsync()
    {
        _modelCatalog.ResetCatalogToDefaults();
        LoadModelCatalogState();
        return Task.CompletedTask;
    }

    private void LoadModelCatalogState()
    {
        try
        {
            var models = _modelCatalog.ListModels();
            _defaultModelId = _modelCatalog.ResolveDefaultModel().ModelId;
            if (!models.Any(m => string.Equals(m.ModelId, _selectedModelId, StringComparison.Ordinal)))
                _selectedModelId = _defaultModelId;

            ModelCatalogError = string.Empty;
            _catalogHash = JobSpecDigestBuilder.HashCanonical(models.Select(m => new { m.ModelId, m.ProviderId, m.Priority, m.IsRemote, m.SupportsTools }).ToArray());
        }
        catch (Exception ex)
        {
            ModelCatalogError = $"Model catalog load failed: {ex.Message}";
            _defaultModelId = "local.default";
            _selectedModelId = _defaultModelId;
            _catalogHash = string.Empty;
        }

        OnPropertyChanged(nameof(DefaultModelId));
        OnPropertyChanged(nameof(CatalogHash));
        OnPropertyChanged(nameof(AvailableModels));
        OnPropertyChanged(nameof(SelectedModelId));
        ResetModelCatalogCommand.RaiseCanExecuteChanged();
    }
    private Task OpenStateFolderAsync()
    {
        var statePath = Path.GetFullPath(Path.Combine(".state"));
        if (OperatingSystem.IsWindows() && ShellExecuteHelper.OpenPath(statePath))
        {
            return Task.CompletedTask;
        }

        LastTracePayload = statePath;

        return Task.CompletedTask;
    }

    private static string ResolveLastSmokeRunId()
    {
        var traceRoot = Path.GetFullPath(Path.Combine(".state", "trace"));
        if (!Directory.Exists(traceRoot))
            return string.Empty;

        var latest = Directory.EnumerateFiles(traceRoot, "wo-smoke-*.trace.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null)
            return string.Empty;

        return Path.GetFileNameWithoutExtension(latest);
    }

    private void RefreshInjectedDecisionDigest()
    {
        if (!HasWaitingInfo || string.IsNullOrWhiteSpace(DecisionToolId))
        {
            InjectedDecisionDigest = string.Empty;
            return;
        }

        try
        {
            var canonicalBindings = CanonicalJson.Normalize(DecisionBindingsJson);
            InjectedDecisionDigest = JobSpecDigestBuilder.HashCanonical(new
            {
                toolId = DecisionToolId.Trim(),
                bindings = canonicalBindings,
                planHash = LastWaitingInfo?.PlanHash,
                workOrderId = LastWaitingInfo?.WorkOrderId
            });
        }
        catch (JsonException)
        {
            InjectedDecisionDigest = string.Empty;
        }
    }

    private string SessionStatePath => Path.GetFullPath(Path.Combine(".state", "chat-intake-sessions.json"));

    private void LoadPersistedSessions()
    {
        var path = SessionStatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            return;

        var persisted = JsonSerializer.Deserialize<List<ChatSessionViewModel>>(File.ReadAllText(path)) ?? new List<ChatSessionViewModel>();
        foreach (var session in persisted.OrderByDescending(x => x.LastUpdatedUtc))
            _chatSessions.Add(session);
    }

    private void SavePersistedSessions()
    {
        var path = SessionStatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ordered = _chatSessions.OrderByDescending(x => x.LastUpdatedUtc).ThenBy(x => x.WorkOrderId, StringComparer.Ordinal).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(ordered));
    }

    public void CaptureExecutionSnapshot(ExecutionEnvelope envelope)
    {
        _traceEntries.Clear();
        foreach (var entry in envelope.Trace.Entries)
            _traceEntries.Add(new TraceEntryViewModel(entry.Tick, entry.Event.ToString(), entry.Detail));

        _artifacts.Clear();
        foreach (var artifact in envelope.Artifacts)
            _artifacts.Add(new ArtifactViewModel(artifact.Id, artifact.Description, artifact.Id));

        TraceLogPath = ResolveTraceLogPath(envelope.GetExecutionId());
        ArtifactsOutputPath = ResolveArtifactsOutputPath(envelope.GetExecutionId());
        PersistTrace(TraceLogPath, _traceEntries);
        EnsureArtifactsDirectory(ArtifactsOutputPath);
        CopyTraceCommand.RaiseCanExecuteChanged();
        CopyTracePathCommand.RaiseCanExecuteChanged();
        CopyArtifactsPathCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(FilteredTraceEntries));
    }

    public void CaptureExecutionSnapshot(ExecutionEnvelopeDto envelope)
    {
        _traceEntries.Clear();
        foreach (var entry in envelope.Trace.Entries)
            _traceEntries.Add(new TraceEntryViewModel(entry.Tick, entry.Event.ToString(), entry.Detail));

        _artifacts.Clear();
        foreach (var artifact in envelope.Artifacts)
            _artifacts.Add(new ArtifactViewModel(artifact.Id, artifact.Description, artifact.Id));

        TraceLogPath = ResolveTraceLogPath(envelope.GetExecutionId());
        ArtifactsOutputPath = ResolveArtifactsOutputPath(envelope.GetExecutionId());
        PersistTrace(TraceLogPath, _traceEntries);
        EnsureArtifactsDirectory(ArtifactsOutputPath);
        CopyTraceCommand.RaiseCanExecuteChanged();
        CopyTracePathCommand.RaiseCanExecuteChanged();
        CopyArtifactsPathCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(FilteredTraceEntries));
    }


    private static string ResolveTraceLogPath(string workOrderId)
        => Path.GetFullPath(Path.Combine(".state", "trace", $"{workOrderId}.trace.json"));

    private static string ResolveArtifactsOutputPath(string workOrderId)
        => Path.GetFullPath(Path.Combine(".state", "artifacts", workOrderId));

    private static void PersistTrace(string path, IEnumerable<TraceEntryViewModel> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void EnsureArtifactsDirectory(string path)
        => Directory.CreateDirectory(path);

    private static IReadOnlyList<string> ParseList(string value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static DecisionGateWaitingInfoViewModel ToWaitingInfoViewModel(DecisionGateWaitingInfo waiting)
        => new(
            waiting.WorkOrderId.Value,
            waiting.RouteGateId,
            waiting.CurrentNodeId,
            waiting.IntentTokenHash,
            waiting.PlanHash,
            waiting.Policy.ToString(),
            waiting.FallbackPresent,
            waiting.ReasonCode,
            waiting.AllowedNextNodes,
            waiting.DecisionPromptKey,
            waiting.DecisionOwner.ToString());
}

public sealed record ChatSessionViewModel(string WorkOrderId, string PlanId, string PlanHash, string LastStatus, DateTimeOffset LastUpdatedUtc, DecisionGateWaitingInfoViewModel? LastWaitingInfo);

public sealed record TraceEntryViewModel(int Tick, string Event, string? Detail);

public sealed record ArtifactViewModel(string Id, string Description, string Path);

public sealed record ToolCatalogItemViewModel(string ToolId, string Title, string Category, string Authority);

public sealed record DecisionGateWaitingInfoViewModel(
    string WorkOrderId,
    string RouteGateId,
    string CurrentNodeId,
    string IntentTokenHash,
    string PlanHash,
    string Policy,
    bool FallbackPresent,
    string ReasonCode,
    IReadOnlyList<string> AllowedNextNodes,
    string DecisionPromptKey,
    string DecisionOwner);

public sealed record JobSpecDigestInput(
    string Intent,
    string Target,
    IReadOnlyList<string> Attachments,
    string Stack,
    IReadOnlyList<string> Constraints,
    string ModelId);

public static class JobSpecDigestBuilder
{
    public static string HashCanonical(object value)
    {
        var canonical = JsonSerializer.Serialize(value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Compute(JobSpecDigestInput input)
    {
        return HashCanonical(new
        {
            intent = input.Intent.Trim(),
            target = input.Target.Trim(),
            stack = input.Stack.Trim(),
            modelId = input.ModelId.Trim(),
            attachments = input.Attachments.OrderBy(x => x, StringComparer.Ordinal),
            constraints = input.Constraints.OrderBy(x => x, StringComparer.Ordinal)
        });
    }
}


public static class CanonicalJson
{
    public static string Normalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        using var doc = JsonDocument.Parse(json);
        return NormalizeElement(doc.RootElement);
    }

    private static string NormalizeElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + NormalizeElement(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(NormalizeElement)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => "null"
        };
}
