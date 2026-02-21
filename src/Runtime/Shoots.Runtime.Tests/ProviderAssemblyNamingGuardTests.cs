using Shoots.ProviderAdapters.Bridge;
using Shoots.ProviderAdapters.Null;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class ProviderAssemblyNamingGuardTests
{
    [Fact]
    public void Runtime_related_assemblies_do_not_reference_legacy_shoots_providers_prefix()
    {
        var assemblies = new[]
        {
            typeof(RuntimeOrchestrator).Assembly,
            typeof(NullProviderClient).Assembly,
            typeof(ProviderRegistryFactory).Assembly
        };

        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is not null && reference.Name.StartsWith("Shoots" + ".Providers.", StringComparison.Ordinal));
        }
    }
}
