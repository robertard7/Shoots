namespace Shoots.UI.Startup;

public enum StartupFlowState
{
    Initial,
    EntryPathSelection,
    StartNewLanguage,
    StartNewName,
    StartNewDescription,
    StartNewConfirm,
    StartNewCompleted,
    ContinueExistingPath,
    ContinueExistingReview,
    ExploreMode
}
