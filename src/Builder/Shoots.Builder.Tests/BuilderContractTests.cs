using System.Reflection;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Builder.Tests;

public sealed class BuilderContractTests
{
    [Fact]
    public void Build_request_can_produce_plan_via_planner()
    {
        var request = new BuildRequest(
            "core.ping",
            new Dictionary<string, object?> { ["msg"] = "hello" }
        );

        var planner = new StubPlanner();
        var plan = planner.Plan(request);

        Assert.Equal("core.ping", plan.Request.CommandId);
        Assert.NotEmpty(plan.Steps);
        Assert.NotEmpty(plan.Artifacts);
    }

    [Fact]
    public void BuilderCore_must_not_reference_runtime_execution()
    {
        var asm = typeof(Shoots.Builder.Core.BuilderKernel).Assembly;
        var referenced = asm.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("Shoots.Runtime.Core", referenced);
        Assert.DoesNotContain("Shoots.Runtime.Loader", referenced);
        Assert.DoesNotContain("Shoots.Runtime.Sandbox", referenced);
    }

    [Fact]
    public void BuilderCore_cannot_inject_provider_authority()
    {
        var kernelType = typeof(Shoots.Builder.Core.BuilderKernel);
        var constructorParams = kernelType.GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(param => param.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(ProviderId), constructorParams);
        Assert.DoesNotContain(typeof(ProviderKind), constructorParams);
    }

    private sealed class StubPlanner : IBuildPlanner
    {
        public BuildPlan Plan(BuildRequest request)
        {
            return new BuildPlan(
                PlanId: "stub-plan",
                Request: request,
                AuthorityProviderId: new ProviderId("local"),
                AuthorityKind: ProviderKind.Local,
                Steps: new[] { new BuildStep("stub-step", "Stub step.") },
                Artifacts: new[] { new BuildArtifact("stub-artifact", "Stub artifact.") }
            );
        }
    }
}
