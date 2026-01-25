using System.Collections.Generic;

namespace Shoots.UI.AiHelp;

public static class UiSurfaceCatalog
{
    public static IReadOnlyList<string> RequiredSurfaceIds { get; } = new[]
    {
        "workspace",
        "execution",
        "execution-environment",
        "blueprints",
        "planner"
    };
}
