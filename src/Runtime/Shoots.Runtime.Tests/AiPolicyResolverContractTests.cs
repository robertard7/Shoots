using System.Reflection;
using Shoots.Contracts.Core.AI;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class AiPolicyResolverContractTests
{
    [Fact]
    public void PolicyResolver_contract_is_frozen()
    {
        var method = typeof(IAiPolicyResolver).GetMethod("Resolve");
        Assert.NotNull(method);
        Assert.Equal(typeof(AiPresentationPolicy), method!.ReturnType);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(AiAccessRole), parameters[0].ParameterType);
    }
}
