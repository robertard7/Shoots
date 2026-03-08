using System;
using System.Collections.Generic;
using Shoots.UI.Diagnostics;
using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public sealed class RuntimeBridgeLocal : IRuntimeBridge
{
    private readonly ToolExecutionService _toolExecutionService;

    public RuntimeBridgeLocal(ToolExecutionService toolExecutionService)
    {
        _toolExecutionService = toolExecutionService;
    }

    public RuntimeBridgeStepResult ExecuteStep(PlanStep step, ProjectModel project, Action<NarrationEvent>? narrate = null)
    {
        try
        {
            var outputPath = _toolExecutionService.ExecuteStep(step, project.WorkspacePath);
            return new RuntimeBridgeStepResult(step.StepId, step.ToolId, "completed", outputPath, null);
        }
        catch (Exception ex)
        {
            narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "error", "RUNTIME_BRIDGE_STEP_FAILED", new Dictionary<string, string>
            {
                ["step_id"] = step.StepId,
                ["tool_id"] = step.ToolId,
                ["error"] = ex.Message
            }));
            return new RuntimeBridgeStepResult(step.StepId, step.ToolId, "failed", null, ex.Message);
        }
    }
}
