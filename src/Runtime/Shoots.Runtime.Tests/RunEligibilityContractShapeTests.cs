using System.Linq;
using System.Reflection;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RunEligibilityContractShapeTests
{
    [Fact]
    public void Decision_gate_waiting_info_shape_is_frozen()
    {
        var expected = new[]
        {
            "WorkOrderId",
            "RouteGateId",
            "CurrentNodeId",
            "IntentTokenHash",
            "PlanHash",
            "Policy",
            "FallbackPresent",
            "ReasonCode",
            "AllowedNextNodes",
            "DecisionPromptKey",
            "DecisionOwner",
            "FallbackToolSelection",
            "FallbackNextNodeId"
        };

        var actual = typeof(DecisionGateWaitingInfo).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Run_resume_state_shape_is_frozen()
    {
        var expected = new[]
        {
            "WorkOrderId",
            "LastOutcomeKind",
            "LastWaitingReceipt",
            "LastInjectedDecisionDigest",
            "LastPlanHash",
            "LastIntentTokenHash",
            "AttemptCounter",
            "ProgressToken"
        };

        var actual = typeof(RunResumeState).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Runtime_run_options_shape_is_frozen()
    {
        var expected = new[]
        {
            "ResumeMode",
            "InjectedDecisionDigest",
            "DiscardWaiting",
            "AllowPlanChangeOverride"
        };

        var actual = typeof(RuntimeRunOptions).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Run_resume_state_store_surface_is_frozen()
    {
        var expected = new[]
        {
            "LoadByWorkOrderId",
            "SaveByWorkOrderId"
        };

        var actual = typeof(IRunResumeStateStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(expected.OrderBy(x => x).ToArray(), actual);
    }
}
