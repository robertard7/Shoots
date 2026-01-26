namespace Shoots.UI.Startup;

public sealed class StartupFlowStateMachine
{
    public StartupFlowState State { get; private set; } = StartupFlowState.Initial;

    public StartupEntryPath? EntryPath { get; private set; }

    public string? SelectedLanguage { get; private set; }

    public string? ProjectName { get; private set; }

    public string? Description { get; private set; }

    public string? ExistingProjectPath { get; private set; }

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
        State = entryPath switch
        {
            StartupEntryPath.StartSomethingNew => StartupFlowState.StartNewLanguage,
            StartupEntryPath.ContinueExistingProject => StartupFlowState.ContinueExistingPath,
            StartupEntryPath.ExploreIdea => StartupFlowState.ExploreMode,
            _ => StartupFlowState.EntryPathSelection
        };
        error = null;
        return true;
    }

    public bool TrySetLanguage(string language, out string? error)
    {
        if (State != StartupFlowState.StartNewLanguage)
        {
            error = "Language selection is not active.";
            return false;
        }

        SelectedLanguage = language;
        State = StartupFlowState.StartNewName;
        error = null;
        return true;
    }

    public bool TrySetProjectName(string? projectName, out string? error)
    {
        if (State != StartupFlowState.StartNewName)
        {
            error = "Project naming is not active.";
            return false;
        }

        ProjectName = projectName;
        State = StartupFlowState.StartNewDescription;
        error = null;
        return true;
    }

    public bool TrySetDescription(string description, out string? error)
    {
        if (State != StartupFlowState.StartNewDescription)
        {
            error = "Description capture is not active.";
            return false;
        }

        Description = description;
        State = StartupFlowState.StartNewConfirm;
        error = null;
        return true;
    }

    public bool TryConfirmCreate(out string? error)
    {
        if (State != StartupFlowState.StartNewConfirm)
        {
            error = "Confirmation is not active.";
            return false;
        }

        State = StartupFlowState.StartNewCompleted;
        error = null;
        return true;
    }

    public bool TrySetExistingProjectPath(string path, out string? error)
    {
        if (State != StartupFlowState.ContinueExistingPath)
        {
            error = "Existing project selection is not active.";
            return false;
        }

        ExistingProjectPath = path;
        State = StartupFlowState.ContinueExistingReview;
        error = null;
        return true;
    }

    public void Reset()
    {
        State = StartupFlowState.Initial;
        EntryPath = null;
        SelectedLanguage = null;
        ProjectName = null;
        Description = null;
        ExistingProjectPath = null;
    }
}
