using System.Collections.ObjectModel;

namespace Shoots.Runtime.Core.Routing;

public sealed record MermaidGraphNode(string Id, string Label, bool IsTerminalShape);

public sealed record MermaidGraphEdge(string FromNodeId, string ToNodeId, string? ConditionLabel);

public sealed class MermaidGraph
{
    public MermaidGraph(IReadOnlyList<MermaidGraphNode> nodes, IReadOnlyList<MermaidGraphEdge> edges)
    {
        Nodes = new ReadOnlyCollection<MermaidGraphNode>(nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray());
        Edges = new ReadOnlyCollection<MermaidGraphEdge>(
            edges
                .OrderBy(edge => edge.FromNodeId, StringComparer.Ordinal)
                .ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal)
                .ThenBy(edge => edge.ConditionLabel ?? string.Empty, StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<MermaidGraphNode> Nodes { get; }

    public IReadOnlyList<MermaidGraphEdge> Edges { get; }
}

public sealed class MermaidGraphParseException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record MermaidGraphDiagnostic(string Code, string Message);

public sealed class MermaidGraphValidationResult
{
    public MermaidGraphValidationResult(IReadOnlyList<MermaidGraphDiagnostic> diagnostics)
    {
        Diagnostics = new ReadOnlyCollection<MermaidGraphDiagnostic>(
            diagnostics
                .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<MermaidGraphDiagnostic> Diagnostics { get; }

    public bool IsValid => Diagnostics.Count == 0;
}

public sealed class GraphValidatorOptions
{
    public int MaxNodes { get; init; } = 256;

    public int MaxEdges { get; init; } = 512;

    public bool AllowCycles { get; init; }
}

public sealed class MermaidGraphParserOptions
{
    public bool NormalizeNodeIdsToLowerInvariant { get; init; }
}
