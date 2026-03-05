using System.Collections.Generic;

namespace Shoots.UI.Builder;

public enum PlanSourceType
{
    Demo,
    Mermaid,
    Manual
}

public sealed record PlanStep(
    string StepId,
    string ToolId,
    IReadOnlyDictionary<string, string> Args,
    string OutputPath
);

public sealed record PlanModel(
    string PlanId,
    PlanSourceType SourceType,
    IReadOnlyList<PlanStep> Steps
);
