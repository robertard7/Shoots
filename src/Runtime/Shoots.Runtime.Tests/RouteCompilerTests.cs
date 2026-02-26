using System;
using System.Linq;
using Shoots.Runtime.Core.Routing;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RouteCompilerTests
{
    [Fact]
    public void Compile_linear_graph_has_stable_step_ordering()
    {
        var parser = new MermaidGraphParser();
        var compiler = new RouteCompiler();

        var graph = parser.Parse(
            """
            flowchart TD
              start[SelectTool;command=core.route] --> run[RunTool;tool=tools.exec]
              run --> done[End]
            """);

        var route = compiler.Compile(graph);

        Assert.Equal(new[] { "done", "run", "start" }, route.Steps.Select(step => step.NodeId).ToArray());
        Assert.Equal(new[] { "done:0000", "run:0001", "start:0002" }, route.Steps.Select(step => step.StepId).ToArray());
    }

    [Fact]
    public void Compile_branching_graph_sorts_edges_by_target_id()
    {
        var parser = new MermaidGraphParser();
        var compiler = new RouteCompiler();

        var graph = parser.Parse(
            """
            flowchart TD
              branch[Branch;condition=next] --> zeta[Emit;text=Z]
              branch --> alpha[Emit;text=A]
              alpha --> done[End]
              zeta --> done
            """);

        var route = compiler.Compile(graph);
        var branchStep = Assert.Single(route.Steps, step => step.NodeId == "branch");

        Assert.Equal(new[] { "alpha", "zeta" }, branchStep.OutgoingEdges.Select(edge => edge.TargetNodeId).ToArray());
    }

    [Fact]
    public void Rejects_unknown_metadata_key()
    {
        var parser = new MermaidGraphParser();
        var compiler = new RouteCompiler();

        var graph = parser.Parse("flowchart TD\n start[SelectTool;unknown=x] --> end[End]");

        var ex = Assert.Throws<RouteCompilerException>(() => compiler.Compile(graph));
        Assert.Equal("route.schema_invalid", ex.Code);
        Assert.Equal("Node 'start' contains unsupported metadata key 'unknown'.", ex.Message);
    }

    [Fact]
    public void Rejects_invalid_node_kind()
    {
        var parser = new MermaidGraphParser();
        var compiler = new RouteCompiler();

        var graph = parser.Parse("flowchart TD\n start[NotAKind] --> end[End]");

        var ex = Assert.Throws<RouteCompilerException>(() => compiler.Compile(graph));
        Assert.Equal("route.schema_invalid", ex.Code);
        Assert.Equal("Unknown route node kind 'NotAKind'.", ex.Message);
    }

    [Fact]
    public void Step_ids_are_deterministic_snapshot()
    {
        var parser = new MermaidGraphParser();
        var compiler = new RouteCompiler();

        var graph = parser.Parse(
            """
            flowchart TD
              n3[Emit;text=three]
              n1[SelectTool;command=core.route] --> n2[RunTool;tool=tools.exec]
              n2 --> n3
            """);

        var route = compiler.Compile(graph);
        var snapshot = string.Join('|', route.Steps.Select(step => $"{step.NodeId}:{step.StepId}:{step.Kind}"));

        Assert.Equal("n1:n1:0000:SelectTool|n2:n2:0001:RunTool|n3:n3:0002:Emit", snapshot);
    }
}
