using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shoots.Runtime.Ui.Abstractions;
using Shoots.UI.AiHelp;
using Shoots.UI.Blueprints;
using Shoots.UI.Environment;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Intents;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.Settings;
using Shoots.UI.ViewModels;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class UiSurfaceCatalogTripwireTests
{
    [Fact]
    public void Ui_surface_catalog_covers_registered_surfaces()
    {
        var viewModel = BuildViewModel();
        var method = typeof(MainWindowViewModel).GetMethod(
            "BuildAiHelpSurfaces",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var surfaces = (IReadOnlyList<IAiHelpSurface>)method!
            .Invoke(viewModel, null)!;

        var surfaceIds = surfaces
            .Select(surface => surface.SurfaceId)
            .ToList();

        foreach (var required in UiSurfaceCatalog.RequiredSurfaceIds)
            Assert.Contains(required, surfaceIds);

        var allowed = UiSurfaceCatalog.RequiredSurfaceIds
            .Concat(UiSurfaceCatalog.OptionalSurfaceIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var surfaceId in surfaceIds)
        {
            var matchesPrefix = UiSurfaceCatalog.OptionalSurfaceIdPrefixes
                .Any(prefix => surfaceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (matchesPrefix)
                continue;

            Assert.True(allowed.Contains(surfaceId), $"Surface '{surfaceId}' missing from UiSurfaceCatalog.");
        }
    }

    private static MainWindowViewModel BuildViewModel()
    {
        var workspaceStore = new ProjectWorkspaceStore();
        var workspaceProvider = new ProjectWorkspaceProvider(workspaceStore);
        var workspaceShell = new WorkspaceShellService();
        var databaseIntentStore = new DatabaseIntentStore();
        var blueprintStore = new SystemBlueprintStore();
        var executionEnvironmentStore = new ExecutionEnvironmentSettingsStore();
        var aiPolicyStore = new AiPolicyStore();

        return new MainWindowViewModel(
            new NullExecutionCommandService(),
            new EnvironmentProfileService(),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            workspaceProvider,
            workspaceShell,
            databaseIntentStore,
            new ToolTierPrompt(),
            blueprintStore,
            executionEnvironmentStore,
            aiPolicyStore,
            new AiPanelVisibilityService(),
            new NullAiHelpFacade());
    }
}
