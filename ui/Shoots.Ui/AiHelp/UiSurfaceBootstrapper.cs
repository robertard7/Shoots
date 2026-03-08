using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.ExecutionRecords;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.ViewModels;

namespace Shoots.UI.AiHelp;

public static class UiSurfaceBootstrapper
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void RegisterAll(MainWindowViewModel? viewModel = null)
    {
        lock (Gate)
        {
            if (_registered)
                return;

            var registry = AiSurfaceRegistry.Current;
            var surfaces = viewModel is null
                ? BuildFallbackSurfaces()
                : viewModel.GetAiHelpSurfacesForRegistration();

            registry.Register(surfaces);
            _registered = true;
        }
    }

    private static IReadOnlyList<IAiHelpSurface> BuildFallbackSurfaces()
    {
        return new IAiHelpSurface[]
        {
            new WorkspaceAiHelpSurface(null, UiToolpackTier.Public),
            new ExecutionAiHelpSurface(null, "Idle", "No plan loaded."),
            new ExecutionEnvironmentAiHelpSurface(null, null, "No execution environment constraints."),
            new BlueprintCatalogAiHelpSurface(Array.Empty<string>()),
            new PlannerAiHelpSurface(null),
            new ToolExecutionCatalogAiHelpSurface(
                Array.Empty<ToolExecutionSessionViewModel>(),
                Array.Empty<ToolExecutionRecordViewModel>(),
                Array.Empty<ToolExecutionRecordViewModel>())
        };
    }
}
