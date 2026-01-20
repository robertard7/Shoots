using System.Collections.Generic;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Tests;

internal static class BuildPlanTestFactory
{
    internal static BuildPlan CreatePlan(
        BuildRequest request,
        IReadOnlyList<BuildStep> steps,
        DelegationAuthority? authority = null,
        bool useDefaultAuthority = true,
        IReadOnlyList<BuildArtifact>? artifacts = null,
        ToolResult? toolResult = null,
        string? graphHash = null,
        string? nodeHash = null,
        string? edgeHash = null)
    {
        var resolvedAuthority = authority ?? (useDefaultAuthority ? CreateAuthority() : null);
        var resolvedArtifacts = artifacts ?? new[] { new BuildArtifact("plan.json", "Plan payload.") };
        var resolvedGraphHash = graphHash ?? HashTools.ComputeSha256Hash("graph");
        var resolvedNodeHash = nodeHash ?? HashTools.ComputeSha256Hash("nodes");
        var resolvedEdgeHash = edgeHash ?? HashTools.ComputeSha256Hash("edges");

        return new BuildPlan(
            "plan",
            request,
            resolvedGraphHash,
            resolvedNodeHash,
            resolvedEdgeHash,
            resolvedAuthority!,
            steps,
            resolvedArtifacts,
            toolResult);
    }

    private static DelegationAuthority CreateAuthority()
    {
        return new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false);
    }
}
