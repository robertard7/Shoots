using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Core.Routing;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class PlannerKernelTests
{
    [Fact]
    public void Same_request_twice_produces_identical_build_plan()
    {
        var kernel = new PlannerKernel();
        var route = BuildRoute();
        var request = new PlannerRequest("core.route", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["branch"] = "yes",
            ["x"] = "1"
        });

        var decisionProvider = new FixedDecisionProvider("tools.echo", new Dictionary<string, string> { ["path"] = "README.md" });
        var catalog = Catalog.WithTool("tools.echo", "path");

        var first = kernel.Plan(request, route, decisionProvider, catalog);
        var second = kernel.Plan(request, route, decisionProvider, catalog);

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.Steps.Select(s => s.StepId), second.Steps.Select(s => s.StepId));
        Assert.Equal(first.Steps.Select(s => s.NodeId), second.Steps.Select(s => s.NodeId));
        Assert.Equal(first.Artifacts.Select(a => a.ArtifactId), second.Artifacts.Select(a => a.ArtifactId));
        Assert.Equal(first.Artifacts.Select(a => a.Content), second.Artifacts.Select(a => a.Content));
    }

    [Fact]
    public void Unknown_command_rejected_with_stable_error()
    {
        var kernel = new PlannerKernel();
        var route = BuildRoute();
        var request = new PlannerRequest("core.unknown", new Dictionary<string, string>());

        var ex = Assert.Throws<PlannerKernelException>(() => kernel.Plan(
            request,
            route,
            new FixedDecisionProvider("tools.echo", new Dictionary<string, string> { ["path"] = "a" }),
            Catalog.WithTool("tools.echo", "path")));

        Assert.Equal("plan.invalid_request", ex.Code);
        Assert.Equal("Unknown command 'core.unknown'.", ex.Message);
    }

    [Fact]
    public void Invalid_tool_selection_rejected_with_stable_error()
    {
        var kernel = new PlannerKernel();
        var route = BuildRoute();
        var request = new PlannerRequest("core.route", new Dictionary<string, string>());

        var ex = Assert.Throws<PlannerKernelException>(() => kernel.Plan(
            request,
            route,
            new FixedDecisionProvider("tools.missing", new Dictionary<string, string>()),
            Catalog.WithTool("tools.echo", "path")));

        Assert.Equal("route.schema_invalid", ex.Code);
        Assert.Equal("Tool 'tools.missing' was not found in the catalog.", ex.Message);
    }

    [Fact]
    public void Branch_resolution_is_deterministic_with_target_tie_break()
    {
        var route = new RouteDefinition(new RouteStep[]
        {
            new("a:0000", "a", RouteNodeKind.Branch, new Dictionary<string, string>(), new[]
            {
                new RouteEdge("z", "yes"),
                new RouteEdge("b", "yes")
            }),
            new("b:0001", "b", RouteNodeKind.End, new Dictionary<string, string>(), Array.Empty<RouteEdge>()),
            new("z:0002", "z", RouteNodeKind.End, new Dictionary<string, string>(), Array.Empty<RouteEdge>())
        });

        var kernel = new PlannerKernel();
        var request = new PlannerRequest("core.route", new Dictionary<string, string> { ["branch"] = "yes" });
        var plan = kernel.Plan(
            request,
            route,
            new FixedDecisionProvider("tools.echo", new Dictionary<string, string> { ["path"] = "a" }),
            Catalog.WithTool("tools.echo", "path"));

        Assert.Equal(new[] { "a", "b" }, plan.Steps.Select(step => step.NodeId).ToArray());
    }

    [Fact]
    public void Max_steps_enforced_deterministically()
    {
        var route = new RouteDefinition(new RouteStep[]
        {
            new("a:0000", "a", RouteNodeKind.Emit, new Dictionary<string, string> { ["text"] = "a" }, new[] { new RouteEdge("b", null) }),
            new("b:0001", "b", RouteNodeKind.Emit, new Dictionary<string, string> { ["text"] = "b" }, new[] { new RouteEdge("c", null) }),
            new("c:0002", "c", RouteNodeKind.End, new Dictionary<string, string>(), Array.Empty<RouteEdge>())
        });

        var kernel = new PlannerKernel();
        var request = new PlannerRequest("core.route", new Dictionary<string, string>(), new PlannerLimits { MaxSteps = 2 });

        var ex = Assert.Throws<PlannerKernelException>(() => kernel.Plan(
            request,
            route,
            new FixedDecisionProvider("tools.echo", new Dictionary<string, string>()),
            Catalog.WithTool("tools.echo", "path")));

        Assert.Equal("plan.invalid_request", ex.Code);
        Assert.Equal("Step cap exceeded at maxSteps=2.", ex.Message);
    }

    [Fact]
    public void Snapshot_plan_is_stable()
    {
        var kernel = new PlannerKernel();
        var route = BuildRoute();
        var request = new PlannerRequest("core.route", new Dictionary<string, string> { ["branch"] = "yes" });

        var plan = kernel.Plan(
            request,
            route,
            new FixedDecisionProvider("tools.echo", new Dictionary<string, string> { ["path"] = "README.md" }),
            Catalog.WithTool("tools.echo", "path"));

        var snapshot = string.Join('|', plan.Steps.Select(step => $"{step.StepId}:{step.NodeKind}:{step.ToolId ?? "-"}"));
        Assert.Equal("select:0003:SelectTool:tools.echo|branch:0000:Branch:-|emitYes:0001:Emit:-|end:0002:End:-", snapshot);
    }

    private static RouteDefinition BuildRoute()
    {
        return new RouteDefinition(new RouteStep[]
        {
            new("select:0003", "select", RouteNodeKind.SelectTool, new Dictionary<string, string> { ["command"] = "core.route" }, new[] { new RouteEdge("branch", null) }),
            new("branch:0000", "branch", RouteNodeKind.Branch, new Dictionary<string, string>(), new[]
            {
                new RouteEdge("emitYes", "yes"),
                new RouteEdge("end", string.Empty)
            }),
            new("emitYes:0001", "emitYes", RouteNodeKind.Emit, new Dictionary<string, string> { ["text"] = "ok" }, new[] { new RouteEdge("end", null) }),
            new("end:0002", "end", RouteNodeKind.End, new Dictionary<string, string>(), Array.Empty<RouteEdge>())
        });
    }

    private sealed class FixedDecisionProvider(string toolId, IReadOnlyDictionary<string, string> bindings) : IPlannerDecisionProvider
    {
        public ToolSelectionDecision SelectTool(PlannerRequest request, RouteStep step) => new(toolId, bindings);
    }

    private sealed class Catalog : IPlannerToolCatalog
    {
        private readonly IReadOnlyDictionary<string, ToolDescriptor> _tools;

        private Catalog(IReadOnlyDictionary<string, ToolDescriptor> tools)
        {
            _tools = tools;
        }

        public static Catalog WithTool(string toolId, params string[] allowedBindingKeys)
        {
            return new Catalog(new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal)
            {
                [toolId] = new ToolDescriptor(toolId, allowedBindingKeys)
            });
        }

        public bool TryGetTool(string toolId, out ToolDescriptor descriptor)
        {
            if (_tools.TryGetValue(toolId, out descriptor!))
                return true;

            descriptor = default!;
            return false;
        }
    }
}
