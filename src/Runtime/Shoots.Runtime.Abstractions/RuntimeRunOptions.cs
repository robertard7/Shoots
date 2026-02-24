namespace Shoots.Runtime.Abstractions;

public enum DecisionWaitMode
{
    Halt = 0,
    Fallback = 1
}

public sealed record RuntimeRunOptions(
    ResumeMode ResumeMode = ResumeMode.None,
    string? InjectedDecisionDigest = null,
    bool DiscardWaiting = false,
    bool AllowPlanChangeOverride = false,
    int MaxDecisionWaits = 1,
    DecisionWaitMode DecisionWaitMode = DecisionWaitMode.Halt
);
