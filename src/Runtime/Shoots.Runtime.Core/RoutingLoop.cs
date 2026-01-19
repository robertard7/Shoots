using System;
using System.Collections.Generic;
using System.Linq;
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
        _catalogHash = registry.CatalogHash;
        _traceBuilder = new RoutingTraceBuilder(_plan, _catalogHash, trace);
        _tracingNarrator = new TracingRuntimeNarrator(_narrator, _traceBuilder);

        if (_plan.Request.WorkOrder is null)
            throw new ArgumentException("work order is required", nameof(plan));

        if (initialState is not null &&
            !string.Equals(initialState.WorkOrderId.Value, _plan.Request.WorkOrder.Id.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("work order mismatch between plan and state", nameof(initialState));
        }

        if (toolResults is not null)
            _toolResults.AddRange(toolResults);

        if (trace is not null &&
            !string.Equals(trace.Plan.PlanId, _plan.PlanId, StringComparison.Ordinal))
        {
            throw new ArgumentException("trace plan mismatch", nameof(trace));
        }
        if (trace is not null &&
            !string.Equals(trace.CatalogHash, _catalogHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("trace catalog hash mismatch", nameof(trace));
        }

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
                var selection = ResolveDecision(step);
                var decision = selection is null
                    ? null
                    : new RouteDecision(null, selection);

                var advanced = RouteGate.TryAdvance(_plan, State, decision, _registry, out var nextState, out var error);
                State = nextState;

                if (!advanced)
                {
                    if (decision is not null && error is not null)
                        _traceBuilder.Add(RoutingTraceEventKind.DecisionRejected, detail: error.Code, state: State, step: step, error: error);
                    if (State.Status == RoutingStatus.Waiting)
                        continue;
                    break;
                }

                if (step.Intent == RouteIntent.SelectTool)
                {
                    var invocation = step.ToolInvocation
                                     ?? (decision?.ToolSelection is null
                                         ? null
                                         : new ToolInvocation(decision.ToolSelection.ToolId, decision.ToolSelection.Bindings, State.WorkOrderId));

                    if (invocation is not null)
                    {
                        var envelope = BuildEnvelope();
                        var toolDetail = BuildToolExecutionDetail(invocation.ToolId);
                        _traceBuilder.Add(RoutingTraceEventKind.ToolExecuted, detail: toolDetail, state: State, step: step);
                        var result = _toolExecutor.Execute(invocation, envelope);
                        _toolResults.Add(result);
                        var resultDetail = SerializeToolResult(result);
                        _traceBuilder.Add(RoutingTraceEventKind.ToolResult, detail: resultDetail, state: State, step: step);

                        if (!result.Success)
                        {
                            var code = ResolveToolFailureCode(result);
                            var failure = new RuntimeError(code, $"Tool '{result.ToolId.Value}' failed.");
                            State = State with { Status = RoutingStatus.Halted };
                            _tracingNarrator.OnHalted(State, failure);
                            break;
                        }
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

        var rule = _plan.Request.RouteRules
            .FirstOrDefault(candidate => string.Equals(candidate.NodeId, step.NodeId, StringComparison.Ordinal));
        var allowedNextNodes = rule?.AllowedNextNodes ?? Array.Empty<string>();
        var snapshot = _registry.GetSnapshot()
            .Select(entry => entry.Spec)
            .OrderBy(spec => spec.ToolId.Value, StringComparer.Ordinal)
            .ToArray();
        var catalog = new ToolCatalogSnapshot(_catalogHash, snapshot);
        var request = new AiDecisionRequest(
            _plan.Request.WorkOrder!,
            step,
            _plan.GraphStructureHash,
            _catalogHash,
            allowedNextNodes,
            catalog);
        return _aiDecisionProvider.RequestDecision(request);
    }

    private ExecutionEnvelope BuildEnvelope()
    {
        var telemetry = _traceBuilder.BuildTelemetry();
        var artifacts = BuildArtifacts();
        return new ExecutionEnvelope(
            _plan,
            State,
            _toolResults.ToArray(),
            artifacts,
            _traceBuilder.Build(),
            telemetry,
            _catalogHash,
            ResolveFinalStatus(State));
    }

    private IReadOnlyList<BuildArtifact> BuildArtifacts()
    {
        var artifacts = new List<BuildArtifact>(_plan.Artifacts);
        var index = 0;

        foreach (var result in _toolResults)
        {
            foreach (var output in result.Outputs
                         .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                var id = $"{result.ToolId.Value}.output.{index}.{output.Key}";
                var description = $"Tool output {output.Key}={output.Value?.ToString() ?? "null"}";
                artifacts.Add(new BuildArtifact(id, description));
                index++;
            }
        }

        return artifacts;
    }

    private static string SerializeToolResult(ToolResult result)
    {
        var builder = new List<string>
        {
            $"success={result.Success.ToString()}"
        };

        foreach (var output in result.Outputs
                     .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Add($"{output.Key}={output.Value?.ToString() ?? "null"}");
        }

        return string.Join("|", builder);
    }

    private string BuildToolExecutionDetail(ToolId toolId)
    {
        var entry = _registry.GetSnapshot()
            .FirstOrDefault(candidate => candidate.Spec.ToolId == toolId);
        var tags = entry?.Spec.Tags ?? Array.Empty<string>();

        if (tags.Count == 0)
            return $"tool.id={toolId.Value}";

        var tagList = string.Join(",", tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));
        return $"tool.id={toolId.Value}|tags={tagList}";
    }

    private static string ResolveToolFailureCode(ToolResult result)
    {
        if (result.Outputs.TryGetValue("error.code", out var value) &&
            value is string code &&
            RuntimeErrorCatalog.IsKnown(code))
        {
            return code;
        }

        return "tool_missing";
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
        if (string.IsNullOrWhiteSpace(state.CurrentNodeId))
            throw new ArgumentException("route node id is required", nameof(state));
        var routeStep = plan.Steps
            .OfType<RouteStep>()
            .FirstOrDefault(step => string.Equals(step.NodeId, state.CurrentNodeId, StringComparison.Ordinal));
        if (routeStep is null)
            throw new ArgumentException("route step is required", nameof(plan));
        return routeStep;
    }
}
