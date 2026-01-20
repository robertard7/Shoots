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
	private readonly bool _isReplay;

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
		_isReplay = trace is not null;

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
		if (State.Status is RoutingStatus.Completed or RoutingStatus.Halted)
			return new RoutingLoopResult(
				State,
				_toolResults.ToArray(),
				_traceBuilder.Build(),
				_traceBuilder.BuildTelemetry());

		var previousNarrator = RouteGate.Narrator;

		// Only attach tracing narrator for fresh runs.
		// Replay must not append new trace entries.
		if (!_isReplay)
			RouteGate.Narrator = _tracingNarrator;

		try
		{
			while (true)
			{
				var step = RequireRouteStep(_plan, State);

				ToolSelectionDecision? selection = null;

				// Providers are only invoked when the graph is WAITING and the step requires it.
				// In replay, the trace should already carry the decision path; provider invocation should not
				// generate new trace entries, but ResolveDecision already gates by State.Status.
				try
				{
					selection = ResolveDecision(step);
				}
				catch (Exception ex)
				{
					// Provider failures halt deterministically.
					var failure = ProviderFailure.FromException(ex);
					var providerError = new RuntimeError("internal_error", "Provider failure.", failure.Message);
					var detail = BuildProviderFailureDetail(failure);

					// This is an actual error condition; even on replay, halting is allowed.
					_traceBuilder.Add(RoutingTraceEventKind.Error, detail, state: State, step: step, error: providerError);

					State = State with { Status = RoutingStatus.Halted };
					_tracingNarrator.OnHalted(State, providerError);
					break;
				}

				var decision = selection is null ? null : new RouteDecision(null, selection);

				var advanced = RouteGate.TryAdvance(
					_plan,
					State,
					decision,
					_registry,
					out var nextState,
					out var routeError);

				State = nextState;

				if (!advanced)
				{
					if (!_isReplay && decision is not null && routeError is not null)
					{
						_traceBuilder.Add(
							RoutingTraceEventKind.DecisionRejected,
							detail: routeError.Code,
							state: State,
							step: step,
							error: routeError);
					}

					if (State.Status == RoutingStatus.Waiting)
						continue;

					break;
				}

				if (step.Intent == RouteIntent.SelectTool)
				{
					var invocation =
						step.ToolInvocation
						?? (decision?.ToolSelection is null
							? null
							: new ToolInvocation(
								decision.ToolSelection.ToolId,
								decision.ToolSelection.Bindings,
								State.WorkOrderId));

					if (invocation is not null)
					{
						// FRESH RUN: execute + trace tool events
						if (!_isReplay)
						{
							var envelope = BuildEnvelope();
							var toolDetail = BuildToolExecutionDetail(invocation.ToolId);

							_traceBuilder.Add(
								RoutingTraceEventKind.ToolExecuted,
								detail: toolDetail,
								state: State,
								step: step);

							var result = _toolExecutor.Execute(invocation, envelope);
							_toolResults.Add(result);

							var resultDetail = SerializeToolResult(result);
							_traceBuilder.Add(
								RoutingTraceEventKind.ToolResult,
								detail: resultDetail,
								state: State,
								step: step);

							if (!result.Success)
							{
								var code = ResolveToolFailureCode(result);
								var failure = new RuntimeError(code, $"Tool '{result.ToolId.Value}' failed.");
								State = State with { Status = RoutingStatus.Halted };
								_tracingNarrator.OnHalted(State, failure);
								break;
							}
						}
						else
						{
							// REPLAY: reuse recorded tool results, do not execute, do not trace
							var recorded = RequireRecordedToolResult(invocation.ToolId);

							// IMPORTANT: hydrate execution memory during replay
							_toolResults.Add(recorded);

							if (!recorded.Success)
							{
								var code = ResolveToolFailureCode(recorded);
								var failure = new RuntimeError(code, $"Tool '{recorded.ToolId.Value}' failed.");
								State = State with { Status = RoutingStatus.Halted };
								_tracingNarrator.OnHalted(State, failure);
								break;
							}
						}
					}
				}

				if (State.Status is RoutingStatus.Completed or RoutingStatus.Halted)
					break;
			}
		}
		finally
		{
			RouteGate.Narrator = previousNarrator;
		}

		return new RoutingLoopResult(
			State,
			_toolResults.ToArray(),
			_traceBuilder.Build(),
			_traceBuilder.BuildTelemetry());
	}

	private ToolSelectionDecision? ResolveDecision(RouteStep step)
	{
		if (State.Status != RoutingStatus.Waiting)
			return null;

		if (step.Intent != RouteIntent.SelectTool || step.Owner != DecisionOwner.Ai)
			return null;

		// IMPORTANT:
		// Runtime snapshot uses ToolRegistryEntry, NOT ToolSpec
		var runtimeEntries = _registry
			.GetSnapshot()
			.OrderBy(e => e.Spec.ToolId.Value, StringComparer.Ordinal)
			.ToArray();

		var runtimeCatalog =
			new Shoots.Runtime.Abstractions.ToolCatalogSnapshot(
				_catalogHash,
				runtimeEntries);

		if (!_isReplay)
		{
			var decisionDetail = BuildProviderDecisionDetail(State.IntentToken);
			_traceBuilder.Add(
				RoutingTraceEventKind.DecisionRequired,
				decisionDetail,
				state: State,
				step: step);
		}

		var request = new AiDecisionRequest(
			_plan.Request.WorkOrder!,
			step,
			_plan.GraphStructureHash,
			_catalogHash,
			runtimeCatalog);

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
		if (result is null)
			throw new ArgumentNullException(nameof(result));

		var parts = new List<string>
		{
			$"tool.id={result.ToolId.Value}",
			result.Success ? "success=true" : "success=false"
		};

		foreach (var kvp in result.Outputs
			.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(kvp.Key))
				continue;

			var value = kvp.Value switch
			{
				null => "null",
				string s => s,
				_ => kvp.Value.ToString() ?? "null"
			};

			parts.Add($"{kvp.Key}={value}");
		}

		return string.Join("|", parts);
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

    private static string BuildProviderDecisionDetail(RouteIntentToken intentToken)
    {
        var decisionId = RouteIntentTokenFactory.ComputeTokenHash(intentToken);
        return $"decision.id={decisionId}|intent={decisionId}|provider.invoked";
    }

    private static string BuildProviderFailureDetail(ProviderFailure failure)
    {
        var provider = string.IsNullOrWhiteSpace(failure.ProviderId) ? "unknown" : failure.ProviderId;
        return $"provider.failure|kind={failure.Kind}|provider={provider}";
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

	private ToolResult RequireRecordedToolResult(ToolId toolId)
	{
		var trace = _traceBuilder.Build();
		var entries = trace.Entries;

		for (var i = 0; i < entries.Count; i++)
		{
			var e = entries[i];

			if (e.Event != RoutingTraceEventKind.ToolExecuted || string.IsNullOrWhiteSpace(e.Detail))
				continue;

			// ToolExecuted detail is already "tool.id=..."
			if (!e.Detail.Contains($"tool.id={toolId.Value}", StringComparison.Ordinal))
				continue;

			// The next ToolResult after this ToolExecuted is the one we need
			for (var j = i + 1; j < entries.Count; j++)
			{
				var r = entries[j];
				if (r.Event != RoutingTraceEventKind.ToolResult)
					continue;

				if (string.IsNullOrWhiteSpace(r.Detail))
					break; // malformed trace segment

				return ToolResultParser.Parse(r.Detail, toolId);
			}

			break;
		}

		throw new InvalidOperationException($"Replay is missing tool result for '{toolId.Value}'.");
	}

	private static class ToolResultParser
	{
		public static ToolResult Parse(string detail, ToolId toolId)
		{
			var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
			var success = false;

			foreach (var token in detail.Split('|', StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = token.Split('=', 2);
				if (parts.Length != 2)
					continue;

				if (string.Equals(parts[0], "tool.id", StringComparison.OrdinalIgnoreCase))
					continue;

				if (string.Equals(parts[0], "success", StringComparison.OrdinalIgnoreCase))
				{
					success = string.Equals(parts[1], "true", StringComparison.OrdinalIgnoreCase);
					continue;
				}

				outputs[parts[0]] = parts[1] == "null" ? null : parts[1];
			}

			return new ToolResult(toolId, outputs, success);
		}
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
