using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        var runJsonPath = Path.Combine(runPath, "run.json");

        var planHash = ComputePlanHash(plan);
        var toolCatalogHash = _toolRegistry.CatalogHash;
        var workspaceDescriptorHash = ComputeWorkspaceDescriptorHash(project);

        EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "info", "RUN_HEADER", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["plan_hash"] = planHash,
            ["tool_catalog_hash"] = toolCatalogHash,
            ["workspace_descriptor_hash"] = workspaceDescriptorHash
        }));

        var steps = new List<RunStep>();
        PersistRun(RunStates.Pending);
        PersistRun(RunStates.Running);

        var environment = CaptureEnvironment(runPath);
        EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "info", "ENV_CAPTURED", new Dictionary<string, string>
        {
            ["environment_hash"] = environment.Hash
        }));

        EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "step", "PLAN_STARTED", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["plan_id"] = plan.PlanId,
            ["plan_hash"] = planHash
        }));

        var status = RunStates.Completed;

        foreach (var step in plan.Steps)
        {
            EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "step", "STEP_STARTED", new Dictionary<string, string>
            {
                ["step_id"] = step.StepId,
                ["tool_id"] = step.ToolId
            }));

            var result = _runtimeBridge.ExecuteStep(step, project, narrate);
            if (string.Equals(result.Status, RunStates.Completed, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(result.OutputPath))
                {
                    _artifactManager.Capture(runPath, step.StepId, result.OutputPath);
                }

                steps.Add(new RunStep(step.StepId, step.ToolId, RunStates.Completed, result.OutputPath, null));
                EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "result", "STEP_COMPLETED", new Dictionary<string, string>
                {
                    ["step_id"] = step.StepId,
                    ["output_path"] = result.OutputPath ?? string.Empty
                }));
                continue;
            }

            status = RunStates.Failed;
            steps.Add(new RunStep(step.StepId, step.ToolId, RunStates.Failed, null, result.Error));
            File.WriteAllText(Path.Combine(runPath, "rollback.marker"), $"step={step.StepId}; error={result.Error}");
            EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "error", "STEP_FAILED", new Dictionary<string, string>
            {
                ["step_id"] = step.StepId,
                ["error"] = result.Error ?? "unknown"
            }));
            break;
        }

        var artifactJsonPath = _artifactManager.WriteMetadata(runPath, planHash, toolCatalogHash);
        var run = PersistRun(status);

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

        RunModel PersistRun(string runStatus)
        {
            var model = new RunModel(
                runId,
                project.ProjectId,
                plan.PlanId,
                planHash,
                toolCatalogHash,
                workspaceDescriptorHash,
                DateTimeOffset.UtcNow,
                runStatus,
                steps);
            File.WriteAllText(runJsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            return model;
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

    private static string ComputeWorkspaceDescriptorHash(ProjectModel project)
    {
        var canonical = $"{project.ProjectId}|{project.Name}|{project.WorkspacePath}|{project.CreatedUtc:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static EnvironmentCapture CaptureEnvironment(string runPath)
    {
        var dotnetVersion = RunCommand("dotnet", "--version");
        var gitVersion = RunCommand("git", "--version");
        var snapshot = new EnvironmentSnapshot(
            Environment.OSVersion.ToString(),
            dotnetVersion,
            gitVersion,
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        var environmentPath = Path.Combine(runPath, "environment.json");
        File.WriteAllText(environmentPath, json);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new EnvironmentCapture(hash, environmentPath);
    }

    private static string RunCommand(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record EnvironmentCapture(string Hash, string Path);

    private sealed record EnvironmentSnapshot(
        string OsVersion,
        string DotnetSdkVersion,
        string GitVersion,
        string PathSnapshot,
        string WorkingDirectory,
        DateTimeOffset CapturedUtc);
}
