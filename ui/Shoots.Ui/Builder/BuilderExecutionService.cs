using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    public BuilderExecutionResult Execute(PlanModel plan, ProjectModel project, string plannerSource = "runtime", string runtimeBridge = "RuntimeBridgeLocal", string provider = "local", string hostTransport = "none", string? hostResponseOutcome = null, string? hostResponseWorkOrderId = null, string? hostResponsePlanId = null, string? hostResponsePlanHash = null, string? hostResponseMessage = null, string? hostResponseErrorCode = null, Action<NarrationEvent>? narrate = null)
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
        var executionRequest = ExecutionContractAdapter.ToExecutionRequest(plan, project, plannerSource, runtimeBridge, provider, hostTransport, planHash);

        EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "info", "RUN_HEADER", new Dictionary<string, string>
        {
            ["run_id"] = runId,
            ["plan_hash"] = planHash,
            ["tool_catalog_hash"] = toolCatalogHash,
            ["workspace_descriptor_hash"] = workspaceDescriptorHash,
            ["contract_version"] = ExecutionContract.Version,
            ["planner_source"] = plannerSource,
            ["runtime_bridge"] = runtimeBridge,
            ["provider"] = provider,
            ["host_transport"] = hostTransport
        }));

        var steps = new List<RunStep>();
        var stageFlow = new List<RunStageRecord>();
        var providerAttempts = new List<ProviderAttemptRecord>();
        var artifactPaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = runJsonPath,
            ["narrator.jsonl"] = narratorPath,
            ["run_metadata.json"] = RunReplayService.MetadataPath(runPath),
            ["timeline.json"] = RunReplayService.TimelinePath(runPath)
        };
        PersistRun(RunStates.Pending);
        PersistRun(RunStates.Running);

        try
        {
            var environmentStage = BeginStage("environment", "Capturing deterministic environment snapshot.");
            var environment = CaptureEnvironment(runPath);
            artifactPaths["environment.json"] = environment.Path;
            EndStage(environmentStage, "completed", $"Environment hash {environment.Hash}.");
            EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "info", "ENV_CAPTURED", new Dictionary<string, string>
            {
                ["environment_hash"] = environment.Hash
            }));

            var providerStage = BeginStage("provider", $"Resolving provider '{provider}'.");
            var providerAttemptStartedUtc = DateTimeOffset.UtcNow;
            var providerOutcome = string.IsNullOrWhiteSpace(hostResponseErrorCode) ? "ready" : "degraded";
            var providerReason = string.IsNullOrWhiteSpace(hostResponseErrorCode) ? null : hostResponseErrorCode;
            var providerDetail = string.IsNullOrWhiteSpace(hostResponseMessage)
                ? $"Provider '{provider}' ready for deterministic execution."
                : hostResponseMessage;
            providerAttempts.Add(new ProviderAttemptRecord(
                1,
                1,
                providerOutcome,
                providerReason,
                providerDetail,
                providerAttemptStartedUtc,
                DateTimeOffset.UtcNow));
            EndStage(providerStage, string.Equals(providerOutcome, "ready", StringComparison.Ordinal) ? "completed" : "failed", providerDetail);

            var planningStage = BeginStage("plan", "Preparing deterministic plan execution.");
            EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "step", "PLAN_STARTED", new Dictionary<string, string>
            {
                ["run_id"] = runId,
                ["plan_id"] = plan.PlanId,
                ["plan_hash"] = planHash
            }));
            EndStage(planningStage, "completed", $"Plan {plan.PlanId} prepared.");

            var status = RunStates.Completed;
            var executeStage = BeginStage("execute", $"Executing {plan.Steps.Count} deterministic step(s).");

            foreach (var step in plan.Steps)
            {
                var stepStage = BeginStage(step.StepId, $"Running tool '{step.ToolId}'.");
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
                        artifactPaths[$"output:{step.StepId}"] = result.OutputPath;
                    }

                    steps.Add(new RunStep(step.StepId, step.ToolId, RunStates.Completed, result.OutputPath, null));
                    EndStage(stepStage, "completed", string.IsNullOrWhiteSpace(result.OutputPath) ? "Step completed." : result.OutputPath);
                    EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "result", "STEP_COMPLETED", new Dictionary<string, string>
                    {
                        ["step_id"] = step.StepId,
                        ["output_path"] = result.OutputPath ?? string.Empty
                    }));
                    continue;
                }

                status = RunStates.Failed;
                steps.Add(new RunStep(step.StepId, step.ToolId, RunStates.Failed, null, result.Error));
                var rollbackMarkerPath = Path.Combine(runPath, "rollback.marker");
                File.WriteAllText(rollbackMarkerPath, $"step={step.StepId}; error={result.Error}");
                artifactPaths["rollback.marker"] = rollbackMarkerPath;
                EndStage(stepStage, "failed", result.Error ?? "unknown");
                EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "error", "STEP_FAILED", new Dictionary<string, string>
                {
                    ["step_id"] = step.StepId,
                    ["error"] = result.Error ?? "unknown"
                }));
                break;
            }

            EndStage(executeStage, string.Equals(status, RunStates.Completed, StringComparison.Ordinal) ? "completed" : "failed", $"Execution {status}.");

            var artifactJsonPath = _artifactManager.WriteMetadata(runPath, planHash, toolCatalogHash);
            artifactPaths["artifact.json"] = artifactJsonPath;
            var manifestPath = Path.Combine(runPath, "artifacts", "manifest.json");
            artifactPaths["manifest.json"] = manifestPath;
            var manifestHash = ComputeFileHash(manifestPath);
            var transcriptPath = Path.Combine(project.WorkspacePath, "notes", "chat_transcript.jsonl");
            if (File.Exists(transcriptPath))
                artifactPaths["chat_transcript.jsonl"] = transcriptPath;
            var transcriptHash = ComputeFileHash(transcriptPath);
            var warning = DetectCatalogDrift(project.WorkspacePath, runId, toolCatalogHash);
            if (!string.IsNullOrWhiteSpace(warning))
            {
                EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "warn", "CATALOG_DRIFT", new Dictionary<string, string>
                {
                    ["warning"] = warning
                }));
            }

            var baselineDrift = EvaluateBaselinePolicy(project.WorkspacePath, planHash, manifestHash, toolCatalogHash);
            if (!string.IsNullOrWhiteSpace(baselineDrift))
            {
                status = RunStates.FailedDrift;
                EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "warn", "BASELINE_DRIFT", new Dictionary<string, string>
                {
                    ["warning"] = baselineDrift
                }));
                warning = string.IsNullOrWhiteSpace(warning) ? baselineDrift : $"{warning}; {baselineDrift}";
            }

            EmitNarration(new NarrationEvent(DateTimeOffset.UtcNow, "result", "RUN_COMPLETED", new Dictionary<string, string>
            {
                ["run_id"] = runId,
                ["status"] = status,
                ["plan_hash"] = planHash,
                ["tool_catalog_hash"] = toolCatalogHash,
                ["contract_version"] = ExecutionContract.Version
            }));

            var narratorHash = ComputeFileHash(narratorPath);
            var run = PersistRun(status, environment.Hash, manifestHash, narratorHash, transcriptHash, null, warning);
            var evidenceBundlePath = WriteEvidenceBundle(runPath, run, environment.Hash, manifestHash, narratorHash, transcriptHash);
            artifactPaths["evidence_bundle.json"] = evidenceBundlePath;
            var evidenceBundleHash = ComputeFileHash(evidenceBundlePath);
            run = PersistRun(status, environment.Hash, manifestHash, narratorHash, transcriptHash, evidenceBundleHash, warning);

            var executionResult = ExecutionContractAdapter.ToExecutionResult(run);
            if (!string.Equals(executionRequest.ContractVersion, executionResult.ContractVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"execution contract drift: request={executionRequest.ContractVersion}; result={executionResult.ContractVersion}");
            }

            var verificationStage = BeginStage("verification", "Validating saved run artifacts.");
            var verification = RunVerificationService.Verify(runPath);
            var verificationReportPath = Path.Combine(runPath, "verification_report.json");
            File.WriteAllText(verificationReportPath, JsonSerializer.Serialize(verification, new JsonSerializerOptions { WriteIndented = true }));
            artifactPaths["verification_report.json"] = verificationReportPath;
            EndStage(verificationStage, verification.Valid ? "completed" : "failed", verification.Valid ? "Verification passed." : string.Join("; ", verification.Errors));

            var failure = BuildFailureFromRun(run, artifactPaths);
            if (failure is not null)
            {
                artifactPaths["failure-fingerprint.json"] = RunReplayService.FailureFingerprintPath(runPath);
                File.WriteAllText(
                    RunReplayService.FailureFingerprintPath(runPath),
                    JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
            }

            WriteReplayArtifacts(runPath, run, stageFlow, providerAttempts, artifactPaths, failure);
            return new BuilderExecutionResult(run, runPath, runJsonPath, artifactJsonPath);
        }
        catch (Exception ex)
        {
            var failure = CreateFailureRecord(ex, artifactPaths);
            artifactPaths["failure-fingerprint.json"] = RunReplayService.FailureFingerprintPath(runPath);
            WriteReplayArtifacts(runPath, null, stageFlow, providerAttempts, artifactPaths, failure);
            File.WriteAllText(
                RunReplayService.FailureFingerprintPath(runPath),
                JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }

        void EmitNarration(NarrationEvent evt)
        {
            narrate?.Invoke(evt);
            File.AppendAllLines(narratorPath, new[] { JsonSerializer.Serialize(evt) });
        }

        RunModel PersistRun(string runStatus, string? envHash = null, string? manHash = null, string? narrHash = null, string? transHash = null, string? evidenceHash = null, string? reproWarning = null)
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
                steps,
                ExecutionContract.Version,
                plannerSource,
                runtimeBridge,
                provider,
                hostTransport,
                envHash,
                manHash,
                narrHash,
                transHash,
                evidenceHash,
                reproWarning,
                hostResponseOutcome,
                hostResponseWorkOrderId,
                hostResponsePlanId,
                hostResponsePlanHash,
                hostResponseMessage,
                hostResponseErrorCode);
            File.WriteAllText(runJsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            return model;
        }

        RunStageBuilder BeginStage(string stageName, string detail)
            => new(stageName, detail, DateTimeOffset.UtcNow);

        void EndStage(RunStageBuilder builder, string status, string detail)
        {
            stageFlow.Add(new RunStageRecord(
                builder.StageName,
                status,
                detail,
                builder.StartedUtc,
                DateTimeOffset.UtcNow));
        }
    }

    private static void WriteReplayArtifacts(
        string runPath,
        RunModel? run,
        IReadOnlyList<RunStageRecord> stageFlow,
        IReadOnlyList<ProviderAttemptRecord> providerAttempts,
        IReadOnlyDictionary<string, string> artifactPaths,
        RunFailureRecord? failure)
    {
        var effectiveRunId = run?.RunId ?? Path.GetFileName(runPath);
        var effectiveProvider = run?.Provider ?? "unknown";
        var effectiveHostTransport = run?.HostTransport ?? "none";
        var effectiveStatus = run?.Status ?? RunStates.FailedCrash;
        var effectiveCreatedUtc = run?.CreatedUtc ?? DateTimeOffset.UtcNow;
        var metadata = new PersistedRunMetadata(
            effectiveRunId,
            runPath,
            effectiveProvider,
            effectiveHostTransport,
            effectiveStatus,
            effectiveCreatedUtc,
            stageFlow.ToArray(),
            providerAttempts.ToArray(),
            failure,
            new Dictionary<string, string>(artifactPaths, StringComparer.Ordinal));

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(RunReplayService.MetadataPath(runPath), JsonSerializer.Serialize(metadata, options));
        File.WriteAllText(RunReplayService.TimelinePath(runPath), JsonSerializer.Serialize(stageFlow, options));
    }

    private static RunFailureRecord CreateFailureRecord(Exception ex, IReadOnlyDictionary<string, string> artifactPaths)
    {
        var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
        var message = ex.Message ?? string.Empty;
        var firstStackFrame = ex.StackTrace?
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith("at ", StringComparison.Ordinal))
            ?? string.Empty;

        var relevantPaths = artifactPaths.Values
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RunFailureRecord(exceptionType, message, firstStackFrame, relevantPaths);
    }

    private static RunFailureRecord? BuildFailureFromRun(RunModel run, IReadOnlyDictionary<string, string> artifactPaths)
    {
        if (run.Steps is null || run.Steps.Count == 0)
            return null;

        var failedStep = run.Steps.FirstOrDefault(step => string.Equals(step.Status, RunStates.Failed, StringComparison.Ordinal));
        if (failedStep is null && string.Equals(run.Status, RunStates.FailedDrift, StringComparison.Ordinal))
        {
            var driftPaths = artifactPaths.Values
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new RunFailureRecord("BaselineDrift", run.ReproWarning ?? "Baseline drift detected.", string.Empty, driftPaths);
        }

        if (failedStep is null || string.IsNullOrWhiteSpace(failedStep.Error))
            return null;

        var firstStackFrame = failedStep.Error
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith("at ", StringComparison.Ordinal))
            ?? string.Empty;
        var headline = failedStep.Error
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?? failedStep.Error;
        var paths = artifactPaths.Values
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RunFailureRecord(
            "RunStepFailure",
            headline,
            firstStackFrame,
            paths);
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
        var dotnetVersion = GetDotnetVersion();
        var gitVersion = GetGitVersion();
        var snapshot = new EnvironmentSnapshot(
            System.Environment.OSVersion.ToString(),
            dotnetVersion,
            gitVersion,
            System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            System.Environment.CurrentDirectory,
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        var environmentPath = Path.Combine(runPath, "environment.json");
        File.WriteAllText(environmentPath, json);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new EnvironmentCapture(hash, environmentPath);
    }

    private static string? ComputeFileHash(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? DetectCatalogDrift(string workspacePath, string currentRunId, string toolCatalogHash)
    {
        var runsPath = Path.Combine(workspacePath, "runs");
        if (!Directory.Exists(runsPath))
        {
            return null;
        }

        var previousRun = Directory.GetDirectories(runsPath)
            .Where(path => !string.Equals(Path.GetFileName(path), currentRunId, StringComparison.Ordinal))
            .Select(path => Path.Combine(path, "run.json"))
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.Ordinal)
            .LastOrDefault();

        if (previousRun is null)
        {
            return null;
        }

        var previous = JsonSerializer.Deserialize<RunModel>(File.ReadAllText(previousRun));
        if (previous is null)
        {
            return null;
        }

        if (string.Equals(previous.ToolCatalogHash, toolCatalogHash, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"catalog hash changed: previous={previous.ToolCatalogHash}; current={toolCatalogHash}";
    }

    private static string? EvaluateBaselinePolicy(string workspacePath, string planHash, string? manifestHash, string toolCatalogHash)
    {
        var baselinePath = Path.Combine(workspacePath, "baseline_policy.json");
        if (!File.Exists(baselinePath))
        {
            return null;
        }

        BaselinePolicy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<BaselinePolicy>(File.ReadAllText(baselinePath));
        }
        catch (Exception ex)
        {
            return $"baseline policy unreadable: {ex.Message}";
        }

        if (policy is null)
        {
            return "baseline policy missing";
        }

        var drift = new List<string>();
        if (!string.Equals(policy.PlanHash, planHash, StringComparison.OrdinalIgnoreCase))
        {
            drift.Add("plan_hash");
        }

        if (!string.Equals(policy.ExpectedManifestHash, manifestHash, StringComparison.OrdinalIgnoreCase))
        {
            drift.Add("manifest_hash");
        }

        if (!string.Equals(policy.CatalogHash, toolCatalogHash, StringComparison.OrdinalIgnoreCase))
        {
            drift.Add("catalog_hash");
        }

        return drift.Count == 0 ? null : $"baseline drift: {string.Join(",", drift)}";
    }

    private static string WriteEvidenceBundle(string runPath, RunModel run, string? environmentHash, string? manifestHash, string? narratorHash, string? transcriptHash)
    {
        var bundlePath = Path.Combine(runPath, "evidence_bundle.json");
        var payload = new
        {
            run_state = run.Status,
            plan_hash = run.PlanHash,
            tool_catalog_hash = run.ToolCatalogHash,
            environment_hash = environmentHash,
            artifact_manifest_hash = manifestHash,
            narrator_hash = narratorHash,
            transcript_hash = transcriptHash,
            repro_warning = run.ReproWarning,
            contract_version = run.ContractVersion,
            planner_source = run.PlannerSource,
            runtime_bridge = run.RuntimeBridge,
            provider = run.Provider,
            host_transport = run.HostTransport,
            host_response_outcome = run.HostResponseOutcome,
            host_response_work_order_id = run.HostResponseWorkOrderId,
            host_response_plan_id = run.HostResponsePlanId,
            host_response_plan_hash = run.HostResponsePlanHash,
            host_response_message = run.HostResponseMessage,
            host_response_error_code = run.HostResponseErrorCode
        };

        File.WriteAllText(bundlePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return bundlePath;
    }

    private static string GetDotnetVersion()
    {
        var framework = RuntimeInformation.FrameworkDescription;
        var runtimeVersion = System.Environment.Version.ToString();
        if (!string.IsNullOrWhiteSpace(framework))
        {
            return $"{framework} ({runtimeVersion})";
        }

        return runtimeVersion;
    }

    private static string GetGitVersion()
    {
        var gitVersion = System.Environment.GetEnvironmentVariable("GIT_VERSION");
        if (!string.IsNullOrWhiteSpace(gitVersion))
        {
            return gitVersion;
        }

        var gitExecPath = System.Environment.GetEnvironmentVariable("GIT_EXEC_PATH");
        if (!string.IsNullOrWhiteSpace(gitExecPath))
        {
            return gitExecPath;
        }

        return "unavailable";
    }

    private sealed record EnvironmentCapture(string Hash, string Path);

    private sealed record RunStageBuilder(string StageName, string Detail, DateTimeOffset StartedUtc);

    private sealed record EnvironmentSnapshot(
        string OsVersion,
        string DotnetSdkVersion,
        string GitVersion,
        string PathSnapshot,
        string WorkingDirectory,
        DateTimeOffset CapturedUtc);
}

public sealed record RunStageRecord(
    string StageName,
    string Status,
    string Detail,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc);

public sealed record ProviderAttemptRecord(
    int Attempt,
    int MaxAttempts,
    string Outcome,
    string? ReasonCode,
    string Detail,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc);

public sealed record RunFailureRecord(
    string ExceptionType,
    string Message,
    string FirstStackFrame,
    IReadOnlyList<string> ArtifactPaths);

public sealed record PersistedRunMetadata(
    string RunId,
    string RunPath,
    string Provider,
    string HostTransport,
    string TerminalStatus,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<RunStageRecord> StageFlow,
    IReadOnlyList<ProviderAttemptRecord> ProviderAttempts,
    RunFailureRecord? Failure,
    IReadOnlyDictionary<string, string> ArtifactPaths);

public sealed record ReplayInspectionResult(
    string SourceRunPath,
    bool IsMatch,
    string Summary,
    IReadOnlyList<string> Mismatches,
    PersistedRunMetadata Metadata,
    RunModel Run);

public static class RunReplayService
{
    public const string MetadataFileName = "run_metadata.json";
    public const string TimelineFileName = "timeline.json";
    public const string FailureFingerprintFileName = "failure-fingerprint.json";

    public static string MetadataPath(string runPath) => Path.Combine(runPath, MetadataFileName);

    public static string TimelinePath(string runPath) => Path.Combine(runPath, TimelineFileName);

    public static string FailureFingerprintPath(string runPath) => Path.Combine(runPath, FailureFingerprintFileName);

    public static ReplayInspectionResult ReplayFromRunPath(string runPath)
    {
        if (string.IsNullOrWhiteSpace(runPath))
            throw new ArgumentException("run path is required", nameof(runPath));
        if (!Directory.Exists(runPath))
            throw new DirectoryNotFoundException($"Run path not found: {runPath}");

        var metadataPath = MetadataPath(runPath);
        var runJsonPath = Path.Combine(runPath, "run.json");

        if (!File.Exists(metadataPath))
            throw new FileNotFoundException("Run metadata file is missing.", metadataPath);
        if (!File.Exists(runJsonPath))
            throw new FileNotFoundException("run.json is missing.", runJsonPath);

        var metadata = JsonSerializer.Deserialize<PersistedRunMetadata>(File.ReadAllText(metadataPath));
        var run = JsonSerializer.Deserialize<RunModel>(File.ReadAllText(runJsonPath));

        if (metadata is null)
            throw new InvalidDataException("Run metadata could not be parsed.");
        if (run is null)
            throw new InvalidDataException("run.json could not be parsed.");

        var mismatches = new List<string>();

        if (!string.Equals(metadata.RunId, run.RunId, StringComparison.Ordinal))
            mismatches.Add($"run_id mismatch: metadata={metadata.RunId}; run={run.RunId}");

        if (!string.Equals(metadata.Provider, run.Provider, StringComparison.Ordinal))
            mismatches.Add($"provider mismatch: metadata={metadata.Provider}; run={run.Provider}");

        if (!string.Equals(metadata.TerminalStatus, run.Status, StringComparison.Ordinal))
            mismatches.Add($"terminal status mismatch: metadata={metadata.TerminalStatus}; run={run.Status}");

        var metadataStages = metadata.StageFlow.Select(stage => $"{stage.StageName}:{stage.Status}").ToArray();
        var runStages = run.Steps.Select(step => $"{step.StepId}:{step.Status}").ToArray();

        if (metadataStages.Length == 0)
        {
            mismatches.Add("metadata stage flow is empty");
        }
        else if (!ContainsOrderedRunSteps(metadataStages, runStages))
        {
            mismatches.Add(
                $"stage flow diverged: metadata={string.Join(",", metadataStages)}; run={string.Join(",", runStages)}");
        }

        foreach (var artifact in metadata.ArtifactPaths.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(artifact.Value) && !File.Exists(artifact.Value) && !Directory.Exists(artifact.Value))
                mismatches.Add($"artifact missing: {artifact.Key}={artifact.Value}");
        }

        var summary = mismatches.Count == 0
            ? "Replay matches saved run metadata."
            : $"Replay diverged from saved run metadata ({mismatches.Count} mismatch{(mismatches.Count == 1 ? string.Empty : "es")}).";

        return new ReplayInspectionResult(runPath, mismatches.Count == 0, summary, mismatches, metadata, run);
    }

    private static bool ContainsOrderedRunSteps(IReadOnlyList<string> metadataStages, IReadOnlyList<string> runStages)
    {
        if (runStages.Count == 0)
            return true;

        var runIndex = 0;
        for (var i = 0; i < metadataStages.Count && runIndex < runStages.Count; i++)
        {
            if (string.Equals(metadataStages[i], runStages[runIndex], StringComparison.Ordinal))
                runIndex++;
        }

        return runIndex == runStages.Count;
    }
}
