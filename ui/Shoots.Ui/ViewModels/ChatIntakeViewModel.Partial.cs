using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ObservableCollection<ChatSessionViewModel> _chatSessions = new();
    private readonly ObservableCollection<string> _chatMessages = new();

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

    public ReadOnlyObservableCollection<ChatSessionViewModel> ChatSessions { get; private set; } = null!;

    public ReadOnlyObservableCollection<string> ChatMessages { get; private set; } = null!;

    public ChatSessionViewModel? SelectedChatSession
    {
        get => _selectedChatSession;
        set
        {
            if (Equals(_selectedChatSession, value))
                return;

            _selectedChatSession = value;
            OnPropertyChanged(nameof(SelectedChatSession));
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

    public AsyncRelayCommand ResumeInjectDecisionCommand { get; private set; } = null!;

    private void InitializeChatIntake()
    {
        ChatSessions = new ReadOnlyObservableCollection<ChatSessionViewModel>(_chatSessions);
        ChatMessages = new ReadOnlyObservableCollection<string>(_chatMessages);

        LockWorkOrderCommand = new AsyncRelayCommand(LockWorkOrderAsync, () => CanLockWorkOrder);
        UnlockWorkOrderCommand = new AsyncRelayCommand(UnlockWorkOrderAsync, () => IsWorkOrderLocked);
        GeneratePlanCommand = new AsyncRelayCommand(GeneratePlanFromIntakeAsync, () => IsWorkOrderLocked);
        RunIntakePlanCommand = new AsyncRelayCommand(StartAsync, CanStart);
        ResumeInjectDecisionCommand = new AsyncRelayCommand(ResumeWithInjectedDecisionAsync, () => CanResumeInjectDecision);

        _chatMessages.Add("System: Start a new work order from chat intake.");
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
            Array.Empty<string>()));

        JobSpecDigest = digest;
        IsWorkOrderLocked = true;

        var workOrderId = $"wo-{digest[..12]}";
        var session = new ChatSessionViewModel(workOrderId, PlanIdLabel, PlanHashLabel, "Draft");
        _chatSessions.Insert(0, session);
        SelectedChatSession = session;
        _chatMessages.Add($"System: WorkOrder locked ({workOrderId}).");
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
                ["intake.digest"] = JobSpecDigest
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
            var updated = SelectedChatSession with { PlanId = plan.PlanId, PlanHash = planHash, LastStatus = "PlanReady" };
            var idx = _chatSessions.IndexOf(SelectedChatSession);
            if (idx >= 0)
                _chatSessions[idx] = updated;
            SelectedChatSession = updated;
        }

        _chatMessages.Add($"System: Plan generated (hash {planHash[..12]}...).");
        return Task.CompletedTask;
    }

    private async Task ResumeWithInjectedDecisionAsync()
    {
        if (Plan is null || !CanResumeInjectDecision)
            return;

        var options = new RuntimeRunOptions(ResumeMode.InjectDecision, InjectedDecisionDigest);
        var result = await _commandService.StartAsync(Plan, options).ConfigureAwait(true);
        if (result.Ok)
            RecordExecutionSession(result);
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

public sealed record ChatSessionViewModel(string WorkOrderId, string PlanId, string PlanHash, string LastStatus);

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
    IReadOnlyList<string> Constraints);

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
