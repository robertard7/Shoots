using System.Text.Json;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

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

        var planner = new DeterministicBuildPlanner(services, new DefaultDelegationPolicy());
        var request = new BuildRequest(
            " core.ping ",
            new Dictionary<string, object?>
            {
                ["b"] = "2",
                ["a"] = "1"
            }
        );

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
        Assert.Equal(plan.AuthorityProviderId, roundTrip.AuthorityProviderId);
        Assert.Equal(plan.AuthorityKind, roundTrip.AuthorityKind);

        var secondPlan = planner.Plan(request);
        Assert.Equal(plan.PlanId, secondPlan.PlanId);
        Assert.Equal(planText, BuildPlanRenderer.RenderText(secondPlan));
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
}
