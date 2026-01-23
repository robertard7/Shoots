namespace Shoots.Runtime.Ui.Abstractions;

public enum AiIntentType
{
    Explain,
    Diagnose,
    Suggest,
    Modify
}

public enum AiIntentScope
{
    Blueprint,
    Execution,
    Provider,
    UI
}

public sealed record AiIntentDescriptor(
    AiIntentType Type,
    AiIntentScope Scope,
    string? TargetId = null
);
