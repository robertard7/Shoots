namespace Shoots.UI.Startup;

public enum StartupFlowState
{
    Initial,
    EntryPathSelection,
    StartNewLanguage,
    StartNewName,
    StartNewDescription,
    StartNewProvider,
    StartNewEnvironment,
    StartNewConfirm,
    StartNewCompleted,
    ContinueExistingPath,
    ContinueExistingReview,
    ExploreMode
}
