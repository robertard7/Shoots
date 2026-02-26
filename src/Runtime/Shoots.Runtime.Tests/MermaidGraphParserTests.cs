using System;
using System.Linq;
using Shoots.Runtime.Core.Routing;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class MermaidGraphParserTests
{
    [Fact]
    public void Parses_simple_graph()
    {
        var parser = new MermaidGraphParser();

        var graph = parser.Parse(
            """
            flowchart TD
                start[Start] --> select[Select]
                select --> end((End))
            """);

        Assert.Equal(new[] { "end", "select", "start" }, graph.Nodes.Select(node => node.Id));
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal("start", graph.Edges[1].FromNodeId);
        Assert.Equal("select", graph.Edges[1].ToNodeId);
    }

    [Fact]
    public void Rejects_duplicate_node_ids_with_stable_message()
    {
        var parser = new MermaidGraphParser();

        var ex = Assert.Throws<MermaidGraphParseException>(() => parser.Parse(
            """
            flowchart TD
                start[Start]
                start[Again]
            """));

        Assert.Equal("graph.duplicate_node", ex.Code);
        Assert.Equal("Duplicate node id 'start'.", ex.Message);
    }

    [Fact]
    public void Rejects_missing_node_references()
    {
        var parser = new MermaidGraphParser();
        var validator = new GraphValidator();

        var graph = parser.Parse(
            """
            flowchart TD
                a[Alpha] --> b
            """);

        var result = validator.Validate(graph);

        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Code == "graph.unknown_node_ref"));
        Assert.Equal("Edge target 'b' is not declared.", diagnostic.Message);
    }

    [Fact]
    public void Rejects_cycles_by_default()
    {
        var parser = new MermaidGraphParser();
        var validator = new GraphValidator();

        var graph = parser.Parse(
            """
            flowchart TD
                a[A] --> b[B]
                b --> a
            """);

        var result = validator.Validate(graph);

        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Code == "graph.cycle_detected"));
        Assert.Equal("Graph contains at least one cycle.", diagnostic.Message);
    }

    [Fact]
    public void Sorts_nodes_and_edges_deterministically()
    {
        var parser = new MermaidGraphParser();

        var graph = parser.Parse(
            """
            flowchart TD
                c[C]
                a[A]
                b[B]
                c --> a
                a --> b
            """);

        Assert.Equal(new[] { "a", "b", "c" }, graph.Nodes.Select(node => node.Id).ToArray());
        Assert.Equal(
            new[] { "a->b", "c->a" },
            graph.Edges.Select(edge => $"{edge.FromNodeId}->{edge.ToNodeId}").ToArray());
    }

    [Fact]
    public void Parses_segments_with_whitespace_and_semicolons()
    {
        var parser = new MermaidGraphParser();

        var graph = parser.Parse(" flowchart TD\n  start[Start] --> mid[Middle] ; mid --> end((Done)) ; ");

        Assert.Equal(new[] { "end", "mid", "start" }, graph.Nodes.Select(node => node.Id).ToArray());
        Assert.Equal(new[] { "mid->end", "start->mid" }, graph.Edges.Select(edge => $"{edge.FromNodeId}->{edge.ToNodeId}").ToArray());
    }
}
