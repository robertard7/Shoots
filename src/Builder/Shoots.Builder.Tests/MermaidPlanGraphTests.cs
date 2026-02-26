using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Builder.Core;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Builder.Tests;

public sealed class MermaidPlanGraphTests
{
    [Fact]
    public void Planner_allows_duplicate_node_mentions_when_kind_matches()
    {
        var planner = CreatePlanner();
        var request = CreateRequest(
            "graph TD; select:::start --> validate --> review --> terminate:::terminal; select --> validate");

        var plan = planner.Plan(request);
        var validateRule = Assert.Single(plan.Request.RouteRules, rule => rule.NodeId == "validate");

        Assert.Equal(MermaidNodeKind.Route, validateRule.NodeKind);
        Assert.Equal(new[] { "select", "validate", "review", "terminate" }, plan.Steps.Select(step => step.Id).ToArray());
        Assert.Equal(new[] { "validate" }, plan.Request.RouteRules.Single(rule => rule.NodeId == "select").AllowedNextNodes);
    }

    [Fact]
    public void Planner_throws_on_duplicate_node_with_conflicting_kind()
    {
        var planner = CreatePlanner();
        var request = CreateRequest("graph TD; validate:::route; validate:::gate");

        var ex = Assert.Throws<InvalidOperationException>(() => planner.Plan(request));

        Assert.Contains("conflicting kind", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_normalizes_whitespace_in_graph_argument()
    {
        var planner = CreatePlanner();
        var request = CreateRequest("\r\ngraph TD; select:::start --> validate --> review --> terminate:::terminal\r\n");

        var plan = planner.Plan(request);

        Assert.Equal("graph TD; select:::start --> validate --> review --> terminate:::terminal", plan.Request.Args["plan.graph"]);
    }

    private static DeterministicBuildPlanner CreatePlanner()
    {
        var services = new StubRuntimeServices(
            new RuntimeCommandSpec(
                "core.ping",
                "Health check.",
                Array.Empty<RuntimeArgSpec>()));

        return new DeterministicBuildPlanner(services, new StubDelegationPolicy());
    }

    private static BuildRequest CreateRequest(string graph)
    {
        return new BuildRequest(
            WorkOrder: new WorkOrder(
                Id: new WorkOrderId("wo-test"),
                OriginalRequest: "Test request.",
                Goal: "Validate planning behavior.",
                Constraints: Array.Empty<string>(),
                SuccessCriteria: Array.Empty<string>()),
            CommandId: "core.ping",
            Args: new Dictionary<string, object?>
            {
                ["plan.graph"] = graph
            },
            RouteRules:
            [
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Route, Array.Empty<string>()),
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Route, Array.Empty<string>()),
                new RouteRule("review", RouteIntent.Review, DecisionOwner.Human, "review", MermaidNodeKind.Route, Array.Empty<string>()),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Route, Array.Empty<string>())
            ]);
    }

    private sealed class StubRuntimeServices : IRuntimeServices
    {
        private readonly IReadOnlyList<RuntimeCommandSpec> _commands;

        public StubRuntimeServices(params RuntimeCommandSpec[] commands)
        {
            _commands = commands;
        }

        public IReadOnlyList<RuntimeCommandSpec> GetAllCommands() => _commands;

        public RuntimeCommandSpec? GetCommand(string commandId)
        {
            return _commands.FirstOrDefault(
                command => string.Equals(command.CommandId, commandId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class StubDelegationPolicy : IDelegationPolicy
    {
        public string PolicyId => "local-only";

        public DelegationDecision Decide(BuildRequest request, BuildPlan plan)
        {
            _ = request ?? throw new ArgumentNullException(nameof(request));
            _ = plan ?? throw new ArgumentNullException(nameof(plan));

            return new DelegationDecision(
                new DelegationAuthority(
                    ProviderId: new ProviderId("local"),
                    Kind: ProviderKind.Local,
                    PolicyId: PolicyId,
                    AllowsDelegation: false
                )
            );
        }
    }
}
