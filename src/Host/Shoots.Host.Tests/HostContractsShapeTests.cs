using Xunit;
using Shoots.Host.Abstractions;

namespace Shoots.Host.Tests;

public sealed class HostContractsShapeTests
{
    [Fact]
    public void Host_policy_options_shape_is_frozen()
    {
        var names = typeof(HostPolicyOptions).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "ProviderTimeout", "MaxRetries", "AllowRemote", "AllowLocal", "AllowCloudAssist", "AllowedProviderIds", "AllowedModelIds", "AllowedToolIds", "DeniedToolIds", "Default" }, names);
    }

    [Fact]
    public void Decision_injection_request_shape_is_frozen()
    {
        var names = typeof(DecisionInjectionRequest).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "WorkOrderId", "PlanHash", "RouteGateId", "ToolId", "BindingsJsonCanonical" }, names);
    }

    [Fact]
    public void Host_policy_default_values_are_frozen()
    {
        var d = HostPolicyOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(30), d.ProviderTimeout);
        Assert.Equal(1, d.MaxRetries);
        Assert.True(d.AllowRemote);
        Assert.True(d.AllowLocal);
        Assert.False(d.AllowCloudAssist);
        Assert.Empty(d.AllowedProviderIds);
        Assert.Empty(d.AllowedModelIds);
        Assert.Empty(d.AllowedToolIds);
        Assert.Empty(d.DeniedToolIds);
    }

    [Fact]
    public void Provider_policy_method_set_is_frozen()
    {
        var names = typeof(IProviderPolicy).GetMethods().Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Select" }, names);
    }

    [Fact]
    public void Model_catalog_method_set_is_frozen()
    {
        var names = typeof(IModelCatalog).GetMethods().Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "ListModels" }, names);
    }

    [Fact]
    public void Host_resume_intent_mode_order_is_frozen()
    {
        var names = Enum.GetNames<HostResumeIntentMode>();
        Assert.Equal(new[] { "None", "InjectDecision", "OverridePlanChange", "DiscardWaitingStartOver" }, names);

        Assert.Equal(0, (int)HostResumeIntentMode.None);
        Assert.Equal(1, (int)HostResumeIntentMode.InjectDecision);
        Assert.Equal(2, (int)HostResumeIntentMode.OverridePlanChange);
        Assert.Equal(3, (int)HostResumeIntentMode.DiscardWaitingStartOver);
    }

    [Fact]
    public void Host_resume_intent_shape_is_frozen()
    {
        var names = typeof(HostResumeIntent).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "Mode" }, names);
    }


    [Fact]
    public void Tool_policy_denies_explicit_tool_ids_deterministically()
    {
        var policy = HostPolicyOptions.Default with
        {
            AllowedToolIds = new[] { "tools.alpha", "tools.beta" },
            DeniedToolIds = new[] { "tools.beta" }
        };

        Assert.True(Shoots.Host.Core.HostPolicyGuards.IsToolAllowed(policy, "tools.alpha"));
        Assert.False(Shoots.Host.Core.HostPolicyGuards.IsToolAllowed(policy, "tools.beta"));
        Assert.False(Shoots.Host.Core.HostPolicyGuards.IsToolAllowed(policy, "tools.gamma"));
    }

}
