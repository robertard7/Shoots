using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ProviderInterfaceGuardTests
{
    [Fact]
    public void Provider_adapter_signature_is_frozen()
    {
        var method = typeof(IAiProviderAdapter).GetMethod("RequestDecision");
        Assert.NotNull(method);

        var parameters = method!.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal(5, parameters.Length);
        Assert.Equal(typeof(WorkOrder), parameters[0]);
        Assert.Equal(typeof(RouteStep), parameters[1]);
        Assert.Equal(typeof(string), parameters[2]);
        Assert.Equal(typeof(string), parameters[3]);
        Assert.Equal(typeof(ToolCatalogSnapshot), parameters[4]);
        Assert.Equal(typeof(ToolSelectionDecision), method.ReturnType);
    }
}
