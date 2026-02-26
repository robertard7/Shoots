using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Shoots.Runtime.Core.Routing;

public sealed class PlannerRequest
{
    public PlannerRequest(string commandId, IReadOnlyDictionary<string, string>? inputs = null, PlannerLimits? limits = null)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            throw new PlannerKernelException("plan.invalid_request", "CommandId is required.");

        CommandId = commandId;
        Inputs = new ReadOnlyDictionary<string, string>(
            (inputs ?? new Dictionary<string, string>(StringComparer.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        Limits = limits ?? PlannerLimits.Default;
    }

    public string CommandId { get; }

    public IReadOnlyDictionary<string, string> Inputs { get; }

    public PlannerLimits Limits { get; }
}

public sealed class PlannerLimits
{
    public static PlannerLimits Default { get; } = new();

    public int MaxSteps { get; init; } = 64;

    public int MaxArtifacts { get; init; } = 32;

    public int MaxEmittedTextBytes { get; init; } = 8192;
}

public sealed record PlanDiagnostic(string Code, string Message);

public sealed record PlanArtifact(string ArtifactId, string Content);

public sealed class PlanStep
{
    public PlanStep(
        string stepId,
        string nodeId,
        RouteNodeKind nodeKind,
        string? toolId,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyList<RouteEdge> outgoing)
    {
        StepId = stepId;
        NodeId = nodeId;
        NodeKind = nodeKind;
        ToolId = toolId;
        Bindings = new ReadOnlyDictionary<string, string>(
            bindings
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        Outgoing = new ReadOnlyCollection<RouteEdge>(
            outgoing
                .OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Predicate ?? string.Empty, StringComparer.Ordinal)
                .ToArray());
    }

    public string StepId { get; }

    public string NodeId { get; }

    public RouteNodeKind NodeKind { get; }

    public string? ToolId { get; }

    public IReadOnlyDictionary<string, string> Bindings { get; }

    public IReadOnlyList<RouteEdge> Outgoing { get; }
}

public sealed class BuildPlan
{
    public BuildPlan(IReadOnlyList<PlanStep> steps, IReadOnlyList<PlanArtifact> artifacts, IReadOnlyList<PlanDiagnostic> diagnostics, string planId)
    {
        Steps = new ReadOnlyCollection<PlanStep>(steps.ToArray());
        Artifacts = new ReadOnlyCollection<PlanArtifact>(artifacts.ToArray());
        Diagnostics = new ReadOnlyCollection<PlanDiagnostic>(
            diagnostics
                .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
                .ToArray());
        PlanId = planId;
    }

    public IReadOnlyList<PlanStep> Steps { get; }

    public IReadOnlyList<PlanArtifact> Artifacts { get; }

    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; }

    public string PlanId { get; }

    public static string ComputePlanId(PlannerRequest request, RouteDefinition route)
    {
        var builder = new StringBuilder();
        builder.Append(request.CommandId).Append('\n');

        foreach (var input in request.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(input.Key).Append('=').Append(input.Value).Append('\n');

        foreach (var step in route.Steps.OrderBy(step => step.NodeId, StringComparer.Ordinal))
        {
            builder.Append(step.StepId).Append('|').Append(step.NodeId).Append('|').Append(step.Kind).Append('\n');
            foreach (var metadata in step.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                builder.Append("meta:").Append(metadata.Key).Append('=').Append(metadata.Value).Append('\n');
            foreach (var edge in step.OutgoingEdges.OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal).ThenBy(edge => edge.Predicate ?? string.Empty, StringComparer.Ordinal))
                builder.Append("edge:").Append(edge.TargetNodeId).Append('|').Append(edge.Predicate ?? string.Empty).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class PlannerKernelException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record ToolSelectionDecision(string ToolId, IReadOnlyDictionary<string, string> Bindings);

public sealed record ToolDescriptor(string ToolId, IReadOnlyList<string> AllowedBindingKeys);

public interface IPlannerDecisionProvider
{
    ToolSelectionDecision SelectTool(PlannerRequest request, RouteStep step);
}

public interface IPlannerToolCatalog
{
    bool TryGetTool(string toolId, out ToolDescriptor descriptor);
}
