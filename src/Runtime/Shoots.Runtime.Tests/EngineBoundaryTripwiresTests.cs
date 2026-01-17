using System.Reflection;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class EngineBoundaryTripwiresTests
{
    [Fact]
    public void RuntimeEngine_must_have_exactly_one_public_instance_Execute_method()
    {
        var t = typeof(RuntimeEngine);

        var execute = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Execute")
            .ToArray();

        Assert.Single(execute);

        // Tripwire: if someone adds overloads, “helper executes”, etc., you catch it.
        // The runtime stays a single entrypoint system.
    }

    [Fact]
    public void RuntimeEngine_constructor_must_not_be_parameterless()
    {
        // Tripwire: engine must not become "ambient" or auto-wired.
        var t = typeof(RuntimeEngine);

        var parameterless = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(c => c.GetParameters().Length == 0);

        Assert.False(parameterless);
    }
}
