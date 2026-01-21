using System.Linq;
using Shoots.UI;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class UiBoundaryTests
{
    [Fact]
    public void UiDoesNotReferenceRuntimeCore()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "Shoots.Runtime.Core");
    }
}
