#nullable enable

namespace Shoots.UI.Services;

public enum ExecutionOutcome
{
    Unknown = 0,
    Started = 1,
    Completed = 2,
    Cancelled = 3,
    Waiting = 4,
    Failed = 5
}

public sealed record ExecutionStartResult(
    ExecutionOutcome Outcome,
    string WorkOrderId,
    string PlanId,
    string PlanHash,
    string? Message);

public sealed record ExecutionVersionInfo(
    int Major,
    int Minor,
    int Patch,
    string Label);

public sealed record ExecutionStatusSnapshot(
    ExecutionVersionInfo Version,
    string PolicyHash,
    string StateLabel);