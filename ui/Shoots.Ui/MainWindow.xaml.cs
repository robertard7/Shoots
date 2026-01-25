using System.Windows;
using Shoots.UI.Environment;
using Shoots.UI.Intents;
using Shoots.UI.Blueprints;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.Settings;
using Shoots.UI.ViewModels;

namespace Shoots.UI;

// DO NOT ADD LOGIC HERE.
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var workspaceStore = new ProjectWorkspaceStore();
        var workspaceProvider = new ProjectWorkspaceProvider(workspaceStore);
        var workspaceShell = new WorkspaceShellService();
        var databaseIntentStore = new DatabaseIntentStore();
        var blueprintStore = new SystemBlueprintStore();
        var executionEnvironmentStore = new ExecutionEnvironmentSettingsStore();
        var aiPolicyStore = new AiPolicyStore();
        DataContext = new MainWindowViewModel(
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
