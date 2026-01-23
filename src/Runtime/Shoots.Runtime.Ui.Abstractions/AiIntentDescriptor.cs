namespace Shoots.Runtime.Ui.Abstractions;

public enum AiIntentType
{
    Explain,
    Validate,
    Compare,
    Diagnose,
    Predict,
    Risk,
    Suggest,
    Modify
}

public enum AiIntentScope
{
    Blueprint,
    Planner,
    Execution,
    ToolExecution,
    Provider,
    UI
}

public sealed record AiIntentDescriptor(
    AiIntentType Type,
    AiIntentScope Scope,
    string? TargetId = null
);
