using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;

namespace Shoots.Runtime.Abstractions
{
    public sealed record RuntimeError(string Code, string Message)
    {
        public static RuntimeError Internal(string message) => new("runtime.internal", message);
    }

    public sealed record RuntimeResult
    {
        public bool Ok { get; init; }
        public object? Output { get; init; }
        public RuntimeError? Error { get; init; }

        public static RuntimeResult Success(object? output = null) => new() { Ok = true, Output = output };
        public static RuntimeResult Fail(RuntimeError error) => new() { Ok = false, Error = error };
    }

    public enum ResumeMode { None = 0, InjectDecision = 1, OverridePlanChange = 2, DiscardWaitingStartOver = 3 }
    public enum DecisionWaitMode { Halt = 0, Fallback = 1 }

    public sealed record RuntimeRunOptions(
        ResumeMode ResumeMode = ResumeMode.None,
        string? InjectedDecisionDigest = null,
        bool DiscardWaiting = false,
        bool AllowPlanChangeOverride = false,
        int MaxDecisionWaits = 1,
        DecisionWaitMode DecisionWaitMode = DecisionWaitMode.Halt);

    public readonly record struct RuntimeVersion(int Major, int Minor, int Patch);

    public enum RoutingTraceEventKind { Plan, Command, Result, Error, Route, WorkOrderReceived, RouteEntered, NodeEntered, DecisionRequired, DecisionAccepted, DecisionGateWaiting, DecisionGateBypassed, DecisionGateRequiredError, StepBudgetExceeded, HostBlockedRerunWaiting, HostResumeOverridePlanChange, HostResumeDiscardWaiting, NodeTransitionChosen, NodeAdvanced, NodeHalted, DecisionRejected, ToolExecuted, ToolResult, Halted, Completed }
    public sealed record RoutingTraceEntry(int Tick, RoutingTraceEventKind Event, string? Detail = null);
    public sealed record RoutingTrace(string PlanId, string CatalogHash, IReadOnlyList<RoutingTraceEntry> Entries);
    public sealed record ExecutionEnvelope(string PlanId, IReadOnlyList<BuildArtifact> Artifacts, RoutingTrace Trace)
    {
        public string GetExecutionId() => PlanId;
    }

    public sealed record ExecutionEnvelopeDto(string PlanId, ExecutionFinalStatus FinalStatus, IReadOnlyList<ToolResultDto> ToolResults, IReadOnlyList<BuildArtifactDto> Artifacts, RoutingTraceDto Trace);
    public sealed record RoutingTraceDto(string PlanId, string CatalogHash, IReadOnlyList<RoutingTraceEntryDto> Entries);
    public sealed record RoutingTraceEntryDto(int Tick, RoutingTraceEventKind Event, string? Detail = null);
    public sealed record ToolResultDto(string ToolId, bool Success, IReadOnlyDictionary<string, object?> Outputs, IReadOnlyList<string> Tags);
    public sealed record BuildArtifactDto(string Id, string Description);
    public sealed record ToolCatalogSnapshot(string Hash, IReadOnlyList<ToolRegistryEntry> Entries);
}

namespace Shoots.Runtime.Ui.Abstractions
{
    using Shoots.Runtime.Abstractions;

    public interface IRuntimeFacade
    {
        Task<RuntimeResult> StartExecution(BuildPlan plan, RuntimeRunOptions? options = null, CancellationToken ct = default);
        Task<IRuntimeStatusSnapshot> QueryStatus(CancellationToken ct = default);
        IAsyncEnumerable<RoutingTraceEntry> SubscribeTrace(CancellationToken ct = default);
        Task CancelExecution(CancellationToken ct = default);
    }

    public interface IRuntimeStatusSnapshot
    {
        RuntimeVersion Version { get; }
        string PolicyHash { get; }
    }

    public interface IHostExecutionService
    {
        WorkOrder CreateWorkOrder(string originalRequest, string intent, IReadOnlyList<string> constraints, IReadOnlyList<string> requestedArtifacts);
        BuildPlan PreviewPlan(BuildRequest request, DelegationAuthority authority, IReadOnlyList<BuildStep> steps, IReadOnlyList<BuildArtifact> artifacts);
        Task<RuntimeResult> RunAsync(BuildPlan plan, RuntimeRunOptions? options = null, CancellationToken ct = default);
        Task<RuntimeResult> ResumeAsync(BuildPlan plan, DecisionInjectionRequest request, HostResumeIntent intent, CancellationToken ct = default);
        Shoots.Contracts.Core.ToolCatalogSnapshot GetToolCatalogSnapshot(BuildPlan plan);
    }

    public enum ToolpackTier { Public, Developer, System }
    public enum ToolpackCapability { FileSystem, Process, Network, Kernel, Build, Deploy }
    public sealed record RoleDescriptor(string Name, string Description, IReadOnlyList<ToolpackCapability> PreferredCapabilities);
    public enum AiIntentType { Explain, Validate, Compare, Diagnose, Predict, Risk, Suggest, Modify }
    public enum AiIntentScope { Blueprint, Planner, Execution, ToolExecution, Provider, UI }
    public sealed record AiIntentDescriptor(AiIntentType Type, AiIntentScope Scope, string? TargetId = null);

    public interface IAiHelpSurface
    {
        string SurfaceId { get; }
        string SurfaceKind { get; }
        IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; }
        string DescribeContext();
        string DescribeCapabilities();
        string DescribeConstraints();
    }

    public sealed record AiHelpScope(string SurfaceId, string? Summary, IReadOnlyDictionary<string, string> Data)
    {
        public AiHelpScope(string surfaceId, string? summary) : this(surfaceId, summary, new Dictionary<string, string>()) { }
    }

    public sealed record AiWorkspaceSnapshot(string? Name, string? RootPath, ToolpackTier Tier, IReadOnlyList<ToolpackCapability> AllowedCapabilities);

    public sealed record AiHelpRequest(
        AiHelpScope Scope,
        AiIntentDescriptor Intent,
        AiWorkspaceSnapshot Workspace,
        BuildPlan? Plan,
        ToolCatalogSnapshot? ToolCatalog,
        string? ExecutionState,
        string? EnvironmentProfile,
        string? LastAppliedProfile,
        RoleDescriptor? Role,
        IReadOnlyList<IAiHelpSurface>? RequestedSurfaces)
    {
        public IReadOnlyList<IAiHelpSurface> Surfaces { get; } = RequestedSurfaces ?? Array.Empty<IAiHelpSurface>();
    }

    public interface IAiHelpFacade
    {
        Task<string> GetContextSummaryAsync(AiHelpRequest request, CancellationToken ct = default);
        Task<string> ExplainStateAsync(AiHelpRequest request, CancellationToken ct = default);
        Task<string> SuggestNextStepsAsync(AiHelpRequest request, CancellationToken ct = default);
    }
}
