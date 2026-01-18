using System;
using System.Collections.Generic;
using System.Text.Json;
using Shoots.Builder.Core;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Builder.Tests;

public sealed class PlannerSerializationTests
{
    [Fact]
    public void Plan_round_trip_is_deterministic()
    {
        var services = new StubRuntimeServices(
            new RuntimeCommandSpec(
                "core.ping",
                "Health check.",
                Array.Empty<RuntimeArgSpec>())
        );

        var planner = new DeterministicBuildPlanner(services, new StubDelegationPolicy());
        var request = CreateRequest(
            " core.ping ",
            "graph TD; select --> validate --> review --> terminate",
            new Dictionary<string, object?>
            {
                ["b"] = "2",
                ["a"] = "1"
            },
            new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection"),
            new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation"),
            new RouteRule("review", RouteIntent.Review, DecisionOwner.Human, "review"),
            new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination"));

        var plan = planner.Plan(request);
        var planText = BuildPlanRenderer.RenderText(plan);

        var json = BuildPlanRenderer.RenderJson(plan);
        var roundTrip = JsonSerializer.Deserialize<BuildPlan>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(plan.PlanId, roundTrip!.PlanId);
        Assert.Equal(planText, BuildPlanRenderer.RenderText(roundTrip));
        Assert.Equal(plan.Steps.Select(step => step.Id), roundTrip.Steps.Select(step => step.Id));
        Assert.Equal(plan.Artifacts.Select(artifact => artifact.Id), roundTrip.Artifacts.Select(artifact => artifact.Id));
        Assert.Equal("core.ping", roundTrip.Request.CommandId);
        Assert.Equal(plan.Authority, roundTrip.Authority);

        var secondPlan = planner.Plan(request);
        Assert.Equal(plan.PlanId, secondPlan.PlanId);
        Assert.Equal(planText, BuildPlanRenderer.RenderText(secondPlan));
    }

    private static BuildRequest CreateRequest(
        string commandId,
        string graph,
        IReadOnlyDictionary<string, object?>? extraArgs,
        params RouteRule[] routeRules)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["plan.graph"] = graph
        };

        if (extraArgs is not null)
        {
            foreach (var kvp in extraArgs)
                args[kvp.Key] = kvp.Value;
        }

        return new BuildRequest(
            WorkOrder: new WorkOrder(
                Id: new WorkOrderId("wo-test"),
                OriginalRequest: "Test request.",
                Goal: "Validate plan serialization.",
                Constraints: Array.Empty<string>(),
                SuccessCriteria: Array.Empty<string>()),
            CommandId: commandId,
            Args: args,
            RouteRules: routeRules);
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
