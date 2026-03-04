using Shoots.UI.Services.AiHelp;
using Xunit;

namespace Shoots.Ui.Tests;

public sealed class SimpleAiHelpSurfaceTests
{
    [Fact]
    public void Intent_ReturnsDeterministicFallback_WhenInputIsEmpty()
    {
        var first = SimpleAiHelpSurface.Intent(string.Empty, string.Empty, string.Empty);
        var second = SimpleAiHelpSurface.Intent(string.Empty, string.Empty, string.Empty);

        Assert.Equal(first, second);
    }
}
