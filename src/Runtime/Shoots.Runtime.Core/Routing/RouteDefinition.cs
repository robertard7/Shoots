using System.Collections.ObjectModel;

namespace Shoots.Runtime.Core.Routing;

public enum RouteNodeKind
{
    SelectTool,
    RunTool,
    Emit,
    Branch,
    End
}

public sealed record RouteEdge(string TargetNodeId, string? Predicate);

public sealed class RouteStep
{
    public RouteStep(
        string stepId,
        string nodeId,
        RouteNodeKind kind,
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<RouteEdge> outgoingEdges)
    {
        StepId = stepId;
        NodeId = nodeId;
        Kind = kind;
        Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata, StringComparer.Ordinal));
        OutgoingEdges = new ReadOnlyCollection<RouteEdge>(
            outgoingEdges
                .OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Predicate ?? string.Empty, StringComparer.Ordinal)
                .ToArray());
    }

    public string StepId { get; }

    public string NodeId { get; }

    public RouteNodeKind Kind { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public IReadOnlyList<RouteEdge> OutgoingEdges { get; }
}

public sealed class RouteDefinition
{
    public RouteDefinition(IReadOnlyList<RouteStep> steps)
    {
        Steps = new ReadOnlyCollection<RouteStep>(
            steps
                .OrderBy(step => step.NodeId, StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<RouteStep> Steps { get; }
}

public sealed class RouteCompilerException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
