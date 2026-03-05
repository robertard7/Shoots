using System.Collections.Generic;
using Shoots.UI.Diagnostics;
using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public sealed record RuntimeBridgeStepResult(
    string StepId,
    string ToolId,
    string Status,
    string? OutputPath,
    string? Error,
    IReadOnlyDictionary<string, string>? Logs = null
);

public interface IRuntimeBridge
{
    RuntimeBridgeStepResult ExecuteStep(PlanStep step, ProjectModel project, Action<NarrationEvent>? narrate = null);
}
