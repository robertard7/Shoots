namespace Shoots.UI.Startup;

public sealed class StartupFlowStateMachine
{
    public StartupFlowState State { get; private set; } = StartupFlowState.Initial;

    public StartupEntryPath? EntryPath { get; private set; }

    public bool TryBeginNewProject(out string? error)
    {
        if (State != StartupFlowState.Initial)
        {
            error = "Startup flow has already begun.";
            return false;
        }

        State = StartupFlowState.EntryPathSelection;
        error = null;
        return true;
    }

    public bool TrySelectEntryPath(StartupEntryPath entryPath, out string? error)
    {
        if (State != StartupFlowState.EntryPathSelection)
        {
            error = "Entry path selection is not active.";
            return false;
        }

        EntryPath = entryPath;
        State = StartupFlowState.EntryPathChosen;
        error = null;
        return true;
    }

    public void Reset()
    {
        State = StartupFlowState.Initial;
        EntryPath = null;
    }
}
