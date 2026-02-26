using System;
using Shoots.Builder.Core;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Builder.Tests;

public sealed class MermaidPlanGraphTests
{
    [Fact]
    public void ParseGraph_allows_duplicate_node_mentions_when_kind_matches()
    {
        var graph = "graph TD; select:::start --> validate --> review --> terminate:::terminal; select --> validate";

        var parsed = MermaidPlanGraph.ParseGraph(graph);

        Assert.Contains(parsed.Nodes, n => n.Id == "validate" && n.Kind == MermaidNodeKind.Route);
        Assert.Contains(("select", "validate"), parsed.Edges);
    }

    [Fact]
    public void ParseGraph_throws_on_duplicate_node_with_conflicting_kind()
    {
        var graph = "graph TD; validate:::route; validate:::gate";

        var ex = Assert.Throws<InvalidOperationException>(() => MermaidPlanGraph.ParseGraph(graph));

        Assert.Contains("conflicting kind", ex.Message, StringComparison.Ordinal);
    }
}
