using Xunit;
using Shoots.Host.Abstractions;

namespace Shoots.Host.Tests;

public sealed class HostContractsShapeTests
{
    [Fact]
    public void Host_policy_options_shape_is_frozen()
    {
        var names = typeof(HostPolicyOptions).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "ProviderTimeout", "MaxRetries", "AllowRemote", "AllowLocal", "AllowCloudAssist" }, names);
    }

    [Fact]
    public void Decision_injection_request_shape_is_frozen()
    {
        var names = typeof(DecisionInjectionRequest).GetProperties().Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "WorkOrderId", "PlanHash", "RouteGateId", "ToolId", "BindingsJsonCanonical" }, names);
    }
}
