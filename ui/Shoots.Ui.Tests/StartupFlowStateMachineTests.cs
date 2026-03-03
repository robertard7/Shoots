using Shoots.UI.Startup;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class StartupFlowStateMachineTests
{
    [Fact]
    public void New_project_transitions_are_single_step_and_ordered()
    {
        var flow = new StartupFlowStateMachine();

        Assert.True(flow.TryBeginNewProject(out _));
        Assert.Equal(StartupFlowState.EntryPathSelection, flow.State);

        Assert.True(flow.TrySelectEntryPath(StartupEntryPath.StartSomethingNew, out _));
        Assert.Equal(StartupFlowState.StartNewLanguage, flow.State);

        Assert.True(flow.TrySetLanguage("dotnet", out _));
        Assert.Equal(StartupFlowState.StartNewName, flow.State);

        Assert.True(flow.TrySetProjectName("alpha", out _));
        Assert.Equal(StartupFlowState.StartNewDescription, flow.State);

        Assert.True(flow.TrySetDescription("desc", out _));
        Assert.Equal(StartupFlowState.StartNewProvider, flow.State);

        Assert.True(flow.TrySetProviderKind("Local", out _));
        Assert.Equal(StartupFlowState.StartNewEnvironment, flow.State);

        Assert.True(flow.TrySetEnvironmentId("host-local", out _));
        Assert.Equal(StartupFlowState.StartNewConfirm, flow.State);

        Assert.True(flow.TryConfirmCreate(out _));
        Assert.Equal(StartupFlowState.StartNewCompleted, flow.State);
    }

    [Fact]
    public void Illegal_transition_is_rejected_with_deterministic_error()
    {
        var flow = new StartupFlowStateMachine();

        var ok = flow.TrySetEnvironmentId("host-local", out var error);

        Assert.False(ok);
        Assert.Equal("Environment selection is not active.", error);
        Assert.Equal(StartupFlowState.Initial, flow.State);
    }
}
