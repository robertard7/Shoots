#nullable enable

namespace Shoots.Ui.ViewModels;

public enum ExecutionState
{
    None = 0,
    Idle = 1,
    Running = 2,
    Waiting = 3,
    Completed = 4,
    Cancelled = 5,
    Failed = 6
}