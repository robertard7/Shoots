namespace Shoots.Runtime.Core.Routing;

public sealed class GraphValidator
{
    public MermaidGraphValidationResult Validate(MermaidGraph graph, GraphValidatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var validatorOptions = options ?? new GraphValidatorOptions();
        var diagnostics = new List<MermaidGraphDiagnostic>();

        if (graph.Nodes.Count > validatorOptions.MaxNodes)
        {
            diagnostics.Add(new MermaidGraphDiagnostic(
                "graph.max_nodes_exceeded",
                $"Node count {graph.Nodes.Count} exceeds max {validatorOptions.MaxNodes}."));
        }

        if (graph.Edges.Count > validatorOptions.MaxEdges)
        {
            diagnostics.Add(new MermaidGraphDiagnostic(
                "graph.max_edges_exceeded",
                $"Edge count {graph.Edges.Count} exceeds max {validatorOptions.MaxEdges}."));
        }

        var nodeIds = new HashSet<string>(graph.Nodes.Select(node => node.Id), StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            if (!nodeIds.Contains(edge.FromNodeId))
                diagnostics.Add(new MermaidGraphDiagnostic("graph.unknown_node_ref", $"Edge source '{edge.FromNodeId}' is not declared."));
            if (!nodeIds.Contains(edge.ToNodeId))
                diagnostics.Add(new MermaidGraphDiagnostic("graph.unknown_node_ref", $"Edge target '{edge.ToNodeId}' is not declared."));
        }

        var knownEdges = graph.Edges
            .Where(edge => nodeIds.Contains(edge.FromNodeId) && nodeIds.Contains(edge.ToNodeId))
            .ToArray();

        var hasExplicitStart = nodeIds.Contains("start");
        if (!hasExplicitStart)
        {
            var indegrees = nodeIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
            foreach (var edge in knownEdges)
                indegrees[edge.ToNodeId] += 1;

            var entryCount = indegrees.Count(pair => pair.Value == 0);
            if (entryCount != 1)
            {
                diagnostics.Add(new MermaidGraphDiagnostic(
                    "graph.invalid_entry",
                    $"Graph must have exactly one entry node; found {entryCount}."));
            }
        }

        if (!validatorOptions.AllowCycles && ContainsCycle(nodeIds, knownEdges))
            diagnostics.Add(new MermaidGraphDiagnostic("graph.cycle_detected", "Graph contains at least one cycle."));

        return new MermaidGraphValidationResult(diagnostics);
    }

    private static bool ContainsCycle(IReadOnlyCollection<string> nodeIds, IReadOnlyCollection<MermaidGraphEdge> edges)
    {
        var adjacency = nodeIds.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in edges)
            adjacency[edge.FromNodeId].Add(edge.ToNodeId);

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var nodeId in nodeIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (Visit(nodeId, adjacency, state))
                return true;
        }

        return false;
    }

    private static bool Visit(string nodeId, IReadOnlyDictionary<string, List<string>> adjacency, IDictionary<string, int> state)
    {
        if (state.TryGetValue(nodeId, out var value))
            return value == 1;

        state[nodeId] = 1;
        foreach (var next in adjacency[nodeId].OrderBy(id => id, StringComparer.Ordinal))
        {
            if (Visit(next, adjacency, state))
                return true;
        }

        state[nodeId] = 2;
        return false;
    }
}
