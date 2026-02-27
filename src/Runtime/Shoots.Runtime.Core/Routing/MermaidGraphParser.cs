namespace Shoots.Runtime.Core.Routing;

public sealed class MermaidGraphParser
{
    public MermaidGraph Parse(string graphText, MermaidGraphParserOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(graphText))
            throw new MermaidGraphParseException("graph.parse_empty", "Graph text is required.");

        var parserOptions = options ?? new MermaidGraphParserOptions();
        var nodes = new Dictionary<string, MermaidGraphNode>(StringComparer.Ordinal);
        var edges = new List<MermaidGraphEdge>();

        foreach (var segment in SplitSegments(Normalize(graphText)))
        {
            if (segment.StartsWith("graph ", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("flowchart ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var edgeIndex = segment.IndexOf("-->", StringComparison.Ordinal);
            if (edgeIndex >= 0)
            {
                ParseEdgeSegment(segment, parserOptions, nodes, edges);
                continue;
            }

            var nodeToken = ParseNodeToken(segment, parserOptions);
            if (!nodeToken.IsDeclaration)
                throw new MermaidGraphParseException("graph.invalid_node", $"Node declaration '{segment}' is not valid.");

            RegisterNode(nodes, nodeToken);
        }

        return new MermaidGraph(nodes.Values.ToArray(), edges);
    }

    internal static string Normalize(string graphText) => graphText
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal)
        .Trim();

    private static IEnumerable<string> SplitSegments(string normalized)
    {
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            foreach (var segment in SplitLineSegments(trimmed))
                yield return segment;
        }
    }

    private static IEnumerable<string> SplitLineSegments(string line)
    {
        var segmentStart = 0;
        var squareDepth = 0;
        var roundDepth = 0;
        var braceDepth = 0;

        for (var index = 0; index < line.Length; index++)
        {
            switch (line[index])
            {
                case '[': squareDepth++; break;
                case ']': squareDepth = Math.Max(0, squareDepth - 1); break;
                case '(': roundDepth++; break;
                case ')': roundDepth = Math.Max(0, roundDepth - 1); break;
                case '{': braceDepth++; break;
                case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                case ';' when squareDepth == 0 && roundDepth == 0 && braceDepth == 0:
                    var segment = line[segmentStart..index].Trim();
                    if (segment.Length > 0)
                        yield return segment;
                    segmentStart = index + 1;
                    break;
            }
        }

        var tail = line[segmentStart..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private static void ParseEdgeSegment(
        string segment,
        MermaidGraphParserOptions options,
        IDictionary<string, MermaidGraphNode> nodes,
        ICollection<MermaidGraphEdge> edges)
    {
        var edgeIndex = segment.IndexOf("-->", StringComparison.Ordinal);
        var left = segment[..edgeIndex].Trim();
        var right = segment[(edgeIndex + 3)..].Trim();

        var from = ParseNodeToken(left, options);
        if (from.IsDeclaration)
            RegisterNode(nodes, from);

        string? condition = null;
        if (right.StartsWith('|'))
        {
            var close = right.IndexOf('|', 1);
            if (close <= 1)
                throw new MermaidGraphParseException("graph.invalid_edge", $"Edge label in '{segment}' is not valid.");

            condition = right[1..close].Trim();
            right = right[(close + 1)..].Trim();
        }

        var to = ParseNodeToken(right, options);
        if (to.IsDeclaration)
            RegisterNode(nodes, to);

        if (from.Id.Length == 0 || to.Id.Length == 0)
            throw new MermaidGraphParseException("graph.invalid_edge", $"Edge '{segment}' is not valid.");

        edges.Add(new MermaidGraphEdge(from.Id, to.Id, condition));
    }

    private static void RegisterNode(IDictionary<string, MermaidGraphNode> nodes, ParsedNodeToken parsed)
    {
        if (nodes.ContainsKey(parsed.Id))
            throw new MermaidGraphParseException("graph.duplicate_node", $"Duplicate node id '{parsed.Id}'.");

        nodes[parsed.Id] = new MermaidGraphNode(parsed.Id, parsed.Label, parsed.IsTerminalShape);
    }

    private static ParsedNodeToken ParseNodeToken(string token, MermaidGraphParserOptions options)
    {
        var trimmed = token.Trim();
        if (trimmed.Length == 0)
            throw new MermaidGraphParseException("graph.invalid_node", "Node token is empty.");

        var firstBracket = trimmed.IndexOfAny(['[', '(', '{']);
        if (firstBracket < 0)
            return new ParsedNodeToken(NormalizeId(trimmed, options), trimmed, false, false);

        var id = NormalizeId(trimmed[..firstBracket].Trim(), options);
        var labelAndShape = trimmed[firstBracket..];
        var (label, terminal) = ParseLabelShape(labelAndShape);
        return new ParsedNodeToken(id, label, true, terminal);
    }

    private static string NormalizeId(string id, MermaidGraphParserOptions options)
    {
        if (id.Length == 0)
            throw new MermaidGraphParseException("graph.invalid_node", "Node id is required.");

        return options.NormalizeNodeIdsToLowerInvariant
            ? id.ToLowerInvariant()
            : id;
    }

    private static (string Label, bool Terminal) ParseLabelShape(string token)
    {
        if (token.StartsWith("[[", StringComparison.Ordinal) || token.StartsWith("((", StringComparison.Ordinal))
        {
            if (!token.EndsWith("]]", StringComparison.Ordinal) && !token.EndsWith("))", StringComparison.Ordinal))
                throw new MermaidGraphParseException("graph.invalid_node", $"Node token '{token}' has unmatched delimiters.");

            var value = token[2..^2].Trim();
            return (value, true);
        }

        if (token.StartsWith("[", StringComparison.Ordinal))
        {
            if (!token.EndsWith("]", StringComparison.Ordinal))
                throw new MermaidGraphParseException("graph.invalid_node", $"Node token '{token}' has unmatched delimiters.");

            return (token[1..^1].Trim(), false);
        }

        if (token.StartsWith("(", StringComparison.Ordinal))
        {
            if (!token.EndsWith(")", StringComparison.Ordinal))
                throw new MermaidGraphParseException("graph.invalid_node", $"Node token '{token}' has unmatched delimiters.");

            return (token[1..^1].Trim(), false);
        }

        if (token.StartsWith("{", StringComparison.Ordinal))
        {
            if (!token.EndsWith("}", StringComparison.Ordinal))
                throw new MermaidGraphParseException("graph.invalid_node", $"Node token '{token}' has unmatched delimiters.");

            return (token[1..^1].Trim(), false);
        }

        throw new MermaidGraphParseException("graph.invalid_node", $"Node token '{token}' uses unsupported shape.");
    }

    private sealed record ParsedNodeToken(string Id, string Label, bool IsDeclaration, bool IsTerminalShape);
}
