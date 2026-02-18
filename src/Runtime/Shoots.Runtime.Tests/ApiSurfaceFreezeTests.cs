using System;
using System.Linq;
using System.Reflection;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ApiSurfaceFreezeTests
{
    [Fact]
    public void Resume_mode_enum_values_are_frozen()
    {
        var expected = new[]
        {
            nameof(ResumeMode.None),
            nameof(ResumeMode.InjectDecision),
            nameof(ResumeMode.OverridePlanChange),
            nameof(ResumeMode.DiscardWaitingStartOver)
        };

        Assert.Equal(expected, Enum.GetNames(typeof(ResumeMode)));
    }

    [Fact]
    public void Run_outcome_kind_enum_values_are_frozen()
    {
        var expected = new[]
        {
            nameof(RunOutcomeKind.Completed),
            nameof(RunOutcomeKind.Halted),
            nameof(RunOutcomeKind.Waiting)
        };

        Assert.Equal(expected, Enum.GetNames(typeof(RunOutcomeKind)));
    }

    [Fact]
    public void Runtime_narrator_method_surface_is_frozen()
    {
        var expected = new[]
        {
            "OnCommand",
            "OnCompleted",
            "OnDecisionAccepted",
            "OnDecisionGateBypassed",
            "OnDecisionGateRequiredError",
            "OnDecisionGateWaiting",
            "OnDecisionRequired",
            "OnError",
            "OnHalted",
            "OnNodeAdvanced",
            "OnNodeEntered",
            "OnNodeHalted",
            "OnNodeTransitionChosen",
            "OnPlan",
            "OnResult",
            "OnRoute",
            "OnRouteEntered",
            "OnStepBudgetExceeded",
            "OnWorkOrderReceived"
        };

        var actual = typeof(IRuntimeNarrator)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name).ToArray(), actual);
    }

    [Fact]
    public void Runtime_orchestrator_run_and_resume_signatures_are_frozen()
    {
        var methods = typeof(RuntimeOrchestrator)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(method => method.Name is "Run" or "Resume")
            .Select(DescribeMethod)
            .OrderBy(x => x)
            .ToArray();

        var expected = new[]
        {
            "ExecutionEnvelope Resume(RoutingTrace trace, IToolRegistry registry, IAiDecisionProvider aiDecisionProvider)",
            "ExecutionEnvelope Run(BuildPlan plan, IToolRegistry registry, IAiDecisionProvider aiDecisionProvider = null, IRuntimePersistence persistence = null, IRuntimeNarrator narrator = null, RuntimeRunOptions options = null)",
            "ExecutionEnvelope Run(BuildPlan plan, RuntimeRunOptions options = null)"
        };

        Assert.Equal(expected.OrderBy(x => x).ToArray(), methods);
    }

    private static string DescribeMethod(MethodInfo method)
    {
        static string Simplify(Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
                return Simplify(nullable);

            return type.Name switch
            {
                "BuildPlan" => "BuildPlan",
                "IToolRegistry" => "IToolRegistry",
                "IAiDecisionProvider" => "IAiDecisionProvider",
                "IRuntimePersistence" => "IRuntimePersistence",
                "IRuntimeNarrator" => "IRuntimeNarrator",
                "RuntimeRunOptions" => "RuntimeRunOptions",
                "RoutingTrace" => "RoutingTrace",
                "ExecutionEnvelope" => "ExecutionEnvelope",
                _ => type.Name
            };
        }

        var parameters = string.Join(", ", method.GetParameters().Select(parameter =>
        {
            var defaultSuffix = parameter.HasDefaultValue ? " = null" : string.Empty;
            return $"{Simplify(parameter.ParameterType)} {parameter.Name}{defaultSuffix}";
        }));

        return $"{Simplify(method.ReturnType)} {method.Name}({parameters})";
    }
}
