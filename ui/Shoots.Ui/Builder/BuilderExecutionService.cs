using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Shoots.UI.Diagnostics;
using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public sealed class BuilderExecutionService
{
    private readonly IRuntimeBridge _runtimeBridge;
    private readonly ArtifactManager _artifactManager;

    public BuilderExecutionService(IRuntimeBridge runtimeBridge, ArtifactManager artifactManager)
    {
        _runtimeBridge = runtimeBridge;
        _artifactManager = artifactManager;
    }

    public BuilderExecutionResult Execute(PlanModel plan, ProjectModel project, Action<NarrationEvent>? narrate = null)
    {
        var runId = NextRunId(project.WorkspacePath);
        var runPath = Path.Combine(project.WorkspacePath, "runs", runId);
        Directory.CreateDirectory(runPath);

        var steps = new List<RunStep>();
        var status = "completed";

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "step", "PLAN_STARTED", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["plan_id"] = plan.PlanId
        }));

        foreach (var step in plan.Steps)
        {
            narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "step", "STEP_STARTED", new Dictionary<string, string>
            {
                ["step_id"] = step.StepId,
                ["tool_id"] = step.ToolId
            }));

            var result = _runtimeBridge.ExecuteStep(step, project, narrate);
            if (string.Equals(result.Status, "completed", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(result.OutputPath))
                {
                    _artifactManager.Capture(runPath, step.StepId, result.OutputPath);
                }

                steps.Add(new RunStep(step.StepId, step.ToolId, "completed", result.OutputPath, null));
                narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "result", "STEP_COMPLETED", new Dictionary<string, string>
                {
                    ["step_id"] = step.StepId,
                    ["output_path"] = result.OutputPath ?? string.Empty
                }));
                continue;
            }

            status = "failed";
            steps.Add(new RunStep(step.StepId, step.ToolId, "failed", null, result.Error));
            File.WriteAllText(Path.Combine(runPath, "rollback.marker"), $"step={step.StepId}; error={result.Error}");
            narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "error", "STEP_FAILED", new Dictionary<string, string>
            {
                ["step_id"] = step.StepId,
                ["error"] = result.Error ?? "unknown"
            }));
            break;
        }

        var run = new RunModel(runId, project.ProjectId, plan.PlanId, DateTimeOffset.UtcNow, status, steps);
        var runJsonPath = Path.Combine(runPath, "run.json");
        File.WriteAllText(runJsonPath, JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        var artifactJsonPath = _artifactManager.WriteMetadata(runPath);

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "result", "RUN_COMPLETED", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["status"] = status
        }));

        return new BuilderExecutionResult(run, runPath, runJsonPath, artifactJsonPath);
    }

    private static string NextRunId(string workspacePath)
    {
        var counterPath = Path.Combine(workspacePath, ".builder-run-counter");
        var current = 0;
        if (File.Exists(counterPath) && int.TryParse(File.ReadAllText(counterPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            current = parsed;
        }

        var next = current + 1;
        File.WriteAllText(counterPath, next.ToString(CultureInfo.InvariantCulture));
        return next.ToString("D6", CultureInfo.InvariantCulture);
    }
}
