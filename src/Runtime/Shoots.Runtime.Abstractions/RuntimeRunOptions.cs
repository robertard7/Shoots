namespace Shoots.Runtime.Abstractions;

public enum DecisionWaitMode
{
    Fail = 0,
    Fallback = 1
}

public sealed record RuntimeRunOptions(
    ResumeMode ResumeMode = ResumeMode.None,
    string? InjectedDecisionDigest = null,
    bool DiscardWaiting = false,
    bool AllowPlanChangeOverride = false,
    int MaxDecisionWaits = 0,
    DecisionWaitMode DecisionWaitMode = DecisionWaitMode.Fail
);
