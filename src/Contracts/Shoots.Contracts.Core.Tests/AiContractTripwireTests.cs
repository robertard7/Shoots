using System.Linq;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Contracts.Core.Tests;

public sealed class AiContractTripwireTests
{
    [Fact]
    public void Ai_contract_types_are_interfaces()
    {
        Assert.True(typeof(IAiSurface).IsInterface);
        Assert.True(typeof(IAiIntent).IsInterface);
        Assert.True(typeof(IAiNarration).IsInterface);
        Assert.True(typeof(IAiNarrator).IsInterface);
    }

    [Fact]
    public void Ai_narrator_requires_surface_and_intent()
    {
        var method = typeof(IAiNarrator)
            .GetMethods()
            .Single(m => m.Name == "Narrate");

        var parameterTypes = method
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IAiSurface), parameterTypes);
        Assert.Contains(typeof(IAiIntent), parameterTypes);
    }

    [Fact]
    public void Contracts_core_must_not_reference_runtime()
    {
        var assembly = typeof(IAiSurface).Assembly;

        var referenced = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        Assert.DoesNotContain(referenced, name => name!.StartsWith("Shoots.Runtime"));
    }
}
