using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Core.Routing;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RunLedgerReplayTests
{
    [Fact]
    public void Replay_produces_identical_final_outputs()
    {
        var route = new RouteDefinition(new RouteStep[]
        {
            new("s:0000", "select", RouteNodeKind.SelectTool, new Dictionary<string, string> { ["command"] = "core.route" }, new[] { new RouteEdge("e", null) }),
            new("e:0001", "e", RouteNodeKind.End, new Dictionary<string, string>(), Array.Empty<RouteEdge>())
        });

        var request = new PlannerRequest("core.route", new Dictionary<string, string>());
        var kernel = new PlannerKernel();
        var plan = kernel.Plan(
            request,
            route,
            new Decision("tools.echo", new Dictionary<string, string> { ["path"] = "README.md" }),
            Catalog.WithTool("tools.echo", "path"));

        var ledger = RunLedgerBuilder.Create(plan, request, route, "catalog-123");
        var replay = new ReplayRunner().Replay(ledger, request, route, "catalog-123");

        Assert.Equal(plan.PlanId, replay.PlanId);
        Assert.Equal(plan.Steps.Select(s => s.StepId), replay.Steps.Select(s => s.StepId));
        Assert.Equal(plan.Artifacts.Select(a => a.ArtifactId), replay.Artifacts.Select(a => a.ArtifactId));
    }

    [Fact]
    public void Replay_hash_mismatch_fails_with_stable_error()
    {
        var route = new RouteDefinition(new RouteStep[]
        {
            new("a:0000", "a", RouteNodeKind.End, new Dictionary<string, string>(), Array.Empty<RouteEdge>())
        });

        var request = new PlannerRequest("core.route", new Dictionary<string, string>());
        var plan = new BuildPlan(Array.Empty<PlanStep>(), Array.Empty<PlanArtifact>(), Array.Empty<PlanDiagnostic>(), "plan-1");
        var ledger = RunLedgerBuilder.Create(plan, request, route, "catalog-A");

        var ex = Assert.Throws<PlannerKernelException>(() => new ReplayRunner().Replay(ledger, request, route, "catalog-B"));
        Assert.Equal("replay.hash_mismatch", ex.Code);
        Assert.Equal("Catalog hash mismatch.", ex.Message);
    }

    private sealed class Decision(string toolId, IReadOnlyDictionary<string, string> bindings) : IPlannerDecisionProvider
    {
        public ToolSelectionDecision SelectTool(PlannerRequest request, RouteStep step) => new(toolId, bindings);
    }

    private sealed class Catalog : IPlannerToolCatalog
    {
        private readonly IReadOnlyDictionary<string, ToolDescriptor> _map;

        private Catalog(IReadOnlyDictionary<string, ToolDescriptor> map)
        {
            _map = map;
        }

        public static Catalog WithTool(string toolId, params string[] keys)
        {
            return new Catalog(new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal)
            {
                [toolId] = new ToolDescriptor(toolId, keys)
            });
        }

        public bool TryGetTool(string toolId, out ToolDescriptor descriptor)
        {
            if (_map.TryGetValue(toolId, out descriptor!))
                return true;

            descriptor = default!;
            return false;
        }
    }
}
