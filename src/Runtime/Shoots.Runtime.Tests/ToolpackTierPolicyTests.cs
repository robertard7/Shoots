using System.Collections.Generic;
using Shoots.Runtime.Loader.Toolpacks;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ToolpackTierPolicyTests
{
    [Fact]
    public void PublicTierFiltersToPublicToolpacks()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(ToolTier.Public);
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.Single(snapshot.Entries);
        Assert.Equal("public.tool", snapshot.Entries[0].Spec.ToolId.Value);
    }

    [Fact]
    public void DeveloperTierIncludesPublicAndDeveloperToolpacks()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(ToolTier.Developer);
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry => entry.Spec.ToolId.Value == "public.tool");
        Assert.Contains(snapshot.Entries, entry => entry.Spec.ToolId.Value == "developer.tool");
    }

    [Fact]
    public void SystemTierIncludesAllToolpacks()
    {
        var toolpacks = CreateSampleToolpacks();
        var policy = new ToolTierPolicy(ToolTier.System);
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(toolpacks, policy);

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry => entry.Spec.ToolId.Value == "system.tool");
    }

    [Fact]
    public void LoaderDoesNotCreateToolsWithoutToolpacks()
    {
        var policy = new ToolTierPolicy(ToolTier.System);
        var loader = new ToolpackLoader();

        var snapshot = loader.LoadFromToolpacks(new List<ToolpackManifest>(), policy);

        Assert.Empty(snapshot.Entries);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
    }

    private static IReadOnlyList<ToolpackManifest> CreateSampleToolpacks()
    {
        return new[]
        {
            CreateToolpack("public", ToolTier.Public, "public.tool"),
            CreateToolpack("developer", ToolTier.Developer, "developer.tool"),
            CreateToolpack("system", ToolTier.System, "system.tool")
        };
    }

    private static ToolpackManifest CreateToolpack(string id, ToolTier tier, string toolId)
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
            new[] { tool },
            null,
            "Sample risk notes.",
            $"toolpacks/{id}/toolpack.json");
    }
}
