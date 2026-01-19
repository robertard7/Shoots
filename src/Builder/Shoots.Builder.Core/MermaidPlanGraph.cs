using System;
using System.Collections.Generic;
using System.Linq;

namespace Shoots.Builder.Core;

internal static class MermaidPlanGraph
{
    internal const string GraphArgKey = "plan.graph";

    public static string Normalize(string graphText)
    {
        if (graphText is null)
            throw new ArgumentNullException(nameof(graphText));

        return graphText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }

    public static GraphDefinition ParseGraph(string graphText)
    {
        if (string.IsNullOrWhiteSpace(graphText))
            throw new ArgumentException("graph is required", nameof(graphText));

        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<(string From, string To)>();

        foreach (var segment in SplitSegments(graphText))
        {
            if (segment.StartsWith("graph ", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("flowchart ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (segment.Contains("-->", StringComparison.Ordinal))
            {
                var chain = segment.Split(new[] { "-->" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(token => token.Trim())
                    .Where(token => token.Length > 0)
                    .ToArray();

                if (chain.Length < 2)
                    continue;

                for (var i = 0; i < chain.Length - 1; i++)
                {
                    var from = chain[i];
                    var to = chain[i + 1];
                    nodes.Add(from);
                    nodes.Add(to);
                    edges.Add((from, to));
                }

                continue;
            }

            nodes.Add(segment);
        }

        if (nodes.Count == 0)
            throw new InvalidOperationException("graph must contain at least one node");

        var adjacency = BuildAdjacency(nodes, edges);
        return new GraphDefinition(nodes.ToArray(), edges, adjacency);
    }

    public static IReadOnlyList<string> OrderStepIds(string graphText)
    {
        var graph = ParseGraph(graphText);
        return TopologicalOrder(graph.Nodes, graph.Edges);
    }

    public static IReadOnlyList<string> GetStartNodes(GraphDefinition graph)
    {
        if (graph is null)
            throw new ArgumentNullException(nameof(graph));

        var indegree = graph.Nodes.ToDictionary(node => node, _ => 0, StringComparer.Ordinal);
        foreach (var (from, to) in graph.Edges)
        {
            if (!indegree.ContainsKey(from) || !indegree.ContainsKey(to))
                continue;

            indegree[to] = indegree[to] + 1;
        }

        return indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .OrderBy(node => node, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> GetTerminalNodes(GraphDefinition graph)
    {
        if (graph is null)
            throw new ArgumentNullException(nameof(graph));

        var outbound = graph.Nodes.ToDictionary(node => node, _ => 0, StringComparer.Ordinal);
        foreach (var (from, to) in graph.Edges)
        {
            if (!outbound.ContainsKey(from) || !outbound.ContainsKey(to))
                continue;

            outbound[from] = outbound[from] + 1;
        }

        return outbound
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .OrderBy(node => node, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> SplitSegments(string graphText)
    {
        foreach (var line in graphText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            foreach (var segment in trimmed.Split(';'))
            {
                var token = segment.Trim();
                if (token.Length > 0)
                    yield return token;
            }
        }
    }

    private static IReadOnlyList<string> TopologicalOrder(
        IReadOnlyCollection<string> nodes,
        IReadOnlyCollection<(string From, string To)> edges)
    {
        var adjacency = nodes.ToDictionary(node => node, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var indegree = nodes.ToDictionary(node => node, _ => 0, StringComparer.Ordinal);

        foreach (var (from, to) in edges)
        {
            if (!adjacency[from].Add(to))
                continue;

            indegree[to] = indegree[to] + 1;
        }

        var ready = new SortedSet<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var ordered = new List<string>(nodes.Count);

        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            ordered.Add(current);

            foreach (var next in adjacency[current])
            {
                indegree[next] -= 1;
                if (indegree[next] == 0)
                    ready.Add(next);
            }
        }

        if (ordered.Count != nodes.Count)
            throw new InvalidOperationException("graph contains a cycle");

        return ordered;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildAdjacency(
        IEnumerable<string> nodes,
        IEnumerable<(string From, string To)> edges)
    {
        var adjacency = nodes.ToDictionary(
            node => node,
            _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var (from, to) in edges)
        {
            if (!adjacency.TryGetValue(from, out var targets))
                continue;

            targets.Add(to);
        }

        return adjacency.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    internal sealed record GraphDefinition(
        IReadOnlyList<string> Nodes,
        IReadOnlyList<(string From, string To)> Edges,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Adjacency);
}
