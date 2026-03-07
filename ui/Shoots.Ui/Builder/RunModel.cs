using System;
using System.Collections.Generic;

namespace Shoots.UI.Builder;

public static class RunStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Failed = "failed";
    public const string Completed = "completed";
    public const string FailedCrash = "failed_crash";
}

public sealed record RunStep(
    string StepId,
    string ToolId,
    string Status,
    string? OutputPath,
    string? Error
);

public sealed record RunModel(
    string RunId,
    string ProjectId,
    string PlanId,
    string PlanHash,
    string ToolCatalogHash,
    string WorkspaceDescriptorHash,
    DateTimeOffset CreatedUtc,
    string Status,
    IReadOnlyList<RunStep> Steps
);

public sealed record BuilderExecutionResult(
    RunModel Run,
    string RunPath,
    string RunJsonPath,
    string ArtifactJsonPath
);
