namespace Shoots.Runtime.Abstractions;

public sealed record RuntimeRunOptions(
    ResumeMode ResumeMode = ResumeMode.None,
    string? InjectedDecisionDigest = null,
    bool DiscardWaiting = false,
    bool AllowPlanChangeOverride = false
);
