using System.Threading.Tasks;
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

public sealed class ChatIntakeViewModelTests
{
    [Fact]
    public async Task Lock_workorder_freezes_editable_state_and_digest_is_deterministic()
    {
        var vm = BuildViewModel();
        vm.IntakeIntent = "Build deterministic intake front door";
        vm.IntakeTarget = "New Project";
        vm.IntakeAttachments = "a.txt\nb.txt";
        vm.IntakeStack = "dotnet";

        await vm.LockWorkOrderCommand.ExecuteAsync();
        var firstDigest = vm.JobSpecDigest;

        await vm.UnlockWorkOrderCommand.ExecuteAsync();
        vm.IntakeIntent = "Build deterministic intake front door";
        vm.IntakeTarget = "New Project";
        vm.IntakeAttachments = "b.txt\na.txt";
        vm.IntakeStack = "dotnet";

        await vm.LockWorkOrderCommand.ExecuteAsync();
        var secondDigest = vm.JobSpecDigest;

        Assert.True(vm.IsWorkOrderLocked);
        Assert.False(string.IsNullOrWhiteSpace(firstDigest));
        Assert.Equal(firstDigest, secondDigest);
    }

    [Fact]
    public async Task Waiting_resume_requires_explicit_user_action_and_does_not_auto_rerun()
    {
        var vm = BuildViewModel();
        vm.IntakeIntent = "Needs decision";

        await vm.LockWorkOrderCommand.ExecuteAsync();
        await vm.GeneratePlanCommand.ExecuteAsync();

        // NullExecutionCommandService always fails: view model should never auto-rerun.
        await vm.RunIntakePlanCommand.ExecuteAsync();

        Assert.False(vm.HasWaitingInfo);
        Assert.False(vm.ResumeInjectDecisionCommand.CanExecute(null));
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
