using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.UI.Diagnostics;
using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public sealed class BuilderExecutionService
{
    private readonly IRuntimeBridge _runtimeBridge;
    private readonly ArtifactManager _artifactManager;
    private readonly ToolRegistry _toolRegistry;

    public BuilderExecutionService(IRuntimeBridge runtimeBridge, ArtifactManager artifactManager, ToolRegistry toolRegistry)
    {
        _runtimeBridge = runtimeBridge;
        _artifactManager = artifactManager;
        _toolRegistry = toolRegistry;
    }

    public BuilderExecutionResult Execute(PlanModel plan, ProjectModel project, Action<NarrationEvent>? narrate = null)
    {
        _artifactManager.Reset();

        var runId = NextRunId(project.WorkspacePath);
        var runPath = Path.Combine(project.WorkspacePath, "runs", runId);
        Directory.CreateDirectory(runPath);
        var narratorPath = Path.Combine(runPath, "narrator.jsonl");

        var planHash = ComputePlanHash(plan);
        var toolCatalogHash = _toolRegistry.CatalogHash;
        EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "info", "RUN_HEADER", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["plan_hash"] = planHash,
            ["tool_catalog_hash"] = toolCatalogHash
        }));

        var steps = new List<RunStep>();
        var status = "completed";

        EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "step", "PLAN_STARTED", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["plan_id"] = plan.PlanId,
            ["plan_hash"] = planHash
        }));

        foreach (var step in plan.Steps)
        {
            EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "step", "STEP_STARTED", new Dictionary<string, string>
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
                EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "result", "STEP_COMPLETED", new Dictionary<string, string>
                {
                    ["step_id"] = step.StepId,
                    ["output_path"] = result.OutputPath ?? string.Empty
                }));
                continue;
            }

            status = "failed";
            steps.Add(new RunStep(step.StepId, step.ToolId, "failed", null, result.Error));
            File.WriteAllText(Path.Combine(runPath, "rollback.marker"), $"step={step.StepId}; error={result.Error}");
            EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "error", "STEP_FAILED", new Dictionary<string, string>
            {
                ["step_id"] = step.StepId,
                ["error"] = result.Error ?? "unknown"
            }));
            break;
        }

        var run = new RunModel(runId, project.ProjectId, plan.PlanId, planHash, toolCatalogHash, DateTimeOffset.UtcNow, status, steps);
        var runJsonPath = Path.Combine(runPath, "run.json");
        File.WriteAllText(runJsonPath, JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        var artifactJsonPath = _artifactManager.WriteMetadata(runPath, planHash, toolCatalogHash);

        EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "result", "RUN_COMPLETED", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["status"] = status,
            ["plan_hash"] = planHash,
            ["tool_catalog_hash"] = toolCatalogHash
        }));

        return new BuilderExecutionResult(run, runPath, runJsonPath, artifactJsonPath);

        void EmitNarration(NarrationEvent evt)
        {
            narrate?.Invoke(evt);
            File.AppendAllLines(narratorPath, new[] { JsonSerializer.Serialize(evt) });
        }
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

    private static string ComputePlanHash(PlanModel plan)
    {
        var canonical = new StringBuilder();
        canonical.Append(plan.PlanId).Append('|').Append(plan.SourceType);

        foreach (var step in plan.Steps)
        {
            canonical.Append('|').Append(step.StepId).Append('|').Append(step.ToolId).Append('|').Append(step.OutputPath);
            foreach (var arg in step.Args.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
            {
                canonical.Append('|').Append(arg.Key).Append('=').Append(arg.Value);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
