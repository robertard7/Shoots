using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class BuildPlanHasherPathTests
{
    [Fact]
    public void Hash_rejects_absolute_paths()
    {
        var request = new BuildRequest(
            "core.ping",
            new Dictionary<string, object?>
            {
                ["plan.graph"] = "graph TD; validate-command --> resolve-command --> execute-command"
            });
        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false
        );

        var steps = new BuildStep[]
        {
            new BuildStep("validate-command", "/usr/local/bin"),
        };
        var artifacts = new[] { new BuildArtifact("plan.json", "Plan payload.") };

        Assert.Throws<ArgumentException>(
            () => BuildPlanHasher.ComputePlanId(request, authority, steps, artifacts));
    }
}
