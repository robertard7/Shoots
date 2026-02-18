namespace Shoots.Runtime.Abstractions;

public enum RunOutcomeKind
{
    Completed,
    Halted,
    Waiting
}

public sealed record RunResumeState(
    string WorkOrderId,
    RunOutcomeKind LastOutcomeKind,
    DecisionGateWaitingInfo? LastWaitingReceipt,
    string? LastInjectedDecisionDigest,
    string? LastPlanHash,
    string? LastIntentTokenHash,
    int AttemptCounter
);
