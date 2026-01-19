using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;

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

        var nodes = new Dictionary<string, MermaidNodeKind>(StringComparer.Ordinal);
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
                    var from = ParseNodeToken(chain[i]);
                    var to = ParseNodeToken(chain[i + 1]);
                    RegisterNode(nodes, from);
                    RegisterNode(nodes, to);
                    edges.Add((from.Id, to.Id));
                }

                continue;
            }

            var node = ParseNodeToken(segment);
            RegisterNode(nodes, node);
        }

        if (nodes.Count == 0)
            throw new InvalidOperationException("graph must contain at least one node");

        var nodeDefinitions = nodes
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new NodeDefinition(pair.Key, pair.Value))
            .ToArray();
        var adjacency = BuildAdjacency(nodes.Keys, edges);
        var hashes = ComputeHashes(nodeDefinitions, edges);
        return new GraphDefinition(nodeDefinitions, edges, adjacency, hashes);
    }

    public static IReadOnlyList<string> OrderStepIds(string graphText)
    {
        var graph = ParseGraph(graphText);
        var nodes = graph.Nodes.Select(node => node.Id).ToArray();
        return TopologicalOrder(nodes, graph.Edges);
    }

    public static IReadOnlyList<string> GetStartNodes(GraphDefinition graph)
    {
        if (graph is null)
            throw new ArgumentNullException(nameof(graph));

        return graph.Nodes
            .Where(node => node.Kind == MermaidNodeKind.Start)
            .Select(node => node.Id)
            .OrderBy(node => node, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> GetTerminalNodes(GraphDefinition graph)
    {
        if (graph is null)
            throw new ArgumentNullException(nameof(graph));

        return graph.Nodes
            .Where(node => node.Kind == MermaidNodeKind.Terminal)
            .Select(node => node.Id)
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

    private static NodeDefinition ParseNodeToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("graph contains an empty node token");

        var kind = MermaidNodeKind.Route;
        var parts = trimmed.Split(new[] { ":::" }, StringSplitOptions.RemoveEmptyEntries);
        var core = parts[0].Trim();

        if (parts.Length > 1)
            kind = ParseNodeKind(parts[^1].Trim());

        var id = ExtractNodeId(core);
        return new NodeDefinition(id, kind);
    }

    private static MermaidNodeKind ParseNodeKind(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "start" => MermaidNodeKind.Start,
            "route" => MermaidNodeKind.Route,
            "tool" => MermaidNodeKind.Tool,
            "gate" => MermaidNodeKind.Gate,
            "terminal" => MermaidNodeKind.Terminal,
            _ => throw new InvalidOperationException($"unknown node kind annotation '{value}'")
        };
    }

    private static string ExtractNodeId(string token)
    {
        var stopIndex = token.IndexOfAny(new[] { '[', '(', '{', '<' });
        var id = stopIndex >= 0 ? token[..stopIndex] : token;
        id = id.Trim();
        if (id.Length == 0)
            throw new InvalidOperationException("graph node id is required");
        return id;
    }

    private static void RegisterNode(IDictionary<string, MermaidNodeKind> nodes, NodeDefinition node)
    {
        if (nodes.TryGetValue(node.Id, out var existingKind))
        {
            throw new InvalidOperationException($"duplicate node '{node.Id}' detected in Mermaid graph.");
        }

        nodes[node.Id] = node.Kind;
    }

    private static GraphHashes ComputeHashes(
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        var nodeTokens = nodes
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => $"{node.Id}|{node.Kind}")
            .ToArray();
        var edgeTokens = edges
            .OrderBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal)
            .Select(edge => $"{edge.From}->{edge.To}")
            .ToArray();

        var nodeSetHash = HashTools.ComputeSha256Hash(string.Join("|", nodeTokens));
        var edgeSetHash = HashTools.ComputeSha256Hash(string.Join("|", edgeTokens));
        var graphStructureHash = HashTools.ComputeSha256Hash($"{nodeSetHash}|{edgeSetHash}");
        return new GraphHashes(graphStructureHash, nodeSetHash, edgeSetHash);
    }

    internal sealed record GraphDefinition(
        IReadOnlyList<NodeDefinition> Nodes,
        IReadOnlyList<(string From, string To)> Edges,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Adjacency,
        GraphHashes Hashes);

    internal sealed record NodeDefinition(
        string Id,
        MermaidNodeKind Kind);

    internal sealed record GraphHashes(
        string GraphStructureHash,
        string NodeSetHash,
        string EdgeSetHash);
}
