using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ToolAuthorityValidatorTests
{
    [Fact]
    public void Validation_denies_when_tool_requires_higher_authority()
    {
        var planAuthority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false
        );

        var toolSpec = new ToolSpec(
            new ToolId("tools.remote"),
            "Remote tool.",
            new ToolAuthorityScope(ProviderKind.Remote, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>());

        var plan = CreatePlan(planAuthority, toolSpec.ToolId);
        var registry = new StubToolRegistry(toolSpec);

        var result = ToolAuthorityValidator.TryValidate(plan, registry, planAuthority, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("tool_authority_denied", error!.Code);
    }

    [Fact]
    public void Validation_allows_when_authority_meets_requirement()
    {
        var planAuthority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false
        );

        var toolSpec = new ToolSpec(
            new ToolId("tools.local"),
            "Local tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>());

        var plan = CreatePlan(planAuthority, toolSpec.ToolId);
        var registry = new StubToolRegistry(toolSpec);

        var result = ToolAuthorityValidator.TryValidate(plan, registry, planAuthority, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    private static BuildPlan CreatePlan(DelegationAuthority authority, ToolId toolId)
    {
        var request = new BuildRequest("core.tool", new Dictionary<string, object?>());
        var steps = new BuildStep[]
        {
            new ToolBuildStep(
                "tool-step",
                "Use tool.",
                toolId,
                new Dictionary<string, object?>(),
                new[] { new ToolOutputSpec("result", "string", "Result.") })
        };
        var artifacts = new[] { new BuildArtifact("plan.json", "Plan payload.") };

        return new BuildPlan(
            PlanId: "plan",
            Request: request,
            Authority: authority,
            Steps: steps,
            Artifacts: artifacts);
    }

    private sealed class StubToolRegistry : IToolRegistry
    {
        private readonly IReadOnlyDictionary<ToolId, ToolRegistryEntry> _entries;

        public StubToolRegistry(params ToolSpec[] specs)
        {
            var entries = new Dictionary<ToolId, ToolRegistryEntry>();
            foreach (var spec in specs)
                entries[spec.ToolId] = new ToolRegistryEntry(spec);

            _entries = entries;
        }

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => _entries.Values.ToList();

        public ToolRegistryEntry? GetTool(ToolId toolId)
        {
            return _entries.TryGetValue(toolId, out var entry) ? entry : null;
        }
    }
}
