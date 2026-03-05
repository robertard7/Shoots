using System;
using System.Collections.Generic;
using Shoots.UI.ExecutionRecords;
using Shoots.UI.Projects;
using Shoots.UI.Services;

namespace Shoots.UI.AiHelp;

public static class UiSurfaceBootstrapper
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void RegisterAll()
    {
        lock (Gate)
        {
            if (_registered)
                return;

            var registry = AiSurfaceRegistry.Current;
            registry.Register(new WorkspaceAiHelpSurface(null, UiToolpackTier.Public));
            registry.Register(new ExecutionAiHelpSurface(null, "Idle", "No plan loaded."));
            registry.Register(new ExecutionEnvironmentAiHelpSurface(null, null, "No execution environment constraints."));
            registry.Register(new BlueprintCatalogAiHelpSurface(Array.Empty<string>()));
            registry.Register(new PlannerAiHelpSurface(null));
            registry.Register(new ToolExecutionCatalogAiHelpSurface(
                Array.Empty<ToolExecutionSessionViewModel>(),
                Array.Empty<ToolExecutionRecordViewModel>(),
                Array.Empty<ToolExecutionRecordViewModel>()));

            _registered = true;
        }
    }
}
