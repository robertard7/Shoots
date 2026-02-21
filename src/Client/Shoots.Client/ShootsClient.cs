using System.Security.Cryptography;
using System.Text;
using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Host.Core;
using Shoots.Host.Core.ModelCatalog;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Abstractions.Provider;
using Shoots.ProviderAdapters.Abstractions;

namespace Shoots.Client;

public sealed record ShootsClientOptions(
    HostPolicyOptions? Policy = null,
    string? SelectedModelId = null,
    string? StateRoot = null);

public sealed record ShootsSessionState(
    string WorkOrderId,
    string PlanHash,
    RoutingStatus LastStatus,
    int TraceEntries);

public sealed class ShootsClient
{
    private readonly HostRunCoordinator _coordinator;
    private readonly LocalModelCatalog _modelCatalog;
    private readonly Dictionary<string, BuildPlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionEnvelope> _sessions = new(StringComparer.Ordinal);
    private readonly ShootsClientOptions _options;

    public ShootsClient(ShootsClientOptions? options = null)
    {
        _options = options ?? new ShootsClientOptions();
        _coordinator = new HostRunCoordinator(new NullToolRegistry(), new ToggleDecisionProvider(), new SuccessfulProviderClient());
        var modelPath = _options.StateRoot is null
            ? null
            : Path.Combine(_options.StateRoot, "models.catalog.json");
        _modelCatalog = new LocalModelCatalog(modelPath);
    }

    public Task<WorkOrder> CreateWorkOrderAsync(string originalRequest, string intent, IReadOnlyList<string> constraints, IReadOnlyList<string> requestedArtifacts)
    {
        var id = $"wo-{Hash($"{originalRequest}|{intent}|{string.Join("|", constraints)}")[..12]}";
        var workOrder = new WorkOrder(new WorkOrderId(id), originalRequest, intent, constraints, requestedArtifacts);
        return Task.FromResult(workOrder);
    }

    public Task<BuildPlan> PreviewPlanAsync(WorkOrder workOrder)
    {
        var request = new BuildRequest(
            workOrder,
            "client.preview",
            new Dictionary<string, object?> { ["model"] = ResolveEffectiveModel(_options.SelectedModelId).ModelId },
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "finish" }),
                new RouteRule("finish", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var steps = new BuildStep[]
        {
            new RouteStep("select", "Select tool", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("finish", "Finish", "finish", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        };

        var planHash = Hash($"{workOrder.Id.Value}|{request.CommandId}|{ResolveEffectiveModel(_options.SelectedModelId).ModelId}");
        var plan = new BuildPlan(planHash, request, "g", "n", "e", new DelegationAuthority(new ProviderId("local"), ProviderKind.Local, "client", true), steps, Array.Empty<BuildArtifact>());
        _plans[workOrder.Id.Value] = plan;
        return Task.FromResult(plan);
    }

    public Task<ExecutionEnvelope> RunAsync(string workOrderId)
    {
        var plan = ResolvePlan(workOrderId);
        var result = _coordinator.Run(plan);
        _sessions[workOrderId] = result;
        return Task.FromResult(result);
    }

    public Task<ExecutionEnvelope> ResumeAsync(string workOrderId, HostResumeIntent intent)
    {
        var plan = ResolvePlan(workOrderId);
        var last = _sessions.GetValueOrDefault(workOrderId);
        if (last?.Waiting is null)
            return Task.FromResult(_coordinator.Run(plan));

        var req = new DecisionInjectionRequest(
            workOrderId,
            last.Waiting.PlanHash,
            last.Waiting.RouteGateId,
            last.Waiting.AllowedNextNodes.FirstOrDefault() ?? "select",
            "{}");

        var envelope = _coordinator.Resume(plan, req, intent);

        _sessions[workOrderId] = envelope;
        return Task.FromResult(envelope);
    }

    public Task<Shoots.Contracts.Core.ToolCatalogSnapshot> GetToolCatalogAsync(string workOrderId)
    {
        var plan = ResolvePlan(workOrderId);
        var specs = plan.Steps.OfType<ToolBuildStep>()
            .Select(step => new ToolSpec(step.ToolId, "Client tool", new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None), Array.Empty<ToolInputSpec>(), Array.Empty<ToolOutputSpec>(), Array.Empty<string>()))
            .ToList();
        return Task.FromResult(new Shoots.Contracts.Core.ToolCatalogSnapshot(Hash(string.Join("|", specs.Select(s => s.ToolId.Value))), specs));
    }

    public Task<IReadOnlyList<ModelDescriptor>> GetModelCatalogAsync()
        => Task.FromResult<IReadOnlyList<ModelDescriptor>>(_modelCatalog.ListModels());

    public Task<ShootsSessionState?> GetSessionStateAsync(string workOrderId)
    {
        if (!_sessions.TryGetValue(workOrderId, out var envelope))
            return Task.FromResult<ShootsSessionState?>(null);

        return Task.FromResult<ShootsSessionState?>(new ShootsSessionState(workOrderId, envelope.Plan.PlanId, envelope.State.Status, envelope.Trace.Entries.Count));
    }

    private BuildPlan ResolvePlan(string workOrderId)
    {
        if (!_plans.TryGetValue(workOrderId, out var plan))
            throw new InvalidOperationException($"Unknown workorder: {workOrderId}");
        return plan;
    }

    private ModelDescriptor ResolveEffectiveModel(string? selectedModelId)
    {
        var models = _modelCatalog.ListModels();
        var selected = models.FirstOrDefault(m => string.Equals(m.ModelId, selectedModelId, StringComparison.Ordinal));
        return selected ?? _modelCatalog.ResolveDefaultModel();
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class NullToolRegistry : IToolRegistry
    {
        private static readonly ToolRegistryEntry Entry = new(
            new ToolSpec(
                new ToolId("tools.sample"),
                "Client sample tool",
                new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.Execute),
                Array.Empty<ToolInputSpec>(),
                Array.Empty<ToolOutputSpec>(),
                Array.Empty<string>()));

        public string CatalogHash => "client";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new[] { Entry };
        public ToolRegistryEntry? GetTool(ToolId toolId) => toolId.Value == Entry.Spec.ToolId.Value ? Entry : null;
        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { Entry };
    }

    private sealed class SuccessfulProviderClient : IProviderClient
    {
        public ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, CancellationToken ct)
        {
            var toolId = envelope.ToolId ?? new ToolId("tools.sample");
            var toolResult = new ToolResult(toolId, new Dictionary<string, object?> { ["status"] = "ok" }, true);
            return ValueTask.FromResult(new ProviderExecutionResult(envelope.RequestId, ProviderExecutionResultKind.ToolExecuted, toolResult, null, null, null));
        }
    }

    private sealed class ToggleDecisionProvider : IAiDecisionProvider
    {
        private int _calls;
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
        {
            _calls++;
            if (_calls == 1)
                return null;
            return new ToolSelectionDecision(new ToolId("tools.sample"), new Dictionary<string, object?>());
        }
    }
}
