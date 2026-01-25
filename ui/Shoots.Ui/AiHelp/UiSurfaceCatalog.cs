using System;
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
        "planner",
        "tool-executions"
    };

    public static IReadOnlyList<string> OptionalSurfaceIds { get; } = Array.Empty<string>();

    public static IReadOnlyList<string> OptionalSurfaceIdPrefixes { get; } = new[]
    {
        "blueprint:",
        "ter:"
    };
}
