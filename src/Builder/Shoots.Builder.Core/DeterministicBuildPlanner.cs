using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Builder.Core;

public sealed class DeterministicBuildPlanner : IBuildPlanner
{
    private const string GraphArgKey = MermaidPlanGraph.GraphArgKey;
    private readonly IRuntimeServices _services;
    private readonly IDelegationPolicy _policy;

    public DeterministicBuildPlanner(
        IRuntimeServices services,
        IDelegationPolicy policy)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public BuildPlan Plan(BuildRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.WorkOrder is null)
            throw new ArgumentException("work order is required", nameof(request));
        if (request.Args is null)
            throw new ArgumentException("args are required", nameof(request));
        if (request.RouteRules is null)
            throw new ArgumentException("route rules are required", nameof(request));

        if (!request.Args.TryGetValue(GraphArgKey, out var graphValue))
            throw new ArgumentException($"missing required '{GraphArgKey}'", nameof(request));

        if (graphValue is not string graphText)
            throw new ArgumentException($"'{GraphArgKey}' must be a string", nameof(request));

        var normalizedGraph = MermaidPlanGraph.Normalize(graphText);
        var graph = MermaidPlanGraph.ParseGraph(normalizedGraph);
        var orderedStepIds = MermaidPlanGraph.OrderStepIds(normalizedGraph);

        // Normalize command
        var normalizedCommandId = request.CommandId.Trim();

        // Normalize args deterministically:
        // - case-insensitive keys
        // - stable ordering
        // - no mutation of the request's original dictionary
        var normalizedArgs = new SortedDictionary<string, object?>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in request.Args)
            normalizedArgs[kvp.Key] = kvp.Value;

        normalizedArgs[GraphArgKey] = normalizedGraph;

        if (string.IsNullOrWhiteSpace(request.WorkOrder.Id.Value))
            throw new ArgumentException("work order id is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorkOrder.OriginalRequest))
            throw new ArgumentException("work order original request is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorkOrder.Goal))
            throw new ArgumentException("work order goal is required", nameof(request));
        if (request.WorkOrder.Constraints is null)
            throw new ArgumentException("work order constraints are required", nameof(request));
        if (request.WorkOrder.SuccessCriteria is null)
            throw new ArgumentException("work order success criteria are required", nameof(request));

        var normalizedRouteRules = request.RouteRules
            .OrderBy(rule => rule.NodeId, StringComparer.Ordinal)
            .ThenBy(rule => rule.Intent)
            .ThenBy(rule => rule.Owner)
            .ThenBy(rule => rule.AllowedOutputKind, StringComparer.Ordinal)
            .ToList();

        // Deterministic steps
        var stepsById = new Dictionary<string, BuildStep>(StringComparer.Ordinal);
        var routeRulesByNode = normalizedRouteRules.ToDictionary(
            rule => rule.NodeId,
            rule => rule,
            StringComparer.Ordinal);

        var spec = _services.GetCommand(normalizedCommandId);

        if (spec is null)
            throw new InvalidOperationException($"unknown command '{normalizedCommandId}'");

        var startNodes = MermaidPlanGraph.GetStartNodes(graph);
        if (startNodes.Count != 1)
            throw new InvalidOperationException($"graph must have exactly one start node (found {startNodes.Count}).");

        var terminalNodes = MermaidPlanGraph.GetTerminalNodes(graph);

        foreach (var nodeId in orderedStepIds)
        {
            if (!routeRulesByNode.TryGetValue(nodeId, out var rule))
                continue;

            var allowedNextNodes = graph.Adjacency.TryGetValue(nodeId, out var adjacency)
                ? adjacency
                : Array.Empty<string>();

            var nodeKind = ResolveNodeKind(nodeId, startNodes, allowedNextNodes);

            stepsById[nodeId] = new RouteStep(
                Id: nodeId,
                Description: $"Route '{nodeId}' intent={rule.Intent} owner={rule.Owner}.",
                NodeId: nodeId,
                Intent: rule.Intent,
                Owner: rule.Owner,
                WorkOrderId: request.WorkOrder.Id
            );

            routeRulesByNode[nodeId] = rule with
            {
                NodeKind = nodeKind,
                AllowedNextNodes = allowedNextNodes
            };
        }

        var unknownRouteNodes = routeRulesByNode.Keys
            .Where(nodeId => !orderedStepIds.Contains(nodeId, StringComparer.Ordinal))
            .ToArray();
        if (unknownRouteNodes.Length > 0)
            throw new InvalidOperationException($"route rules reference unknown nodes: {string.Join(", ", unknownRouteNodes)}");

        var unknownGraphSteps = orderedStepIds
            .Where(stepId => !stepsById.ContainsKey(stepId))
            .ToArray();
        if (unknownGraphSteps.Length > 0)
            throw new InvalidOperationException($"graph references unknown steps: {string.Join(", ", unknownGraphSteps)}");

        var missingIntents = Enum.GetValues<RouteIntent>()
            .Where(intent => !routeRulesByNode.Values.Any(rule => rule.Intent == intent))
            .ToArray();
        if (missingIntents.Length > 0)
            throw new InvalidOperationException($"route rules missing required intents: {string.Join(", ", missingIntents)}");

        var hasTerminalIntent = terminalNodes.Any(
            nodeId => routeRulesByNode.TryGetValue(nodeId, out var rule) && rule.Intent == RouteIntent.Terminate);
        if (!hasTerminalIntent)
            throw new InvalidOperationException("graph must include at least one terminal node with Terminate intent.");

        foreach (var rule in routeRulesByNode.Values)
        {
            if (rule.Intent == RouteIntent.Terminate && rule.AllowedNextNodes.Count > 0)
                throw new InvalidOperationException($"terminate node '{rule.NodeId}' must not have outbound edges.");
        }

        var normalizedRequest = request with
        {
            CommandId = normalizedCommandId,
            Args = normalizedArgs,
            RouteRules = routeRulesByNode.Values
                .OrderBy(rule => rule.NodeId, StringComparer.Ordinal)
                .ThenBy(rule => rule.Intent)
                .ThenBy(rule => rule.Owner)
                .ThenBy(rule => rule.AllowedOutputKind, StringComparer.Ordinal)
                .ToList()
        };

        var steps = orderedStepIds
            .Select(stepId => stepsById[stepId])
            .ToList();

        // Guardrail: steps must not embed execution metadata
        if (steps.Any(s => s.Id.Contains("execute:", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Plan steps must not embed execution metadata.");

        // Stable artifacts list
        var artifacts = new[]
        {
            new BuildArtifact("plan.txt", "Human-readable plan output."),
            new BuildArtifact("plan.json", "Machine-readable plan output."),
            new BuildArtifact("result.json", "Runtime execution result."),
            new BuildArtifact("resolution.json", "Failure classification output.")
        };

        if (string.IsNullOrWhiteSpace(_policy.PolicyId))
            throw new InvalidOperationException("Delegation policy id is required.");

        // Provisional plan for delegation decision (authority seed)
        var provisionalAuthority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: _policy.PolicyId,
            AllowsDelegation: false
        );

        var provisionalPlan = new BuildPlan(
            PlanId: string.Empty,
            Request: normalizedRequest,
            Authority: provisionalAuthority,
            Steps: steps,
            Artifacts: artifacts
        );

        // Delegation decision (pure)
        var decision = _policy.Decide(normalizedRequest, provisionalPlan);

        // Deterministic hash (single authority)
        var planId = BuildPlanHasher.ComputePlanId(normalizedRequest, decision.Authority, steps, artifacts);

        return new BuildPlan(
            PlanId: planId,
            Request: normalizedRequest,
            Authority: decision.Authority,
            Steps: steps,
            Artifacts: artifacts
        );
    }

    private static MermaidNodeKind ResolveNodeKind(
        string nodeId,
        IReadOnlyList<string> startNodes,
        IReadOnlyList<string> allowedNextNodes)
    {
        if (startNodes.Contains(nodeId, StringComparer.Ordinal))
            return MermaidNodeKind.Start;

        if (allowedNextNodes.Count == 0)
            return MermaidNodeKind.Terminate;

        if (allowedNextNodes.Count > 1)
            return MermaidNodeKind.Gate;

        return MermaidNodeKind.Linear;
    }
}
