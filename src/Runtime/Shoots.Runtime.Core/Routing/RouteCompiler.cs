namespace Shoots.Runtime.Core.Routing;

public sealed class RouteCompiler
{
    private static readonly IReadOnlyDictionary<RouteNodeKind, ISet<string>> AllowedMetadataByKind =
        new Dictionary<RouteNodeKind, ISet<string>>
        {
            [RouteNodeKind.SelectTool] = new HashSet<string>(new[] { "command" }, StringComparer.Ordinal),
            [RouteNodeKind.RunTool] = new HashSet<string>(new[] { "tool" }, StringComparer.Ordinal),
            [RouteNodeKind.Emit] = new HashSet<string>(new[] { "text" }, StringComparer.Ordinal),
            [RouteNodeKind.Branch] = new HashSet<string>(new[] { "condition" }, StringComparer.Ordinal),
            [RouteNodeKind.End] = new HashSet<string>(Array.Empty<string>(), StringComparer.Ordinal)
        };

    public RouteDefinition Compile(MermaidGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var edgeLookup = graph.Edges
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RouteEdge>)group
                    .Select(edge => new RouteEdge(edge.ToNodeId, edge.ConditionLabel))
                    .OrderBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Predicate ?? string.Empty, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        var orderedNodes = graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var steps = new List<RouteStep>(orderedNodes.Length);

        for (var index = 0; index < orderedNodes.Length; index++)
        {
            var node = orderedNodes[index];
            var kindAndMetadata = ParseKindAndMetadata(node.Label);
            var kind = kindAndMetadata.Kind;
            var metadata = kindAndMetadata.Metadata;

            ValidateMetadata(kind, metadata, node.Id);

            var outgoing = edgeLookup.TryGetValue(node.Id, out var routeEdges)
                ? routeEdges
                : Array.Empty<RouteEdge>();

            ValidateStructure(kind, node.Id, outgoing);

            var stepId = $"{node.Id}:{index:D4}";
            steps.Add(new RouteStep(stepId, node.Id, kind, metadata, outgoing));
        }

        return new RouteDefinition(steps);
    }

    private static void ValidateStructure(RouteNodeKind kind, string nodeId, IReadOnlyList<RouteEdge> outgoing)
    {
        if (kind == RouteNodeKind.End && outgoing.Count > 0)
            throw new RouteCompilerException("route.schema_invalid", $"End node '{nodeId}' cannot have outgoing edges.");

        if (kind == RouteNodeKind.SelectTool && outgoing.Count == 0)
            throw new RouteCompilerException("route.schema_invalid", $"SelectTool node '{nodeId}' must have at least one outgoing edge.");
    }

    private static void ValidateMetadata(RouteNodeKind kind, IReadOnlyDictionary<string, string> metadata, string nodeId)
    {
        var allowed = AllowedMetadataByKind[kind];
        foreach (var key in metadata.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!allowed.Contains(key))
                throw new RouteCompilerException("route.schema_invalid", $"Node '{nodeId}' contains unsupported metadata key '{key}'.");
        }
    }

    private static (RouteNodeKind Kind, IReadOnlyDictionary<string, string> Metadata) ParseKindAndMetadata(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new RouteCompilerException("route.schema_invalid", "Node label must include route node kind.");

        var tokens = label
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (tokens.Length == 0)
            throw new RouteCompilerException("route.schema_invalid", "Node label must include route node kind.");

        if (!Enum.TryParse<RouteNodeKind>(tokens[0], ignoreCase: true, out var kind))
            throw new RouteCompilerException("route.schema_invalid", $"Unknown route node kind '{tokens[0]}'.");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < tokens.Length; i++)
        {
            var entry = tokens[i];
            var split = entry.IndexOf('=');
            if (split <= 0 || split == entry.Length - 1)
                throw new RouteCompilerException("route.schema_invalid", $"Metadata token '{entry}' is not valid.");

            var key = entry[..split].Trim();
            var value = entry[(split + 1)..].Trim();
            if (metadata.ContainsKey(key))
                throw new RouteCompilerException("route.schema_invalid", $"Duplicate metadata key '{key}'.");

            metadata[key] = value;
        }

        return (kind, metadata);
    }
}
