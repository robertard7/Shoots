using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Builder.Tests;

public sealed class BuilderContractTests
{
    [Fact]
    public void Build_request_can_produce_plan_via_planner()
    {
        var request = new BuildRequest(
            CommandId: "core.ping",
            Args: new Dictionary<string, object?>
            {
                ["msg"] = "hello"
            }
        );

        IBuildPlanner planner = new StubPlanner();

        var plan = planner.Plan(request);

        Assert.Equal("core.ping", plan.Request.CommandId);
        Assert.NotEmpty(plan.Steps);
        Assert.NotEmpty(plan.Artifacts);
    }

    [Fact]
    public void BuilderCore_must_not_reference_runtime_execution_projects()
    {
        var assembly = typeof(Shoots.Builder.Core.BuilderKernel).Assembly;

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
    public void BuilderCore_cannot_accept_provider_authority_in_constructors()
    {
        var kernelType = typeof(Shoots.Builder.Core.BuilderKernel);

        var parameterTypes = kernelType
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(ProviderId), parameterTypes);
        Assert.DoesNotContain(typeof(ProviderKind), parameterTypes);
        Assert.DoesNotContain(typeof(DelegationAuthority), parameterTypes);
    }

    // ----------------------------
    // Stub planner (pure abstraction)
    // ----------------------------
    private sealed class StubPlanner : IBuildPlanner
    {
        public BuildPlan Plan(BuildRequest request)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));

            return new BuildPlan(
                PlanId: "stub-plan",
                Request: request,
                Authority: new DelegationAuthority(
                    ProviderId: new ProviderId("local"),
                    Kind: ProviderKind.Local,
                    PolicyId: "local-only",
                    AllowsDelegation: false
                ),
                Steps: new[]
                {
                    new BuildStep("stub-step", "Stub step.")
                },
                Artifacts: new[]
                {
                    new BuildArtifact("stub-artifact", "Stub artifact.")
                }
            );
        }
    }
}
