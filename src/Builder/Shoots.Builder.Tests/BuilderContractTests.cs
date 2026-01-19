using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Builder.Tests;

public sealed class BuilderContractTests
{
    [Fact]
    public void Build_request_can_produce_plan_via_planner()
    {
        var request = new BuildRequest(
            WorkOrder: new WorkOrder(
                Id: new WorkOrderId("wo-test"),
                OriginalRequest: "Test request.",
                Goal: "Validate planner contract.",
                Constraints: Array.Empty<string>(),
                SuccessCriteria: Array.Empty<string>()),
            CommandId: "core.ping",
            Args: new Dictionary<string, object?>
            {
                ["msg"] = "hello"
            },
            RouteRules: new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Linear, Array.Empty<string>()),
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Linear, Array.Empty<string>()),
                new RouteRule("review", RouteIntent.Review, DecisionOwner.Human, "review", MermaidNodeKind.Linear, Array.Empty<string>()),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Linear, Array.Empty<string>())
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

        var directReferences = CollectDirectReferencedAssemblyNames(assembly);
        var transitiveReferences = CollectTransitiveReferencedAssemblyNames(assembly);

        Assert.DoesNotContain("Shoots.Runtime.Core", directReferences);
        Assert.DoesNotContain("Shoots.Runtime.Loader", directReferences);
        Assert.DoesNotContain("Shoots.Runtime.Sandbox", directReferences);

        Assert.DoesNotContain("Shoots.Runtime.Core", transitiveReferences);
        Assert.DoesNotContain("Shoots.Runtime.Loader", transitiveReferences);
        Assert.DoesNotContain("Shoots.Runtime.Sandbox", transitiveReferences);
    }

    [Fact]
    public void BuilderCli_must_not_reference_runtime_execution_projects()
    {
        var assembly = Assembly.Load("Shoots.Builder.Cli");

        var directReferences = CollectDirectReferencedAssemblyNames(assembly);
        var transitiveReferences = CollectTransitiveReferencedAssemblyNames(assembly);

        Assert.DoesNotContain("Shoots.Runtime.Core", directReferences);
        Assert.DoesNotContain("Shoots.Runtime.Loader", directReferences);
        Assert.DoesNotContain("Shoots.Runtime.Sandbox", directReferences);

        Assert.DoesNotContain("Shoots.Runtime.Core", transitiveReferences);
        Assert.DoesNotContain("Shoots.Runtime.Loader", transitiveReferences);
        Assert.DoesNotContain("Shoots.Runtime.Sandbox", transitiveReferences);
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

    [Fact]
    public void BuilderCore_must_not_reference_tool_registry()
    {
        var assembly = typeof(Shoots.Builder.Core.BuilderKernel).Assembly;

        var parameterTypes = assembly
            .GetTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .OfType<MethodBase>()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IToolRegistry), parameterTypes);
    }

    private static string[] CollectDirectReferencedAssemblyNames(Assembly rootAssembly)
    {
        return rootAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray()!;
    }

    private static string[] CollectTransitiveReferencedAssemblyNames(Assembly rootAssembly)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();
        queue.Enqueue(rootAssembly);

        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (string.IsNullOrWhiteSpace(reference.Name))
                    continue;

                if (!visited.Add(reference.Name))
                    continue;

                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch
                {
                    // Ignore load failures; they are not part of the boundary check.
                }
            }
        }

        return visited.ToArray();
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
