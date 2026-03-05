using Shoots.UI.Builder;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void Catalog_contains_required_demo_tools()
    {
        var registry = new ToolRegistry("etc/ui.tools.catalog.json");

        Assert.True(registry.Contains("write_text"));
        Assert.True(registry.Contains("create_directory"));
        Assert.True(registry.Contains("copy_file"));
        Assert.True(registry.Contains("dotnet.build"));
    }
}
