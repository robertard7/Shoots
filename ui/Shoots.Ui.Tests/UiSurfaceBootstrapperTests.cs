using Shoots.UI.AiHelp;
using Shoots.UI.Services;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class UiSurfaceBootstrapperTests
{
    [Fact]
    public void RegisterAll_registers_all_required_surfaces()
    {
        UiSurfaceBootstrapper.RegisterAll();

        var missing = AiSurfaceRegistry.Current.GetMissingSurfaceIds(UiSurfaceCatalog.RequiredSurfaceIds);
        Assert.Empty(missing);
    }
}
