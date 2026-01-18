using System;
using System.Collections.Generic;
using Shoots.Builder.Core;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Builder.Tests;

public sealed class DeterministicPlannerTests
{
    [Fact]
    public void Planner_is_deterministic_for_same_request()
    {
        var services = new StubRuntimeServices(
            new RuntimeCommandSpec(
                "core.ping",
                "Health check.",
                Array.Empty<RuntimeArgSpec>()));

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

        var firstPlan = planner.Plan(request);
        var secondPlan = planner.Plan(request);

        Assert.Equal(firstPlan.PlanId, secondPlan.PlanId);
        Assert.Equal(firstPlan.Steps.Select(step => step.Id), secondPlan.Steps.Select(step => step.Id));
        Assert.Equal(firstPlan.Steps.Select(step => step.Description), secondPlan.Steps.Select(step => step.Description));
        Assert.Equal(firstPlan.Artifacts.Select(artifact => artifact.Id), secondPlan.Artifacts.Select(artifact => artifact.Id));
        Assert.Equal(firstPlan.Artifacts.Select(artifact => artifact.Description), secondPlan.Artifacts.Select(artifact => artifact.Description));
    }

    [Fact]
    public void Planner_hash_matches_authority_fields()
    {
        var services = new StubRuntimeServices();
        var policy = new StubDelegationPolicy();
        var planner = new DeterministicBuildPlanner(services, policy);
        var request = CreateRequest(
            "core.ping",
            "graph TD; select --> validate --> review --> terminate",
            null,
            new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection"),
            new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation"),
            new RouteRule("review", RouteIntent.Review, DecisionOwner.Human, "review"),
            new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination"));

        var plan = planner.Plan(request);
        var computed = BuildPlanHasher.ComputePlanId(plan.Request, plan.Authority, plan.Steps, plan.Artifacts);

        Assert.Equal(plan.PlanId, computed);
    }

    [Fact]
    public void Planner_assembly_must_not_reference_runtime_execution_projects()
    {
        var assembly = typeof(DeterministicBuildPlanner).Assembly;

        var referencedAssemblyNames = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.DoesNotContain("Shoots.Runtime.Core", referencedAssemblyNames);
        Assert.DoesNotContain("Shoots.Runtime.Loader", referencedAssemblyNames);
        Assert.DoesNotContain("Shoots.Runtime.Sandbox", referencedAssemblyNames);
    }

    [Fact]
    public void Planner_orders_steps_from_graph()
    {
        var services = new StubRuntimeServices(
            new RuntimeCommandSpec(
                "core.ping",
                "Health check.",
                Array.Empty<RuntimeArgSpec>()));

        var planner = new DeterministicBuildPlanner(services, new StubDelegationPolicy());
        var request = CreateRequest(
            "core.ping",
            "graph TD; select --> review --> validate --> terminate",
            null,
            new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection"),
            new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation"),
            new RouteRule("review", RouteIntent.Review, DecisionOwner.Human, "review"),
            new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination"));

        var plan = planner.Plan(request);

        Assert.Equal(
            new[] { "select", "review", "validate", "terminate" },
            plan.Steps.Select(step => step.Id));
    }

    [Fact]
    public void Planner_rejects_graph_missing_required_steps()
    {
        var services = new StubRuntimeServices(
            new RuntimeCommandSpec(
                "core.ping",
                "Health check.",
                Array.Empty<RuntimeArgSpec>()));

        var planner = new DeterministicBuildPlanner(services, new StubDelegationPolicy());
        var request = CreateRequest(
            "core.ping",
            "graph TD; select --> validate",
            null,
            new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection"),
            new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation"),
            new RouteRule("review", RouteIntent.Review, DecisionOwner.Human, "review"),
            new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination"));

        Assert.Throws<InvalidOperationException>(() => planner.Plan(request));
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
                Goal: "Validate planning behavior.",
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
