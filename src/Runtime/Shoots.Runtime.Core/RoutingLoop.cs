using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class RoutingLoop
{
    private readonly BuildPlan _plan;
    private readonly IToolRegistry _registry;
    private readonly IAiDecisionProvider _aiDecisionProvider;
    private readonly IRuntimeNarrator _narrator;
    private readonly IToolExecutor _toolExecutor;
    private readonly List<ToolResult> _toolResults = new();
    private readonly RoutingTraceBuilder _traceBuilder;
    private readonly TracingRuntimeNarrator _tracingNarrator;
    private readonly string _catalogHash;

    public RoutingState State { get; private set; }
    public IReadOnlyList<ToolResult> ToolResults => _toolResults;
    public RoutingTrace Trace => _traceBuilder.Build();

    public RoutingLoop(
        BuildPlan plan,
        IToolRegistry registry,
        IAiDecisionProvider aiDecisionProvider,
        IRuntimeNarrator narrator,
        IToolExecutor toolExecutor,
        RoutingState? initialState = null,
        IReadOnlyList<ToolResult>? toolResults = null,
        RoutingTrace? trace = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _aiDecisionProvider = aiDecisionProvider ?? throw new ArgumentNullException(nameof(aiDecisionProvider));
        _narrator = narrator ?? throw new ArgumentNullException(nameof(narrator));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _traceBuilder = new RoutingTraceBuilder(trace);
        _tracingNarrator = new TracingRuntimeNarrator(_narrator, _traceBuilder);
        _catalogHash = registry.CatalogHash;

        if (_plan.Request.WorkOrder is null)
            throw new ArgumentException("work order is required", nameof(plan));

        if (initialState is not null &&
            !string.Equals(initialState.WorkOrderId.Value, _plan.Request.WorkOrder.Id.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("work order mismatch between plan and state", nameof(initialState));
        }

        if (toolResults is not null)
            _toolResults.AddRange(toolResults);

        State = initialState ?? RoutingState.CreateInitial(_plan);
    }

    public RoutingLoopResult Run()
    {
        if (State.Status == RoutingStatus.Completed || State.Status == RoutingStatus.Halted)
            return new RoutingLoopResult(State, _toolResults.ToArray(), _traceBuilder.Build(), _traceBuilder.BuildTelemetry());

        var previousNarrator = RouteGate.Narrator;
        RouteGate.Narrator = _tracingNarrator;

        try
        {
            while (true)
            {
                var step = RequireRouteStep(_plan, State);
                var decision = ResolveDecision(step);

                if (State.Status == RoutingStatus.Waiting && decision is null)
                    break;

                var advanced = RouteGate.TryAdvance(_plan, State, decision, _registry, out var nextState, out var error);
                State = nextState;

                if (!advanced)
                {
                    if (decision is not null && error is not null)
                        _traceBuilder.Add(RoutingTraceEventKind.DecisionRejected, error.Code, State, step, error);
                    if (State.Status == RoutingStatus.Waiting)
                        continue;
                    break;
                }

                if (step.Intent == RouteIntent.SelectTool)
                {
                    var invocation = step.ToolInvocation
                                     ?? (decision is null
                                         ? null
                                         : new ToolInvocation(decision.ToolId, decision.Bindings, State.WorkOrderId));

                    if (invocation is not null)
                    {
                        var envelope = BuildEnvelope();
                        _traceBuilder.Add(RoutingTraceEventKind.ToolExecuted, invocation.ToolId.Value, State, step);
                        var result = _toolExecutor.Execute(invocation, envelope);
                        _toolResults.Add(result);
                        _traceBuilder.Add(RoutingTraceEventKind.ToolResult, result.Success.ToString(), State, step);
                    }
                }

                if (State.Status == RoutingStatus.Completed || State.Status == RoutingStatus.Halted)
                    break;
            }
        }
        finally
        {
            RouteGate.Narrator = previousNarrator;
        }

        return new RoutingLoopResult(State, _toolResults.ToArray(), _traceBuilder.Build(), _traceBuilder.BuildTelemetry());
    }

    private ToolSelectionDecision? ResolveDecision(RouteStep step)
    {
        if (State.Status != RoutingStatus.Waiting)
            return null;

        var snapshot = _registry.GetSnapshot();
        var envelope = BuildEnvelope();
        var context = new AiDecisionRequestContext(_plan.Request.WorkOrder!, step, State, envelope, snapshot);
        return _aiDecisionProvider.RequestDecision(context);
    }

    private ExecutionEnvelope BuildEnvelope()
    {
        var telemetry = _traceBuilder.BuildTelemetry();
        return new ExecutionEnvelope(
            _plan,
            State,
            _toolResults.ToArray(),
            _traceBuilder.Build(),
            telemetry,
            _catalogHash,
            ResolveFinalStatus(State));
    }

    private static ExecutionFinalStatus ResolveFinalStatus(RoutingState state)
    {
        return state.Status switch
        {
            RoutingStatus.Completed => ExecutionFinalStatus.Completed,
            RoutingStatus.Halted => ExecutionFinalStatus.Halted,
            _ => ExecutionFinalStatus.Aborted
        };
    }
    private static RouteStep RequireRouteStep(BuildPlan plan, RoutingState state)
    {
        if (plan.Steps is null || plan.Steps.Count == 0)
            throw new ArgumentException("route steps are required", nameof(plan));
        if (state.CurrentRouteIndex < 0 || state.CurrentRouteIndex >= plan.Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(state), "route index out of range");
        if (plan.Steps[state.CurrentRouteIndex] is not RouteStep routeStep)
            throw new ArgumentException("route step is required", nameof(plan));
        return routeStep;
    }
}
