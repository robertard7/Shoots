using System.Text;

namespace Shoots.Runtime.Core.Routing;

public sealed class PlannerKernel
{
    public BuildPlan Plan(
        PlannerRequest request,
        RouteDefinition route,
        IPlannerDecisionProvider decisionProvider,
        IPlannerToolCatalog toolCatalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(decisionProvider);
        ArgumentNullException.ThrowIfNull(toolCatalog);

        if (route.Steps.Count == 0)
            throw new PlannerKernelException("route.no_path", "Route definition contains no steps.");

        var limits = request.Limits;
        if (limits.MaxSteps <= 0)
            throw new PlannerKernelException("plan.invalid_request", "MaxSteps must be greater than zero.");

        var byNodeId = route.Steps.ToDictionary(step => step.NodeId, StringComparer.Ordinal);
        var indegree = route.Steps.ToDictionary(step => step.NodeId, _ => 0, StringComparer.Ordinal);

        foreach (var step in route.Steps)
        {
            foreach (var edge in step.OutgoingEdges)
            {
                if (!indegree.ContainsKey(edge.TargetNodeId))
                    throw new PlannerKernelException("route.schema_invalid", $"Outgoing edge targets unknown node '{edge.TargetNodeId}'.");

                indegree[edge.TargetNodeId] += 1;
            }
        }

        var entry = indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (entry is null)
            throw new PlannerKernelException("route.no_path", "Route definition has no entry node.");

        string? currentNodeId = entry;
        var stepCount = 0;
        var emittedTextBytes = 0;
        var steps = new List<PlanStep>();
        var artifacts = new List<PlanArtifact>();
        var diagnostics = new List<PlanDiagnostic>();

        while (currentNodeId is not null)
        {
            if (stepCount >= limits.MaxSteps)
                throw new PlannerKernelException("plan.invalid_request", $"Step cap exceeded at maxSteps={limits.MaxSteps}.");

            if (!byNodeId.TryGetValue(currentNodeId, out var routeStep))
                throw new PlannerKernelException("route.no_path", $"Node '{currentNodeId}' was not found in route definition.");

            var toolId = (string?)null;
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);

            switch (routeStep.Kind)
            {
                case RouteNodeKind.SelectTool:
                    ValidateCommand(request, routeStep);
                    var selection = decisionProvider.SelectTool(request, routeStep);
                    ValidateSelection(selection, toolCatalog);
                    toolId = selection.ToolId;
                    bindings = selection.Bindings
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    break;

                case RouteNodeKind.RunTool:
                    if (!routeStep.Metadata.TryGetValue("tool", out var runToolId))
                        throw new PlannerKernelException("route.schema_invalid", $"RunTool node '{routeStep.NodeId}' missing required metadata key 'tool'.");
                    EnsureCatalogTool(runToolId, toolCatalog);
                    toolId = runToolId;
                    break;

                case RouteNodeKind.Emit:
                    var text = routeStep.Metadata.TryGetValue("text", out var emitText) ? emitText : string.Empty;
                    var bytes = Encoding.UTF8.GetByteCount(text);
                    if (emittedTextBytes + bytes > limits.MaxEmittedTextBytes)
                        throw new PlannerKernelException("plan.invalid_request", $"Emit text cap exceeded at maxEmittedTextBytes={limits.MaxEmittedTextBytes}.");
                    emittedTextBytes += bytes;

                    if (artifacts.Count >= limits.MaxArtifacts)
                        throw new PlannerKernelException("plan.invalid_request", $"Artifact cap exceeded at maxArtifacts={limits.MaxArtifacts}.");

                    artifacts.Add(new PlanArtifact($"emit/{routeStep.StepId}.txt", text));
                    break;

                case RouteNodeKind.Branch:
                    break;

                case RouteNodeKind.End:
                    break;

                default:
                    throw new PlannerKernelException("route.schema_invalid", $"Unknown route node kind '{routeStep.Kind}'.");
            }

            steps.Add(new PlanStep(routeStep.StepId, routeStep.NodeId, routeStep.Kind, toolId, bindings, routeStep.OutgoingEdges));
            stepCount += 1;

            if (routeStep.Kind == RouteNodeKind.End)
                break;

            currentNodeId = routeStep.Kind == RouteNodeKind.Branch
                ? ResolveBranchTarget(routeStep, request)
                : ResolveNextTarget(routeStep);
        }

        var planId = BuildPlan.ComputePlanId(request, route);
        return new BuildPlan(steps, artifacts, diagnostics, planId);
    }

    private static void ValidateCommand(PlannerRequest request, RouteStep routeStep)
    {
        if (!routeStep.Metadata.TryGetValue("command", out var expectedCommand))
            throw new PlannerKernelException("route.schema_invalid", $"SelectTool node '{routeStep.NodeId}' missing required metadata key 'command'.");

        if (!string.Equals(request.CommandId, expectedCommand, StringComparison.Ordinal))
            throw new PlannerKernelException("plan.invalid_request", $"Unknown command '{request.CommandId}'.");
    }

    private static void ValidateSelection(ToolSelectionDecision selection, IPlannerToolCatalog toolCatalog)
    {
        if (string.IsNullOrWhiteSpace(selection.ToolId))
            throw new PlannerKernelException("route.schema_invalid", "Decision provider returned empty tool id.");

        EnsureCatalogTool(selection.ToolId, toolCatalog);

        toolCatalog.TryGetTool(selection.ToolId, out var descriptor);
        var allowed = descriptor!.AllowedBindingKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

        foreach (var key in selection.Bindings.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
                throw new PlannerKernelException("route.schema_invalid", $"Binding key '{key}' is not allowed for tool '{selection.ToolId}'.");
        }
    }

    private static void EnsureCatalogTool(string toolId, IPlannerToolCatalog toolCatalog)
    {
        if (!toolCatalog.TryGetTool(toolId, out _))
            throw new PlannerKernelException("route.schema_invalid", $"Tool '{toolId}' was not found in the catalog.");
    }

    private static string? ResolveNextTarget(RouteStep routeStep)
    {
        if (routeStep.OutgoingEdges.Count == 0)
            return null;

        return routeStep.OutgoingEdges
            .OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Predicate ?? string.Empty, StringComparer.Ordinal)
            .First()
            .TargetNodeId;
    }

    private static string ResolveBranchTarget(RouteStep routeStep, PlannerRequest request)
    {
        var branchValue = request.Inputs.TryGetValue("branch", out var value) ? value : string.Empty;

        var matches = routeStep.OutgoingEdges
            .Where(edge => string.Equals(edge.Predicate ?? string.Empty, branchValue, StringComparison.Ordinal))
            .OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Predicate ?? string.Empty, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            matches = routeStep.OutgoingEdges
                .Where(edge => string.IsNullOrEmpty(edge.Predicate))
                .OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                .ToArray();
        }

        if (matches.Length == 0)
            throw new PlannerKernelException("route.no_path", $"Branch node '{routeStep.NodeId}' has no path for value '{branchValue}'.");

        return matches[0].TargetNodeId;
    }
}
