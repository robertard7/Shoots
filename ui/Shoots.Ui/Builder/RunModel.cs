using System;
using System.Collections.Generic;

namespace Shoots.UI.Builder;

public static class ExecutionContract
{
    public const string Version = "ui-runtime-v1";
}

public static class RunStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Failed = "failed";
    public const string Completed = "completed";
    public const string FailedCrash = "failed_crash";
    public const string FailedDrift = "failed_drift";
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
    IReadOnlyList<RunStep> Steps,
    string ContractVersion,
    string PlannerSource,
    string RuntimeBridge,
    string Provider,
    string HostTransport,
    string? EnvironmentHash = null,
    string? ManifestHash = null,
    string? NarratorHash = null,
    string? TranscriptHash = null,
    string? EvidenceBundleHash = null,
    string? ReproWarning = null,
    string? HostResponseOutcome = null,
    string? HostResponseWorkOrderId = null,
    string? HostResponsePlanId = null,
    string? HostResponsePlanHash = null,
    string? HostResponseMessage = null,
    string? HostResponseErrorCode = null
);

public sealed record BuilderExecutionResult(
    RunModel Run,
    string RunPath,
    string RunJsonPath,
    string ArtifactJsonPath
);
