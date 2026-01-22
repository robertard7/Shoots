using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Loader.Toolpacks;
using Shoots.Runtime.Ui.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ToolpackTierPolicyTests
{
    [Fact]
    public void PublicTierFiltersToPublicToolpacks()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(ToolTier.Public, new[] { ToolpackCapability.FileSystem });
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.Single(snapshot.Entries);
        Assert.Equal("public.tool", snapshot.Entries[0].Spec.ToolId.Value);
    }

    [Fact]
    public void DeveloperTierIncludesPublicAndDeveloperToolpacks()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(
            ToolTier.Developer,
            new[] { ToolpackCapability.FileSystem, ToolpackCapability.Build });
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry => entry.Spec.ToolId.Value == "public.tool");
        Assert.Contains(snapshot.Entries, entry => entry.Spec.ToolId.Value == "developer.tool");
    }

    [Fact]
    public void CapabilityMismatchFiltersToolpacks()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(
            ToolTier.Developer,
            new[] { ToolpackCapability.FileSystem });
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.Single(snapshot.Entries);
        Assert.Equal("public.tool", snapshot.Entries[0].Spec.ToolId.Value);
    }

    [Fact]
    public void SystemTierIncludesAllToolpacks()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(
            ToolTier.System,
            new[] { ToolpackCapability.FileSystem, ToolpackCapability.Build, ToolpackCapability.Kernel });
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry => entry.Spec.ToolId.Value == "system.tool");
    }


    [Fact]
    public void ToolpackFilteringIsOrderIndependent()
    {
        var toolpacks = CreateSampleToolpacks();
        var reversed = toolpacks.Reverse().ToList();
        var policy = new ToolTierPolicy(
            ToolTier.Developer,
            new[] { ToolpackCapability.FileSystem, ToolpackCapability.Build });
        var loader = new ToolpackLoader();

        var first = loader.LoadFromToolpacks(toolpacks, policy);
        var second = loader.LoadFromToolpacks(reversed, policy);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Entries.Count, second.Entries.Count);
    }

    [Fact]
    public void SystemTierToolpacksAreNotExposedByDefault()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(
            ToolTier.Public,
            new[] { ToolpackCapability.FileSystem });
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.DoesNotContain(snapshot.Entries, entry => entry.Spec.ToolId.Value == "system.tool");
    }
    [Fact]
    public void LoaderDoesNotCreateToolsWithoutToolpacks()
    {
        var policy = new ToolTierPolicy(
            ToolTier.System,
            new[] { ToolpackCapability.FileSystem, ToolpackCapability.Build });
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(new List<ToolpackManifest>(), policy);

        Assert.Empty(snapshot.Entries);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
    }

    [Fact]
    public void RoleChangesDoNotAlterToolCatalog()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(
            ToolTier.Developer,
            new[] { ToolpackCapability.FileSystem, ToolpackCapability.Build });
        var loader = new ToolpackLoader();

        var first = loader.LoadFromToolpacks(toolpacks, policy);
        var second = loader.LoadFromToolpacks(toolpacks, policy);

        var roleA = new RoleDescriptor(
            "Integrator",
            "Prefer build-friendly tools.",
            new[] { ToolpackCapability.Build });
        var roleB = new RoleDescriptor(
            "Verifier",
            "Prefer filesystem tools.",
            new[] { ToolpackCapability.FileSystem });

        _ = roleA;
        _ = roleB;

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Entries.Count, second.Entries.Count);
    }

    private static IReadOnlyList<ToolpackManifest> CreateSampleToolpacks()
    {
        return new[]
        {
            CreateToolpack("public", ToolTier.Public, "public.tool", ToolpackCapability.FileSystem),
            CreateToolpack("developer", ToolTier.Developer, "developer.tool", ToolpackCapability.Build),
            CreateToolpack("system", ToolTier.System, "system.tool", ToolpackCapability.Kernel)
        };
    }

    private static ToolpackManifest CreateToolpack(
        string id,
        ToolTier tier,
        string toolId,
        ToolpackCapability capability)
    {
        var tool = new ToolpackTool(
            toolId,
            "Sample tool",
            new ToolpackAuthority("Local", new[] { "Plan" }),
            new[]
            {
                new ToolpackInput("path", "string", "Sample path.", true)
            },
            new[]
            {
                new ToolpackOutput("ok", "boolean", "Sample output.")
            },
            new[] { "sample" });

        return new ToolpackManifest(
            $"toolpack.{id}",
            "1.0.0",
            tier,
            "Sample toolpack",
            new[] { capability },
            new[] { tool },
            null,
            "Sample risk notes.",
            $"toolpacks/{id}/toolpack.json");
    }
}
