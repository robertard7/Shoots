using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public sealed record ExecutionRequest(
    string ContractVersion,
    string ProjectId,
    string WorkspacePath,
    ExecutionPlan Plan,
    string PlannerSource,
    string RuntimeBridge,
    string Provider,
    string HostTransport);

public sealed record ExecutionPlan(
    string PlanId,
    string PlanHash,
    IReadOnlyList<ExecutionStep> Steps);

public sealed record ExecutionStep(
    string StepId,
    string ToolId,
    IReadOnlyDictionary<string, string> Args,
    string OutputPath);

public sealed record ToolInvocationRecord(
    string StepId,
    string ToolId,
    string Status,
    string? OutputPath,
    string? Error);

public sealed record ExecutionEvidence(
    string? EnvironmentHash,
    string? ManifestHash,
    string? NarratorHash,
    string? TranscriptHash,
    string? EvidenceBundleHash,
    string? ReproWarning);

public sealed record ExecutionResult(
    string ContractVersion,
    string RunId,
    string Status,
    IReadOnlyList<ToolInvocationRecord> ToolInvocations,
    ExecutionEvidence Evidence,
    string PlannerSource,
    string RuntimeBridge,
    string Provider,
    string HostTransport);

public static class ExecutionContractAdapter
{
    public static ExecutionRequest ToExecutionRequest(
        PlanModel plan,
        ProjectModel project,
        string plannerSource,
        string runtimeBridge,
        string provider,
        string hostTransport,
        string planHash)
    {
        return new ExecutionRequest(
            ExecutionContract.Version,
            project.ProjectId,
            project.WorkspacePath,
            new ExecutionPlan(
                plan.PlanId,
                planHash,
                plan.Steps.Select(static step => new ExecutionStep(step.StepId, step.ToolId, step.Args, step.OutputPath)).ToArray()),
            plannerSource,
            runtimeBridge,
            provider,
            hostTransport);
    }

    public static ExecutionResult ToExecutionResult(RunModel run)
    {
        return new ExecutionResult(
            run.ContractVersion,
            run.RunId,
            run.Status,
            run.Steps.Select(static step => new ToolInvocationRecord(step.StepId, step.ToolId, step.Status, step.OutputPath, step.Error)).ToArray(),
            new ExecutionEvidence(run.EnvironmentHash, run.ManifestHash, run.NarratorHash, run.TranscriptHash, run.EvidenceBundleHash, run.ReproWarning),
            run.PlannerSource,
            run.RuntimeBridge,
            run.Provider,
            run.HostTransport);
    }
}
