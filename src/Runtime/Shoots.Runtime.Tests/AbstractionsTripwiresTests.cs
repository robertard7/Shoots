using System.Reflection;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class AbstractionsTripwiresTests
{
    [Fact]
    public void Abstractions_must_not_reference_Core_or_Loader_or_Language()
    {
        var asm = typeof(IRuntimeModule).Assembly;

        var referenced = asm
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("Shoots.Runtime.Core", referenced);
        Assert.DoesNotContain("Shoots.Runtime.Loader", referenced);
        Assert.DoesNotContain("Shoots.Runtime.Language", referenced);
    }

    [Fact]
    public void Abstractions_must_not_expose_RuntimeEngine()
    {
        // Tripwire: if someone smuggles engine concerns into Abstractions, fail loudly.
        var asm = typeof(IRuntimeModule).Assembly;

        var bad = asm.GetTypes()
            .Any(t => string.Equals(t.Name, "RuntimeEngine", StringComparison.OrdinalIgnoreCase));

        Assert.False(bad);
    }
}
