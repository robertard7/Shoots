using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoots.UI.Diagnostics;
using Shoots.UI.Projects;
using Shoots.UI.Services.Backends;
using Shoots.UI.Services;

namespace Shoots.UI.Builder;

public sealed class BuilderExecutionService
{
    public const string BuilderProofFloorModelId = "qwen2.5:0.5b-instruct";
private const int BuilderReadinessRequiredProofRuns = 2;
private const int BuilderReadinessRequiredPreparedLaunches = 2;
private const int BuilderReadinessGateHistoryRetentionCount = 30;
private const int BuilderOverrideRecoveryRequiredProofRuns = 1;
private const int BuilderOverrideRecoveryRequiredPreparedLaunches = 1;

    private readonly IRuntimeBridge _runtimeBridge;
    private readonly ArtifactManager _artifactManager;
    private readonly ToolRegistry _toolRegistry;
    private readonly IBuilderProofCommandRunner _builderProofCommandRunner;
    private readonly IBuilderStrongerTierResolver _builderStrongerTierResolver;
    private readonly IBuilderToolchainCapabilityScanner _builderToolchainCapabilityScanner;
    private readonly IBuilderGitReadinessProbe _builderGitReadinessProbe;

    public BuilderExecutionService(
        IRuntimeBridge runtimeBridge,
        ArtifactManager artifactManager,
        ToolRegistry toolRegistry,
        IBuilderProofCommandRunner? builderProofCommandRunner = null,
        IBuilderStrongerTierResolver? builderStrongerTierResolver = null,
        IBuilderToolchainCapabilityScanner? builderToolchainCapabilityScanner = null,
        IBuilderGitReadinessProbe? builderGitReadinessProbe = null)
    {
        _runtimeBridge = runtimeBridge;
        _artifactManager = artifactManager;
        _toolRegistry = toolRegistry;
        _builderProofCommandRunner = builderProofCommandRunner ?? new ValidationCommandBuilderProofRunner();
        _builderStrongerTierResolver = builderStrongerTierResolver ?? new NullBuilderStrongerTierResolver();
        _builderToolchainCapabilityScanner = builderToolchainCapabilityScanner ?? new DefaultBuilderToolchainCapabilityScanner();
        _builderGitReadinessProbe = builderGitReadinessProbe ?? new DefaultBuilderGitReadinessProbe();
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

    public static string BuilderProofRootForRepo(string repoRoot)
        => BuilderWorkspaceService.BuilderProofRootForRepo(repoRoot);

    public static string BuilderProofRunsRootForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "runs");

    public static string BuilderProofHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_proof_history.json");

    public static string BuilderProofMatrixArtifactPath(string runFolder)
        => Path.Combine(runFolder, "builder_proof_matrix.json");

    public static string BuilderProofRunArtifactPath(string runFolder)
        => Path.Combine(runFolder, "builder_proof_run.json");

    public static string BuilderProofSummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_proof_summary.md");

    public static string BuilderModelFloorVerdictPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_floor_verdict.json");

    public static string BuilderModelFloorSummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_floor_summary.md");

    public static string BuilderModelFloorFailurePatternsPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_floor_failure_patterns.json");

    public static string BuilderExternalProofRunPath(string runFolder)
        => Path.Combine(runFolder, "builder_external_proof_run.json");

    public static string BuilderExternalProofSummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_external_proof_summary.md");

    public static string BuilderExternalFloorVerdictPath(string runFolder)
        => Path.Combine(runFolder, "builder_external_floor_verdict.json");

    public static string BuilderExternalFloorSummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_external_floor_summary.md");

    public static string BuilderModelFloorPolicyPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_floor_policy.json");

    public static string BuilderModelFloorPolicySummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_floor_policy.md");

    public static string BuilderModelTrustBandsPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_trust_bands.json");

    public static string BuilderModelScopeSummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_scope_summary.md");

    public static string BuilderModelRoutingRecommendationPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_routing_recommendation.json");

    public static string BuilderModelEscalationDecisionPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_escalation_decision.json");

    public static string BuilderModelRoutingPlanPath(string runFolder)
        => Path.Combine(runFolder, "builder_model_routing_plan.json");

    public static string BuilderStrongerTierAvailabilityPath(string runFolder)
        => Path.Combine(runFolder, "builder_stronger_tier_availability.json");

    public static string BuilderComparativeProofRunPath(string runFolder)
        => Path.Combine(runFolder, "builder_comparative_proof_run.json");

    public static string BuilderComparativeProofSummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_comparative_proof_summary.md");

    public static string BuilderRoutingPolicyEvidencePath(string runFolder)
        => Path.Combine(runFolder, "builder_routing_policy_evidence.json");

    public static string BuilderSplitFirstPlanPath(string runFolder)
        => Path.Combine(runFolder, "builder_split_first_plan.json");

    public static string BuilderTieredRoutingPolicyPath(string runFolder)
        => Path.Combine(runFolder, "builder_tiered_routing_policy.json");

    public static string BuilderSplitStepExecutionPath(string runFolder)
        => Path.Combine(runFolder, "builder_split_step_execution.json");

    public static string BuilderSplitFirstOutcomePath(string runFolder)
        => Path.Combine(runFolder, "builder_split_first_outcome.json");

    public static string BuilderDefaultPolicyPath(string runFolder)
        => Path.Combine(runFolder, "builder_default_policy.json");

    public static string BuilderDefaultPolicyHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_default_policy_history.json");

    public static string BuilderRequestPolicyDecisionPath(string runFolder)
        => Path.Combine(runFolder, "builder_request_policy_decision.json");

    public static string BuilderPolicyStabilityPath(string runFolder)
        => Path.Combine(runFolder, "builder_policy_stability.json");

    public static string BuilderRequestIntakePath(string runFolder)
        => Path.Combine(runFolder, "builder_request_intake.json");

    public static string BuilderRequestIntakeHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_request_intake_history.json");

    public static string BuilderExecutionPrepPath(string runFolder)
        => Path.Combine(runFolder, "builder_execution_prep.json");

    public static string BuilderExecutionPrepHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_execution_prep_history.json");

    public static string BuilderExecutionLaunchPath(string runFolder)
        => Path.Combine(runFolder, "builder_execution_launch.json");

    public static string BuilderExecutionResultPath(string runFolder)
        => Path.Combine(runFolder, "builder_execution_result.json");

    public static string BuilderReadinessGatePath(string runFolder)
        => Path.Combine(runFolder, "builder_readiness_gate.json");

    public static string BuilderReadinessGateHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_readiness_gate_history.json");

    public static string BuilderRouteStabilitySummaryPath(string runFolder)
        => Path.Combine(runFolder, "builder_route_stability_summary.md");

    public static string BuilderConfirmedTaskClassesPath(string runFolder)
        => Path.Combine(runFolder, "builder_confirmed_task_classes.json");

    public static string BuilderDefaultRouteDecisionPath(string runFolder)
        => Path.Combine(runFolder, "builder_default_route_decision.json");

    public static string BuilderReadinessContradictionsPath(string runFolder)
        => Path.Combine(runFolder, "builder_readiness_contradictions.json");

    public static string BuilderLaunchDefaultDecisionPath(string runFolder)
        => Path.Combine(runFolder, "builder_launch_default_decision.json");

    public static string BuilderRouteOverrideEvidencePath(string runFolder)
        => Path.Combine(runFolder, "builder_route_override_evidence.json");

    public static string BuilderPolicyReviewCandidatesPath(string runFolder)
        => Path.Combine(runFolder, "builder_policy_review_candidates.json");

    public static string BuilderRouteReconfirmationPath(string runFolder)
        => Path.Combine(runFolder, "builder_route_reconfirmation.json");

    public static string BuilderDefaultRouteRecoveryPath(string runFolder)
        => Path.Combine(runFolder, "builder_default_route_recovery.json");

    public static string BuilderRouteStateContinuityPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_route_state_continuity.json");

    public static string BuilderRouteCurrentStateIndexPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_route_current_state_index.json");

    public static string BuilderModelCapabilityMatrixPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_capability_matrix.json");

    public static string BuilderModelRoutingPolicyPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_routing_policy.json");

    public static string BuilderModelRoutingPolicySummaryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_routing_policy_summary.md");

    public static string BuilderModelDecisionPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_decision.json");

    public static string BuilderModelEscalationPolicyDecisionPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_escalation_policy_decision.json");

    public static string BuilderModelRoutingPolicyHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_routing_policy_history.json");

    public static string BuilderModelRoutingStabilityPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_routing_stability.json");

    public static string BuilderRouteExplanationPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_route_explanation.json");

    public static string BuilderModelDecisionExplanationPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_model_decision_explanation.json");

    public static string BuilderFailureAnalysisPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_failure_analysis.json");

    public static string BuilderOperatorDiagnosticSummaryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_operator_diagnostic_summary.md");

    public static string BuilderToolchainCapabilityRegistryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_toolchain_capability_registry.json");

    public static string BuilderToolchainCapabilityHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_toolchain_capability_history.json");

    public static string BuilderLanguageEligibilityPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_language_eligibility.json");

    public static string BuilderLanguageEligibilitySummaryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_language_eligibility_summary.md");

    public static string BuilderCapabilityBlockDecisionPath(string runFolder)
        => Path.Combine(runFolder, "builder_capability_block_decision.json");

    public static string BuilderRepoKnowledgeIndexPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_repo_knowledge_index.json");

    public static string BuilderRepoKnowledgeSummaryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_repo_knowledge_summary.md");

    public static string BuilderRepoKnowledgeHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_repo_knowledge_history.json");

    public static string BuilderRepoKnowledgeDriftPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_repo_knowledge_drift.json");

    public static string BuilderRepoRetrievalContextPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_repo_retrieval_context.json");

    public static string BuilderConversationIntakePathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_conversation_intake.json");

    public static string BuilderConversationHandoffPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_conversation_handoff.json");

    public static string BuilderConversationExecutionSessionPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_conversation_execution_session.json");

    public static string BuilderPatchReviewPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_review.json");

    public static string BuilderPatchReviewOutcomePathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_review_outcome.json");

    public static string BuilderPatchDiffReviewPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_diff_review.json");

    public static string BuilderFileReviewDecisionPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_file_review_decision.json");

    public static string BuilderPatchApplyDecisionPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_apply_decision.json");

    public static string BuilderPatchSnapshotPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_snapshot.json");

    public static string BuilderCommitProposalPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_commit_proposal.json");

    public static string BuilderPatchBundlePathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_bundle.patch");

    public static string BuilderPatchExportPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_export.json");

    public static string BuilderPatchSnapshotHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_patch_snapshot_history.json");

    public static string BuilderOutputHandoffPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_output_handoff.json");

    public static string BuilderOutputHandoffSummaryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_output_handoff_summary.md");

    public static string BuilderGitHandoffReadinessPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_git_handoff_readiness.json");

    public static string BuilderManualApplyGuidancePathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_manual_apply_guidance.json");

    public static string BuilderGitCommitHandoffPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_git_commit_handoff.json");

    public static string BuilderOutputHandoffHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_output_handoff_history.json");

    public static string BuilderConversationExecutionHistoryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_conversation_execution_history.json");

    public static string BuilderConversationReviewSummaryPathForRepo(string repoRoot)
        => Path.Combine(BuilderProofRootForRepo(repoRoot), "builder_conversation_review_summary.md");

    public static BuilderProofHistory LoadBuilderProofHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderProofHistoryPathForRepo(repoRoot),
            new BuilderProofHistory(20, Array.Empty<BuilderProofHistoryEntry>()));

    public static BuilderRouteStateContinuity LoadBuilderRouteStateContinuity(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderRouteStateContinuityPathForRepo(repoRoot),
            new BuilderRouteStateContinuity(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                0,
                0,
                Array.Empty<BuilderRouteStateContinuityEntry>(),
                "No builder route continuity recorded.",
                BuilderRouteStateContinuityPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderRouteCurrentStateIndex LoadBuilderRouteCurrentStateIndex(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderRouteCurrentStateIndexPathForRepo(repoRoot),
            new BuilderRouteCurrentStateIndex(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                0,
                0,
                Array.Empty<BuilderRouteCurrentStateArtifactIndexEntry>(),
                "No builder route current-state index recorded.",
                BuilderRouteCurrentStateIndexPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderModelCapabilityMatrix LoadBuilderModelCapabilityMatrix(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderModelCapabilityMatrixPathForRepo(repoRoot),
            new BuilderModelCapabilityMatrix(
                string.Empty,
                string.Empty,
                "low_floor_model_tier",
                "stronger_builder_tier",
                Array.Empty<BuilderModelCapabilityMatrixEntry>(),
                "No builder model capability matrix recorded.",
                BuilderModelCapabilityMatrixPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderModelRoutingPolicy LoadBuilderModelRoutingPolicy(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderModelRoutingPolicyPathForRepo(repoRoot),
            new BuilderModelRoutingPolicy(
                string.Empty,
                string.Empty,
                "low_floor_model_tier",
                string.Empty,
                Array.Empty<BuilderModelRoutingPolicyEntry>(),
                "No builder model routing policy recorded.",
                BuilderModelRoutingPolicyPathForRepo(repoRoot),
                BuilderModelRoutingPolicySummaryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderModelDecision LoadBuilderModelDecision(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderModelDecisionPathForRepo(repoRoot),
            new BuilderModelDecision(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "not_yet_proven",
                "not_needed",
                "not_required",
                false,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                "No builder model decision recorded.",
                BuilderModelDecisionPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderModelEscalationPolicyDecision LoadBuilderModelEscalationPolicyDecision(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderModelEscalationPolicyDecisionPathForRepo(repoRoot),
            new BuilderModelEscalationPolicyDecision(
                string.Empty,
                string.Empty,
                "not_yet_proven",
                "not_needed",
                "not_viable",
                "unknown",
                "not_ready",
                string.Empty,
                Array.Empty<string>(),
                "No builder model escalation policy decision recorded.",
                BuilderModelEscalationPolicyDecisionPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderModelRoutingPolicyHistory LoadBuilderModelRoutingPolicyHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderModelRoutingPolicyHistoryPathForRepo(repoRoot),
            new BuilderModelRoutingPolicyHistory(
                20,
                Array.Empty<BuilderModelRoutingPolicyHistoryEntry>(),
                "No builder model routing policy history recorded.",
                BuilderModelRoutingPolicyHistoryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderModelRoutingStability LoadBuilderModelRoutingStability(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderModelRoutingStabilityPathForRepo(repoRoot),
            new BuilderModelRoutingStability(
                string.Empty,
                string.Empty,
                Array.Empty<BuilderModelRoutingStabilityEntry>(),
                "No builder model routing stability recorded.",
                BuilderModelRoutingStabilityPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderRouteExplanation LoadBuilderRouteExplanation(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderRouteExplanationPathForRepo(repoRoot),
            new BuilderRouteExplanation(
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                "No builder route explanation recorded.",
                Array.Empty<string>(),
                Array.Empty<string>(),
                "No builder route explanation recorded.",
                BuilderRouteExplanationPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderModelDecisionExplanation LoadBuilderModelDecisionExplanation(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderModelDecisionExplanationPathForRepo(repoRoot),
            new BuilderModelDecisionExplanation(
                string.Empty,
                string.Empty,
                string.Empty,
                "No builder model capability entry recorded.",
                "No builder routing rules entry recorded.",
                "not_recorded",
                "No split-first reasoning recorded.",
                "not_recorded",
                "not_recorded",
                Array.Empty<string>(),
                "No builder model decision explanation recorded.",
                BuilderModelDecisionExplanationPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderFailureAnalysis LoadBuilderFailureAnalysis(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderFailureAnalysisPathForRepo(repoRoot),
            new BuilderFailureAnalysis(
                string.Empty,
                "not_started",
                "Not started",
                "no_failure_recorded",
                "No builder failure analysis recorded.",
                Array.Empty<string>(),
                "No remediation required.",
                "No builder failure analysis recorded.",
                BuilderFailureAnalysisPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderToolchainCapabilityRegistry LoadBuilderToolchainCapabilityRegistry(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderToolchainCapabilityRegistryPathForRepo(repoRoot),
            new BuilderToolchainCapabilityRegistry(
                string.Empty,
                string.Empty,
                Array.Empty<BuilderToolchainCapabilityRegistryEntry>(),
                "not_refreshed",
                "not_recorded",
                Array.Empty<string>(),
                Array.Empty<string>(),
                "No toolchain capability registry recorded.",
                BuilderToolchainCapabilityRegistryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderToolchainCapabilityHistory LoadBuilderToolchainCapabilityHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderToolchainCapabilityHistoryPathForRepo(repoRoot),
            new BuilderToolchainCapabilityHistory(
                12,
                Array.Empty<BuilderToolchainCapabilityHistoryEntry>(),
                "No toolchain capability refresh history recorded.",
                BuilderToolchainCapabilityHistoryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderLanguageEligibility LoadBuilderLanguageEligibility(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderLanguageEligibilityPathForRepo(repoRoot),
            new BuilderLanguageEligibility(
                string.Empty,
                string.Empty,
                Array.Empty<BuilderLanguageEligibilityEntry>(),
                "No language eligibility registry recorded.",
                BuilderLanguageEligibilityPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderRepoKnowledgeIndex LoadBuilderRepoKnowledgeIndex(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderRepoKnowledgeIndexPathForRepo(repoRoot),
            new BuilderRepoKnowledgeIndex(
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<BuilderRepoKnowledgeProjectEntry>(),
                Array.Empty<BuilderRepoKnowledgeOwnershipSummary>(),
                Array.Empty<string>(),
                "not_refreshed",
                "not_recorded",
                Array.Empty<string>(),
                "No repo knowledge index recorded.",
                BuilderRepoKnowledgeIndexPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderRepoKnowledgeHistory LoadBuilderRepoKnowledgeHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderRepoKnowledgeHistoryPathForRepo(repoRoot),
            new BuilderRepoKnowledgeHistory(
                12,
                Array.Empty<BuilderRepoKnowledgeHistoryEntry>(),
                "No repo knowledge refresh history recorded.",
                BuilderRepoKnowledgeHistoryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderRepoKnowledgeDrift? LoadBuilderRepoKnowledgeDrift(string repoRoot)
        => TryLoadBuilderProofArtifact<BuilderRepoKnowledgeDrift?>(BuilderRepoKnowledgeDriftPathForRepo(repoRoot), null);

    public static BuilderRepoRetrievalContext LoadBuilderRepoRetrievalContext(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderRepoRetrievalContextPathForRepo(repoRoot),
            new BuilderRepoRetrievalContext(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "not_recorded",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Empty,
                Array.Empty<string>(),
                "No repo retrieval context recorded.",
                BuilderRepoRetrievalContextPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderConversationIntake LoadBuilderConversationIntake(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderConversationIntakePathForRepo(repoRoot),
            new BuilderConversationIntake(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "not_recorded",
                "No repo retrieval match recorded.",
                "not_recorded",
                "No capability decision recorded.",
                string.Empty,
                string.Empty,
                false,
                string.Empty,
                "not_reviewed",
                "not_ready",
                "No builder conversation intake recorded.",
                Array.Empty<string>(),
                "No builder conversation intake recorded.",
                BuilderConversationIntakePathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderConversationHandoff LoadBuilderConversationHandoff(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderConversationHandoffPathForRepo(repoRoot),
            new BuilderConversationHandoff(
                string.Empty,
                string.Empty,
                "not_recorded",
                "not_recorded",
                string.Empty,
                string.Empty,
                "not_reviewed",
                "not_ready",
                "No builder conversation handoff recorded.",
                Array.Empty<string>(),
                "No builder conversation handoff recorded.",
                BuilderConversationHandoffPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderConversationExecutionSession LoadBuilderConversationExecutionSession(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderConversationExecutionSessionPathForRepo(repoRoot),
            new BuilderConversationExecutionSession(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "not_started",
                string.Empty,
                string.Empty,
                "not_reviewed",
                "No validation summary recorded.",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<BuilderPatchReviewChangedFile>(),
                Array.Empty<BuilderConversationExecutionStage>(),
                Array.Empty<string>(),
                "No builder conversation execution session recorded.",
                BuilderConversationExecutionSessionPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderPatchReview LoadBuilderPatchReview(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderPatchReviewPathForRepo(repoRoot),
            new BuilderPatchReview(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "No validation summary recorded.",
                "not_ready",
                Array.Empty<BuilderPatchReviewChangedFile>(),
                Array.Empty<string>(),
                "No builder patch review recorded.",
                BuilderPatchReviewPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderPatchReviewOutcome LoadBuilderPatchReviewOutcome(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderPatchReviewOutcomePathForRepo(repoRoot),
            new BuilderPatchReviewOutcome(
                string.Empty,
                "not_reviewed",
                "not_started",
                "not_reviewed",
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                "No builder patch review outcome recorded.",
                BuilderPatchReviewOutcomePathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderPatchDiffReview LoadBuilderPatchDiffReview(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderPatchDiffReviewPathForRepo(repoRoot),
            new BuilderPatchDiffReview(
                string.Empty,
                string.Empty,
                string.Empty,
                "all_files_pending",
                "not_ready",
                Array.Empty<BuilderPatchDiffReviewFileEntry>(),
                Array.Empty<string>(),
                "No builder patch diff review recorded.",
                BuilderPatchDiffReviewPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderFileReviewDecision LoadBuilderFileReviewDecision(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderFileReviewDecisionPathForRepo(repoRoot),
            new BuilderFileReviewDecision(
                string.Empty,
                string.Empty,
                "all_files_pending",
                Array.Empty<BuilderFileReviewDecisionEntry>(),
                Array.Empty<string>(),
                "No builder file review decision recorded.",
                BuilderFileReviewDecisionPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderPatchApplyDecision LoadBuilderPatchApplyDecision(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderPatchApplyDecisionPathForRepo(repoRoot),
            new BuilderPatchApplyDecision(
                string.Empty,
                "all_files_pending",
                "not_ready",
                Array.Empty<string>(),
                "not_ready_to_apply",
                Array.Empty<string>(),
                "No builder patch apply decision recorded.",
                BuilderPatchApplyDecisionPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderPatchSnapshot LoadBuilderPatchSnapshot(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderPatchSnapshotPathForRepo(repoRoot),
            new BuilderPatchSnapshot(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "not_recorded",
                Array.Empty<BuilderPatchSnapshotFileEntry>(),
                Array.Empty<string>(),
                "No builder patch snapshot recorded.",
                BuilderPatchSnapshotPathForRepo(repoRoot),
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue));

    public static BuilderCommitProposal LoadBuilderCommitProposal(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderCommitProposalPathForRepo(repoRoot),
            new BuilderCommitProposal(
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                string.Empty,
                repoRoot,
                Array.Empty<string>(),
                "No builder commit proposal recorded.",
                BuilderCommitProposalPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderPatchExport LoadBuilderPatchExport(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderPatchExportPathForRepo(repoRoot),
            new BuilderPatchExport(
                string.Empty,
                string.Empty,
                DateTimeOffset.MinValue,
                0,
                Array.Empty<string>(),
                "No builder patch export recorded.",
                BuilderPatchExportPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderPatchSnapshotHistory LoadBuilderPatchSnapshotHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderPatchSnapshotHistoryPathForRepo(repoRoot),
            new BuilderPatchSnapshotHistory(
                12,
                Array.Empty<BuilderPatchSnapshotHistoryEntry>(),
                "No builder patch snapshot history recorded.",
                BuilderPatchSnapshotHistoryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderOutputHandoff LoadBuilderOutputHandoff(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderOutputHandoffPathForRepo(repoRoot),
            new BuilderOutputHandoff(
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "not_ready",
                "blocked_git_unknown_state",
                Array.Empty<string>(),
                Array.Empty<string>(),
                "No builder output handoff recorded.",
                BuilderOutputHandoffPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderGitHandoffReadiness LoadBuilderGitHandoffReadiness(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderGitHandoffReadinessPathForRepo(repoRoot),
            new BuilderGitHandoffReadiness(
                false,
                string.Empty,
                false,
                false,
                "unknown",
                "blocked_git_unknown_state",
                Array.Empty<string>(),
                "No builder Git handoff readiness recorded.",
                BuilderGitHandoffReadinessPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderManualApplyGuidance LoadBuilderManualApplyGuidance(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderManualApplyGuidancePathForRepo(repoRoot),
            new BuilderManualApplyGuidance(
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "No builder manual apply guidance recorded.",
                BuilderManualApplyGuidancePathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderGitCommitHandoff LoadBuilderGitCommitHandoff(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderGitCommitHandoffPathForRepo(repoRoot),
            new BuilderGitCommitHandoff(
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                string.Empty,
                "blocked_git_unknown_state",
                Array.Empty<string>(),
                Array.Empty<string>(),
                "No builder Git commit handoff recorded.",
                BuilderGitCommitHandoffPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderOutputHandoffHistory LoadBuilderOutputHandoffHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderOutputHandoffHistoryPathForRepo(repoRoot),
            new BuilderOutputHandoffHistory(
                12,
                Array.Empty<BuilderOutputHandoffHistoryEntry>(),
                "No builder output handoff history recorded.",
                BuilderOutputHandoffHistoryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderConversationExecutionHistory LoadBuilderConversationExecutionHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderConversationExecutionHistoryPathForRepo(repoRoot),
            new BuilderConversationExecutionHistory(
                12,
                Array.Empty<BuilderConversationExecutionHistoryEntry>(),
                "No builder conversation execution history recorded.",
                BuilderConversationExecutionHistoryPathForRepo(repoRoot),
                DateTimeOffset.MinValue));

    public static BuilderProofRun? LoadBuilderProofRun(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderProofRun?>(BuilderProofRunArtifactPath(runFolder), null);

    public static BuilderProofRun? LoadLatestBuilderProofRun(string repoRoot)
    {
        var latestEntry = LoadBuilderProofHistory(repoRoot).Entries
            .OrderByDescending(entry => entry.CompletedUtc)
            .ThenByDescending(entry => entry.RunId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latestEntry is not null && File.Exists(BuilderProofRunArtifactPath(latestEntry.RunFolder)))
            return LoadBuilderProofRun(latestEntry.RunFolder);

        var runsRoot = BuilderProofRunsRootForRepo(repoRoot);
        if (!Directory.Exists(runsRoot))
            return null;

        return Directory.GetDirectories(runsRoot)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(LoadBuilderProofRun)
            .FirstOrDefault(run => run is not null);
    }

    public static BuilderModelFloorVerdict? LoadBuilderModelFloorVerdict(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderModelFloorVerdict?>(BuilderModelFloorVerdictPath(runFolder), null);

    public static BuilderModelFloorVerdict? LoadLatestBuilderModelFloorVerdict(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderModelFloorVerdict(runFolder)
            : null;

    public static BuilderProofFailurePatternSummary? LoadBuilderProofFailurePatternSummary(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderProofFailurePatternSummary?>(BuilderModelFloorFailurePatternsPath(runFolder), null);

    public static BuilderExternalProofRun? LoadBuilderExternalProofRun(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderExternalProofRun?>(BuilderExternalProofRunPath(runFolder), null);

    public static BuilderExternalProofRun? LoadLatestBuilderExternalProofRun(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderExternalProofRun(runFolder)
            : null;

    public static BuilderExternalFloorVerdict? LoadBuilderExternalFloorVerdict(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderExternalFloorVerdict?>(BuilderExternalFloorVerdictPath(runFolder), null);

    public static BuilderExternalFloorVerdict? LoadLatestBuilderExternalFloorVerdict(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderExternalFloorVerdict(runFolder)
            : null;

    public static BuilderModelFloorPolicy? LoadBuilderModelFloorPolicy(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderModelFloorPolicy?>(BuilderModelFloorPolicyPath(runFolder), null);

    public static BuilderModelFloorPolicy? LoadLatestBuilderModelFloorPolicy(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderModelFloorPolicy(runFolder)
            : null;

    public static BuilderModelTrustBands? LoadBuilderModelTrustBands(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderModelTrustBands?>(BuilderModelTrustBandsPath(runFolder), null);

    public static BuilderModelTrustBands? LoadLatestBuilderModelTrustBands(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderModelTrustBands(runFolder)
            : null;

    public static BuilderModelRoutingRecommendation? LoadBuilderModelRoutingRecommendation(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderModelRoutingRecommendation?>(BuilderModelRoutingRecommendationPath(runFolder), null);

    public static BuilderModelRoutingRecommendation? LoadLatestBuilderModelRoutingRecommendation(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderModelRoutingRecommendation(runFolder)
            : null;

    public static BuilderModelEscalationDecision? LoadBuilderModelEscalationDecision(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderModelEscalationDecision?>(BuilderModelEscalationDecisionPath(runFolder), null);

    public static BuilderModelEscalationDecision? LoadLatestBuilderModelEscalationDecision(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderModelEscalationDecision(runFolder)
            : null;

    public static BuilderModelRoutingPlan? LoadBuilderModelRoutingPlan(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderModelRoutingPlan?>(BuilderModelRoutingPlanPath(runFolder), null);

    public static BuilderModelRoutingPlan? LoadLatestBuilderModelRoutingPlan(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderModelRoutingPlan(runFolder)
            : null;

    public static BuilderStrongerTierAvailability? LoadBuilderStrongerTierAvailability(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderStrongerTierAvailability?>(BuilderStrongerTierAvailabilityPath(runFolder), null);

    public static BuilderStrongerTierAvailability? LoadLatestBuilderStrongerTierAvailability(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderStrongerTierAvailability(runFolder)
            : null;

    public static BuilderComparativeProofRun? LoadBuilderComparativeProofRun(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderComparativeProofRun?>(BuilderComparativeProofRunPath(runFolder), null);

    public static BuilderComparativeProofRun? LoadLatestBuilderComparativeProofRun(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderComparativeProofRun(runFolder)
            : null;

    public static BuilderRoutingPolicyEvidence? LoadBuilderRoutingPolicyEvidence(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRoutingPolicyEvidence?>(BuilderRoutingPolicyEvidencePath(runFolder), null);

    public static BuilderRoutingPolicyEvidence? LoadLatestBuilderRoutingPolicyEvidence(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderRoutingPolicyEvidence(runFolder)
            : null;

    public static BuilderSplitFirstPlan? LoadBuilderSplitFirstPlan(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderSplitFirstPlan?>(BuilderSplitFirstPlanPath(runFolder), null);

    public static BuilderSplitFirstPlan? LoadLatestBuilderSplitFirstPlan(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderSplitFirstPlan(runFolder)
            : null;

    public static BuilderTieredRoutingPolicy? LoadBuilderTieredRoutingPolicy(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderTieredRoutingPolicy?>(BuilderTieredRoutingPolicyPath(runFolder), null);

    public static BuilderTieredRoutingPolicy? LoadLatestBuilderTieredRoutingPolicy(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderTieredRoutingPolicy(runFolder)
            : null;

    public static BuilderSplitStepExecution? LoadBuilderSplitStepExecution(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderSplitStepExecution?>(BuilderSplitStepExecutionPath(runFolder), null);

    public static BuilderSplitStepExecution? LoadLatestBuilderSplitStepExecution(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderSplitStepExecution(runFolder)
            : null;

    public static BuilderSplitFirstOutcome? LoadBuilderSplitFirstOutcome(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderSplitFirstOutcome?>(BuilderSplitFirstOutcomePath(runFolder), null);

    public static BuilderSplitFirstOutcome? LoadLatestBuilderSplitFirstOutcome(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderSplitFirstOutcome(runFolder)
            : null;

    public static BuilderDefaultPolicy? LoadBuilderDefaultPolicy(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderDefaultPolicy?>(BuilderDefaultPolicyPath(runFolder), null);

    public static BuilderDefaultPolicy? LoadLatestBuilderDefaultPolicy(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderDefaultPolicy(runFolder)
            : null;

    public static BuilderDefaultPolicyHistory LoadBuilderDefaultPolicyHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderDefaultPolicyHistoryPathForRepo(repoRoot),
            new BuilderDefaultPolicyHistory(20, Array.Empty<BuilderDefaultPolicyHistoryEntry>()));

    public static BuilderRequestPolicyDecision? LoadBuilderRequestPolicyDecision(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRequestPolicyDecision?>(BuilderRequestPolicyDecisionPath(runFolder), null);

    public static BuilderRequestPolicyDecision? LoadLatestBuilderRequestPolicyDecision(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderRequestPolicyDecision(runFolder)
            : null;

    public static BuilderPolicyStability? LoadBuilderPolicyStability(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderPolicyStability?>(BuilderPolicyStabilityPath(runFolder), null);

    public static BuilderPolicyStability? LoadLatestBuilderPolicyStability(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderPolicyStability(runFolder)
            : null;

    public static BuilderRequestIntake? LoadBuilderRequestIntake(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRequestIntake?>(BuilderRequestIntakePath(runFolder), null);

    public static BuilderRequestIntake? LoadLatestBuilderRequestIntake(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderRequestIntake(runFolder)
            : null;

    public static BuilderRequestIntakeHistory LoadBuilderRequestIntakeHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderRequestIntakeHistoryPathForRepo(repoRoot),
            new BuilderRequestIntakeHistory(20, Array.Empty<BuilderRequestIntakeHistoryEntry>()));

    public static BuilderExecutionPrep? LoadBuilderExecutionPrep(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderExecutionPrep?>(BuilderExecutionPrepPath(runFolder), null);

    public static BuilderExecutionPrep? LoadLatestBuilderExecutionPrep(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderExecutionPrep(runFolder)
            : null;

    public static BuilderExecutionPrepHistory LoadBuilderExecutionPrepHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderExecutionPrepHistoryPathForRepo(repoRoot),
            new BuilderExecutionPrepHistory(20, Array.Empty<BuilderExecutionPrepHistoryEntry>()));

    public static PreparedBuilderExecutionLaunch? LoadBuilderExecutionLaunch(string runFolder)
        => TryLoadBuilderProofArtifact<PreparedBuilderExecutionLaunch?>(BuilderExecutionLaunchPath(runFolder), null);

    public static PreparedBuilderExecutionLaunch? LoadLatestBuilderExecutionLaunch(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderExecutionLaunch(runFolder)
            : null;

    public static PreparedBuilderExecutionResult? LoadBuilderExecutionResult(string runFolder)
        => TryLoadBuilderProofArtifact<PreparedBuilderExecutionResult?>(BuilderExecutionResultPath(runFolder), null);

    public static PreparedBuilderExecutionResult? LoadLatestBuilderExecutionResult(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderExecutionResult(runFolder)
            : null;

    public static BuilderReadinessGate? LoadBuilderReadinessGate(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderReadinessGate?>(BuilderReadinessGatePath(runFolder), null);

    public static BuilderReadinessGate? LoadLatestBuilderReadinessGate(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderReadinessGate(runFolder)
            : null;

    public static BuilderReadinessGateHistory LoadBuilderReadinessGateHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderReadinessGateHistoryPathForRepo(repoRoot),
            new BuilderReadinessGateHistory(BuilderReadinessGateHistoryRetentionCount, Array.Empty<BuilderReadinessGateHistoryEntry>()));

    public static BuilderConfirmedTaskClasses? LoadBuilderConfirmedTaskClasses(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderConfirmedTaskClasses?>(BuilderConfirmedTaskClassesPath(runFolder), null);

    public static BuilderConfirmedTaskClasses? LoadLatestBuilderConfirmedTaskClasses(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderConfirmedTaskClasses(runFolder)
            : null;

    public static BuilderDefaultRouteDecision? LoadBuilderDefaultRouteDecision(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderDefaultRouteDecision?>(BuilderDefaultRouteDecisionPath(runFolder), null);

    public static BuilderDefaultRouteDecision? LoadLatestBuilderDefaultRouteDecision(string repoRoot)
        => LoadBuilderRouteArtifactFromCurrentStateIndex(
            repoRoot,
            "builder_default_route_decision",
            LoadBuilderDefaultRouteDecision);

    public static BuilderReadinessContradictions? LoadBuilderReadinessContradictions(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderReadinessContradictions?>(BuilderReadinessContradictionsPath(runFolder), null);

    public static BuilderReadinessContradictions? LoadLatestBuilderReadinessContradictions(string repoRoot)
        => LoadBuilderRouteArtifactFromCurrentStateIndex(
            repoRoot,
            "builder_readiness_contradictions",
            LoadBuilderReadinessContradictions);

    public static BuilderLaunchDefaultDecision? LoadBuilderLaunchDefaultDecision(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderLaunchDefaultDecision?>(BuilderLaunchDefaultDecisionPath(runFolder), null);

    public static BuilderLaunchDefaultDecision? LoadLatestBuilderLaunchDefaultDecision(string repoRoot)
        => LoadBuilderRouteArtifactFromCurrentStateIndex(
            repoRoot,
            "builder_launch_default_decision",
            LoadBuilderLaunchDefaultDecision);

    public static BuilderRouteOverrideEvidence? LoadBuilderRouteOverrideEvidence(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRouteOverrideEvidence?>(BuilderRouteOverrideEvidencePath(runFolder), null);

    public static BuilderRouteOverrideEvidence? LoadLatestBuilderRouteOverrideEvidence(string repoRoot)
        => LoadBuilderRouteArtifactFromCurrentStateIndex(
            repoRoot,
            "builder_route_override_evidence",
            LoadBuilderRouteOverrideEvidence);

    public static BuilderRouteReviewCandidates? LoadBuilderPolicyReviewCandidates(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRouteReviewCandidates?>(BuilderPolicyReviewCandidatesPath(runFolder), null);

    public static BuilderRouteReviewCandidates? LoadLatestBuilderPolicyReviewCandidates(string repoRoot)
        => LoadBuilderRouteArtifactFromCurrentStateIndex(
            repoRoot,
            "builder_policy_review_candidates",
            LoadBuilderPolicyReviewCandidates);

    public static BuilderRouteReconfirmation? LoadBuilderRouteReconfirmation(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRouteReconfirmation?>(BuilderRouteReconfirmationPath(runFolder), null);

    public static BuilderRouteReconfirmation? LoadLatestBuilderRouteReconfirmation(string repoRoot)
        => LoadBuilderRouteArtifactFromCurrentStateIndex(
            repoRoot,
            "builder_route_reconfirmation",
            LoadBuilderRouteReconfirmation);

    public static BuilderDefaultRouteRecovery? LoadBuilderDefaultRouteRecovery(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderDefaultRouteRecovery?>(BuilderDefaultRouteRecoveryPath(runFolder), null);

    public static BuilderDefaultRouteRecovery? LoadLatestBuilderDefaultRouteRecovery(string repoRoot)
        => LoadBuilderRouteArtifactFromCurrentStateIndex(
            repoRoot,
            "builder_default_route_recovery",
            LoadBuilderDefaultRouteRecovery);

    public static BuilderCapabilityBlockDecision? LoadBuilderCapabilityBlockDecision(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderCapabilityBlockDecision?>(BuilderCapabilityBlockDecisionPath(runFolder), null);

    public static BuilderCapabilityBlockDecision? LoadLatestBuilderCapabilityBlockDecision(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderCapabilityBlockDecision(runFolder)
            : null;

    public static BuilderProofMatrixDefinition? LoadBuilderProofMatrix(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderProofMatrixDefinition?>(BuilderProofMatrixArtifactPath(runFolder), null);

    public async Task<BuilderProofRun> RunBuilderProofMatrixAsync(
        string repoRoot,
        string? configuredModelId = null,
        string provider = "ollama",
        Action<NarrationEvent>? narrate = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ct.ThrowIfCancellationRequested();

        var effectiveModelId = string.IsNullOrWhiteSpace(configuredModelId)
            ? BuilderProofFloorModelId
            : configuredModelId.Trim();
        var startedUtc = DateTimeOffset.UtcNow;
        var proofRunId = $"{startedUtc:yyyyMMdd-HHmmssfffZ}-{SanitizeBuilderProofToken(effectiveModelId)}";
        var runFolder = Path.Combine(BuilderProofRunsRootForRepo(repoRoot), proofRunId);
        Directory.CreateDirectory(runFolder);

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "info", "BUILDER_PROOF_STARTED", new Dictionary<string, string>
        {
            ["proof_run_id"] = proofRunId,
            ["model_id"] = effectiveModelId,
            ["provider"] = provider
        }));

        var repoLocalMatrix = BuildProofMatrixDefinition(proofRunId, runFolder, effectiveModelId);
        File.WriteAllText(BuilderProofMatrixArtifactPath(runFolder), JsonSerializer.Serialize(repoLocalMatrix, new JsonSerializerOptions { WriteIndented = true }));

        var priorHistory = LoadBuilderProofHistory(repoRoot);
        var repoLocalCaseResults = await ExecuteProofCaseSetAsync(
            repoRoot,
            runFolder,
            Path.Combine(runFolder, "targets"),
            effectiveModelId,
            provider,
            repoLocalMatrix,
            priorHistory,
            narrate,
            ct).ConfigureAwait(false);

        var repoLocalVerdict = BuildModelFloorVerdict(repoRoot, runFolder, effectiveModelId, repoLocalMatrix, repoLocalCaseResults, priorHistory);
        File.WriteAllText(BuilderModelFloorVerdictPath(runFolder), JsonSerializer.Serialize(repoLocalVerdict, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderModelFloorSummaryPath(runFolder), BuildModelFloorSummaryMarkdown(repoLocalVerdict));

        var externalMatrix = BuildExternalProofMatrixDefinition(proofRunId, runFolder, effectiveModelId);
        var externalStartedUtc = DateTimeOffset.UtcNow;
        var externalCaseResults = await ExecuteProofCaseSetAsync(
            repoRoot,
            runFolder,
            Path.Combine(runFolder, "external-target-pack", "targets"),
            effectiveModelId,
            provider,
            externalMatrix,
            priorHistory,
            narrate,
            ct).ConfigureAwait(false);
        var externalVerdict = BuildExternalFloorVerdict(runFolder, effectiveModelId, externalMatrix, externalCaseResults, priorHistory);
        var externalRun = BuildExternalProofRun(
            repoRoot,
            runFolder,
            proofRunId,
            effectiveModelId,
            provider,
            externalMatrix,
            externalCaseResults,
            externalVerdict,
            externalStartedUtc,
            DateTimeOffset.UtcNow);
        File.WriteAllText(BuilderExternalProofRunPath(runFolder), JsonSerializer.Serialize(externalRun, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderExternalProofSummaryPath(runFolder), BuildExternalProofSummaryMarkdown(externalRun));
        File.WriteAllText(BuilderExternalFloorVerdictPath(runFolder), JsonSerializer.Serialize(externalVerdict, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderExternalFloorSummaryPath(runFolder), BuildExternalFloorSummaryMarkdown(externalVerdict));

        var failurePatterns = BuildBuilderProofFailurePatternSummary(
            runFolder,
            effectiveModelId,
            repoLocalCaseResults,
            externalCaseResults);
        File.WriteAllText(BuilderModelFloorFailurePatternsPath(runFolder), JsonSerializer.Serialize(failurePatterns, new JsonSerializerOptions { WriteIndented = true }));

        var policy = BuildBuilderModelFloorPolicy(
            runFolder,
            effectiveModelId,
            repoLocalVerdict,
            externalVerdict,
            repoLocalMatrix,
            repoLocalCaseResults,
            externalMatrix,
            externalCaseResults,
            failurePatterns);
        File.WriteAllText(BuilderModelFloorPolicyPath(runFolder), JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderModelFloorPolicySummaryPath(runFolder), BuildBuilderModelFloorPolicyMarkdown(policy));

        var trustBands = BuildBuilderModelTrustBands(
            runFolder,
            effectiveModelId,
            repoLocalVerdict,
            externalVerdict,
            repoLocalMatrix,
            repoLocalCaseResults,
            externalMatrix,
            externalCaseResults,
            failurePatterns);
        File.WriteAllText(BuilderModelTrustBandsPath(runFolder), JsonSerializer.Serialize(trustBands, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderModelScopeSummaryPath(runFolder), BuildBuilderModelScopeSummaryMarkdown(trustBands));

        repoLocalCaseResults = ApplyTrustBandMetadata(repoLocalCaseResults, trustBands);
        externalCaseResults = ApplyTrustBandMetadata(externalCaseResults, trustBands);
        externalRun = BuildExternalProofRun(
            repoRoot,
            runFolder,
            proofRunId,
            effectiveModelId,
            provider,
            externalMatrix,
            externalCaseResults,
            externalVerdict,
            externalStartedUtc,
            DateTimeOffset.UtcNow);
        File.WriteAllText(BuilderExternalProofRunPath(runFolder), JsonSerializer.Serialize(externalRun, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderExternalProofSummaryPath(runFolder), BuildExternalProofSummaryMarkdown(externalRun));

        var routingRecommendation = BuildBuilderModelRoutingRecommendation(
            runFolder,
            effectiveModelId,
            repoLocalMatrix,
            repoLocalCaseResults,
            externalMatrix,
            externalCaseResults,
            trustBands);
        File.WriteAllText(BuilderModelRoutingRecommendationPath(runFolder), JsonSerializer.Serialize(routingRecommendation, new JsonSerializerOptions { WriteIndented = true }));

        var escalationDecision = BuildBuilderModelEscalationDecision(
            runFolder,
            effectiveModelId,
            repoLocalMatrix,
            repoLocalCaseResults,
            externalMatrix,
            externalCaseResults,
            trustBands,
            routingRecommendation);
        File.WriteAllText(BuilderModelEscalationDecisionPath(runFolder), JsonSerializer.Serialize(escalationDecision, new JsonSerializerOptions { WriteIndented = true }));

        var routingPlan = BuildBuilderModelRoutingPlan(
            runFolder,
            effectiveModelId,
            escalationDecision,
            routingRecommendation,
            trustBands);
        File.WriteAllText(BuilderModelRoutingPlanPath(runFolder), JsonSerializer.Serialize(routingPlan, new JsonSerializerOptions { WriteIndented = true }));

        var strongerTierAvailability = await ResolveBuilderStrongerTierAvailabilityAsync(
            runFolder,
            effectiveModelId,
            routingPlan.RecommendedModelClass,
            preferredStrongerModelId: null,
            provider,
            ct).ConfigureAwait(false);
        File.WriteAllText(BuilderStrongerTierAvailabilityPath(runFolder), JsonSerializer.Serialize(strongerTierAvailability, new JsonSerializerOptions { WriteIndented = true }));

        var overallSummary = BuildOverallBuilderProofSummary(repoLocalVerdict, externalVerdict, policy, trustBands, routingRecommendation, escalationDecision, routingPlan);
        var run = BuildProofRun(repoRoot, runFolder, proofRunId, effectiveModelId, provider, repoLocalMatrix, repoLocalCaseResults, repoLocalVerdict, overallSummary, startedUtc, DateTimeOffset.UtcNow);
        File.WriteAllText(BuilderProofRunArtifactPath(runFolder), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderProofSummaryPath(runFolder), BuildProofSummaryMarkdown(run, externalRun, failurePatterns, policy, trustBands, routingRecommendation, escalationDecision, routingPlan));
        WriteBuilderProofHistory(repoRoot, run, externalCaseResults, priorHistory);
        RefreshBuilderDefaultPolicyArtifacts(repoRoot, runFolder);

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "result", "BUILDER_PROOF_COMPLETED", new Dictionary<string, string>
        {
            ["proof_run_id"] = proofRunId,
            ["model_id"] = effectiveModelId,
            ["verdict"] = repoLocalVerdict.Verdict,
            ["external_verdict"] = externalVerdict.Verdict,
            ["run_folder"] = runFolder
        }));

        return run;
    }

    public async Task<BuilderComparativeProofRun> RunBuilderComparativeProofAsync(
        string repoRoot,
        string? preferredStrongerModelId = null,
        string provider = "ollama",
        Action<NarrationEvent>? narrate = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ct.ThrowIfCancellationRequested();

        var latestRun = LoadLatestBuilderProofRun(repoRoot)
            ?? throw new InvalidOperationException("Comparative proof requires a completed builder proof matrix run.");
        var repoLocalMatrix = LoadBuilderProofMatrix(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Comparative proof requires builder_proof_matrix.json from the latest proof run.");
        var externalMatrix = LoadBuilderExternalProofRun(latestRun.RunFolder)?.TargetPack
            ?? BuildExternalProofMatrixDefinition(latestRun.ProofRunId, latestRun.RunFolder, latestRun.ModelId);
        var trustBands = LoadBuilderModelTrustBands(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Comparative proof requires builder_model_trust_bands.json from the latest proof run.");
        var routingRecommendation = LoadBuilderModelRoutingRecommendation(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Comparative proof requires builder_model_routing_recommendation.json from the latest proof run.");
        var escalationDecision = LoadBuilderModelEscalationDecision(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Comparative proof requires builder_model_escalation_decision.json from the latest proof run.");
        var routingPlan = LoadBuilderModelRoutingPlan(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Comparative proof requires builder_model_routing_plan.json from the latest proof run.");

        var preconditionFailure = GetComparativeProofPreconditionFailure(latestRun, escalationDecision, routingPlan);
        if (!string.IsNullOrWhiteSpace(preconditionFailure))
        {
            throw new InvalidOperationException(preconditionFailure);
        }

        var strongerTierAvailability = await ResolveBuilderStrongerTierAvailabilityAsync(
            latestRun.RunFolder,
            latestRun.ModelId,
            routingPlan.RecommendedModelClass,
            preferredStrongerModelId,
            provider,
            ct).ConfigureAwait(false);
        File.WriteAllText(BuilderStrongerTierAvailabilityPath(latestRun.RunFolder), JsonSerializer.Serialize(strongerTierAvailability, new JsonSerializerOptions { WriteIndented = true }));

        if (!string.Equals(strongerTierAvailability.AvailabilityState, "available", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Comparative proof is blocked because {BuildBuilderStrongerTierAvailabilitySummary(strongerTierAvailability).TrimEnd('.').ToLowerInvariant()}.");
        }

        var sourceCase = latestRun.CaseResults.FirstOrDefault(result =>
            string.Equals(result.ProofScope, escalationDecision.ProofScope, StringComparison.Ordinal) &&
            string.Equals(result.TargetId, escalationDecision.TargetId, StringComparison.Ordinal));
        if (sourceCase is null)
        {
            throw new InvalidOperationException("Comparative proof could not locate the escalation target inside the latest builder proof run.");
        }

        var sourceTarget = ResolveComparativeSourceTarget(escalationDecision, repoLocalMatrix, externalMatrix)
            ?? throw new InvalidOperationException("Comparative proof could not resolve the bounded source target definition.");

        var priorHistory = LoadBuilderProofHistory(repoRoot);
        var comparativeFolder = Path.Combine(latestRun.RunFolder, "comparative-proof");
        Directory.CreateDirectory(comparativeFolder);

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "info", "BUILDER_COMPARATIVE_PROOF_STARTED", new Dictionary<string, string>
        {
            ["proof_run_id"] = latestRun.ProofRunId,
            ["target_id"] = sourceTarget.TargetId,
            ["task_class"] = sourceTarget.TaskClass,
            ["current_model"] = latestRun.ModelId,
            ["stronger_model"] = strongerTierAvailability.ConfiguredStrongerTierId
        }));

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "step", "BUILDER_COMPARATIVE_PROOF_CASE_STARTED", new Dictionary<string, string>
        {
            ["case_kind"] = "stronger_tier",
            ["target_id"] = sourceTarget.TargetId,
            ["model_id"] = strongerTierAvailability.ConfiguredStrongerTierId
        }));

        var strongerCase = await ExecuteProofCaseAsync(
            repoRoot,
            latestRun.RunFolder,
            Path.Combine(comparativeFolder, "stronger-tier"),
            strongerTierAvailability.ConfiguredStrongerTierId,
            provider,
            escalationDecision.ProofScope,
            sourceTarget,
            priorHistory,
            ct).ConfigureAwait(false);

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "result", "BUILDER_COMPARATIVE_PROOF_CASE_COMPLETED", new Dictionary<string, string>
        {
            ["case_kind"] = "stronger_tier",
            ["target_id"] = sourceTarget.TargetId,
            ["model_id"] = strongerTierAvailability.ConfiguredStrongerTierId,
            ["final_classification"] = strongerCase.FinalClassification
        }));

        BuilderProofCaseResult? splitFloorCase = null;
        if (SupportsSplitComparativeProof(routingPlan, sourceTarget))
        {
            var splitTarget = BuildSplitComparativeTarget(sourceTarget);
            if (splitTarget is not null)
            {
                narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "step", "BUILDER_COMPARATIVE_PROOF_CASE_STARTED", new Dictionary<string, string>
                {
                    ["case_kind"] = "split_floor",
                    ["target_id"] = splitTarget.TargetId,
                    ["model_id"] = latestRun.ModelId
                }));

                splitFloorCase = await ExecuteProofCaseAsync(
                    repoRoot,
                    latestRun.RunFolder,
                    Path.Combine(comparativeFolder, "split-floor"),
                    latestRun.ModelId,
                    provider,
                    escalationDecision.ProofScope,
                    splitTarget,
                    priorHistory,
                    ct).ConfigureAwait(false);

                narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "result", "BUILDER_COMPARATIVE_PROOF_CASE_COMPLETED", new Dictionary<string, string>
                {
                    ["case_kind"] = "split_floor",
                    ["target_id"] = splitTarget.TargetId,
                    ["model_id"] = latestRun.ModelId,
                    ["final_classification"] = splitFloorCase.FinalClassification
                }));
            }
        }

        var weakSpotOutcomes = BuildBuilderWeakSpotComparativeOutcomes(sourceCase, strongerCase, escalationDecision);
        var comparativeClassification = DetermineComparativeProofClassification(sourceCase, strongerCase, splitFloorCase);
        var splitThenEscalateEvidenceState = DetermineSplitThenEscalateEvidenceState(sourceCase, strongerCase, splitFloorCase);
        var splitThenEscalateSummary = BuildSplitThenEscalateEvidenceSummary(sourceCase, strongerCase, splitFloorCase, splitThenEscalateEvidenceState);
        var repairBurdenDifferenceSummary = BuildComparativeRepairBurdenSummary(sourceCase, strongerCase, splitFloorCase);
        var comparativeSummary = BuildComparativeProofSummary(sourceCase, strongerCase, splitFloorCase, comparativeClassification, splitThenEscalateSummary, repairBurdenDifferenceSummary);
        var comparativeRun = new BuilderComparativeProofRun(
            latestRun.ProofRunId,
            repoRoot,
            latestRun.RunFolder,
            comparativeFolder,
            latestRun.ModelId,
            strongerTierAvailability.ConfiguredStrongerTierId,
            routingPlan.RecommendedModelClass,
            escalationDecision.ProofScope,
            escalationDecision.TargetId,
            escalationDecision.TargetLabel,
            escalationDecision.TaskClass,
            escalationDecision.ComplexityDimensions,
            sourceCase,
            strongerCase,
            splitFloorCase,
            splitThenEscalateEvidenceState,
            splitThenEscalateSummary,
            repairBurdenDifferenceSummary,
            weakSpotOutcomes,
            comparativeClassification,
            comparativeSummary,
            BuilderComparativeProofRunPath(latestRun.RunFolder),
            BuilderComparativeProofSummaryPath(latestRun.RunFolder),
            DateTimeOffset.UtcNow);
        File.WriteAllText(BuilderComparativeProofRunPath(latestRun.RunFolder), JsonSerializer.Serialize(comparativeRun, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderComparativeProofSummaryPath(latestRun.RunFolder), BuildComparativeProofSummaryMarkdown(comparativeRun));

        var routingPolicyEvidence = BuildBuilderRoutingPolicyEvidence(
            latestRun.RunFolder,
            routingPlan,
            escalationDecision,
            strongerTierAvailability,
            comparativeRun);
        File.WriteAllText(BuilderRoutingPolicyEvidencePath(latestRun.RunFolder), JsonSerializer.Serialize(routingPolicyEvidence, new JsonSerializerOptions { WriteIndented = true }));
        var splitFirstPlan = BuildBuilderSplitFirstPlan(
            latestRun.RunFolder,
            routingPlan,
            escalationDecision,
            comparativeRun,
            routingPolicyEvidence);
        File.WriteAllText(BuilderSplitFirstPlanPath(latestRun.RunFolder), JsonSerializer.Serialize(splitFirstPlan, new JsonSerializerOptions { WriteIndented = true }));
        WriteBuilderSplitExecutionHooks(latestRun.RunFolder, splitFirstPlan);

        var tieredRoutingPolicy = BuildBuilderTieredRoutingPolicy(
            latestRun.RunFolder,
            routingPlan,
            escalationDecision,
            comparativeRun,
            routingPolicyEvidence,
            splitFirstPlan);
        File.WriteAllText(BuilderTieredRoutingPolicyPath(latestRun.RunFolder), JsonSerializer.Serialize(tieredRoutingPolicy, new JsonSerializerOptions { WriteIndented = true }));

        var splitStepExecution = BuildBuilderSplitStepExecution(
            repoRoot,
            latestRun.RunFolder,
            comparativeRun,
            splitFirstPlan);
        File.WriteAllText(BuilderSplitStepExecutionPath(latestRun.RunFolder), JsonSerializer.Serialize(splitStepExecution, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(BuilderSplitFirstOutcomePath(latestRun.RunFolder)))
        {
            File.Delete(BuilderSplitFirstOutcomePath(latestRun.RunFolder));
        }

        WriteBuilderComparativeProofHook(latestRun.RunFolder, routingPolicyEvidence, comparativeRun, splitFirstPlan, tieredRoutingPolicy);
        RefreshBuilderDefaultPolicyArtifacts(repoRoot, latestRun.RunFolder);

        narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "result", "BUILDER_COMPARATIVE_PROOF_COMPLETED", new Dictionary<string, string>
        {
            ["proof_run_id"] = latestRun.ProofRunId,
            ["target_id"] = sourceTarget.TargetId,
            ["current_model"] = latestRun.ModelId,
            ["stronger_model"] = strongerTierAvailability.ConfiguredStrongerTierId,
            ["comparative_classification"] = comparativeClassification,
            ["routing_policy_state"] = routingPolicyEvidence.RoutingPolicyState
        }));

        return comparativeRun;
    }

    public static BuilderSplitStepExecution RecordBuilderSplitStepInteraction(
        string repoRoot,
        string runFolder,
        string stepId,
        string executionState,
        string actionKind,
        string detail,
        string evidencePath)
    {
        var comparativeRun = LoadBuilderComparativeProofRun(runFolder);
        var splitPlan = LoadBuilderSplitFirstPlan(runFolder);
        if (comparativeRun is null || splitPlan is null)
        {
            return new BuilderSplitStepExecution(
                string.Empty,
                runFolder,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<BuilderSplitStepExecutionStepState>(),
                "Split-step execution is unavailable until comparative proof creates a split-first plan.",
                BuilderSplitStepExecutionPath(runFolder),
                DateTimeOffset.UtcNow);
        }

        var current = LoadBuilderSplitStepExecution(runFolder) ?? BuildBuilderSplitStepExecution(repoRoot, runFolder, comparativeRun, splitPlan);
        var refreshed = RefreshBuilderSplitStepExecution(repoRoot, current, splitPlan, comparativeRun, LoadBuilderSplitFirstOutcome(runFolder));
        var updatedSteps = refreshed.Steps
            .Select(step =>
            {
                if (!string.Equals(step.StepId, stepId, StringComparison.Ordinal))
                    return step;

                var mergedExecutionState = MergeBuilderSplitStepExecutionState(step.ExecutionState, executionState);
                var linkedArtifactPaths = step.LinkedArtifactPaths
                    .Concat(string.IsNullOrWhiteSpace(evidencePath) ? Array.Empty<string>() : new[] { evidencePath })
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return step with
                {
                    ExecutionState = mergedExecutionState,
                    Detail = string.IsNullOrWhiteSpace(detail) ? step.Detail : detail,
                    EvidencePath = string.IsNullOrWhiteSpace(evidencePath) ? step.EvidencePath : evidencePath,
                    LastActionKind = string.IsNullOrWhiteSpace(actionKind) ? step.LastActionKind : actionKind,
                    LinkedArtifactPaths = linkedArtifactPaths,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            })
            .OrderBy(step => step.StepNumber)
            .ThenBy(step => step.StepId, StringComparer.Ordinal)
            .ToArray();
        var updated = RefreshBuilderSplitStepExecution(
            repoRoot,
            refreshed with
            {
                RecordedUtc = DateTimeOffset.UtcNow,
                Steps = updatedSteps
            },
            splitPlan,
            comparativeRun,
            LoadBuilderSplitFirstOutcome(runFolder));
        File.WriteAllText(updated.ArtifactPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        return updated;
    }

    public async Task<BuilderSplitFirstOutcome> RunBuilderSplitStepRerunAsync(
        string repoRoot,
        string provider = "ollama",
        string? stepId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ct.ThrowIfCancellationRequested();

        var latestRun = LoadLatestBuilderProofRun(repoRoot)
            ?? throw new InvalidOperationException("Split-step rerun requires a recorded builder proof run.");
        var comparativeRun = LoadBuilderComparativeProofRun(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Split-step rerun requires builder_comparative_proof_run.json from the latest proof run.");
        var splitPlan = LoadBuilderSplitFirstPlan(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Split-step rerun requires builder_split_first_plan.json from the latest proof run.");
        var currentExecution = LoadBuilderSplitStepExecution(latestRun.RunFolder)
            ?? BuildBuilderSplitStepExecution(repoRoot, latestRun.RunFolder, comparativeRun, splitPlan);
        currentExecution = RefreshBuilderSplitStepExecution(repoRoot, currentExecution, splitPlan, comparativeRun, LoadBuilderSplitFirstOutcome(latestRun.RunFolder));

        var rerunStep = ResolveBuilderSplitRerunStep(currentExecution, stepId);
        if (rerunStep is null)
        {
            throw new InvalidOperationException("No eligible rerun-capable split step is available for the latest builder proof run.");
        }

        if (!string.IsNullOrWhiteSpace(rerunStep.BlockReason))
        {
            throw new InvalidOperationException(rerunStep.BlockReason);
        }

        var sourceTarget = ResolveBuilderComparativeSourceTarget(latestRun.RunFolder, comparativeRun)
            ?? throw new InvalidOperationException("Split-step rerun could not resolve the bounded comparative target.");
        var splitTarget = BuildSplitComparativeTarget(sourceTarget)
            ?? throw new InvalidOperationException("Split-step rerun is only supported for bounded split-first comparative targets.");
        var priorHistory = LoadBuilderProofHistory(repoRoot);
        var splitExecutionRoot = Path.Combine(latestRun.RunFolder, "split-step-execution");
        Directory.CreateDirectory(splitExecutionRoot);

        var rerunResult = await ExecuteProofCaseAsync(
            repoRoot,
            latestRun.RunFolder,
            splitExecutionRoot,
            comparativeRun.CurrentModelId,
            provider,
            comparativeRun.ProofScope,
            splitTarget,
            priorHistory,
            ct).ConfigureAwait(false);

        var linkedOutcomeArtifacts = BuildBuilderSplitOutcomeArtifactPaths(rerunResult);
        var updatedSteps = currentExecution.Steps
            .Select(step =>
            {
                if (!string.Equals(step.StepId, rerunStep.StepId, StringComparison.Ordinal))
                    return step;

                return step with
                {
                    ExecutionState = "completed_by_outcome",
                    Detail = rerunResult.FinalSummary,
                    EvidencePath = linkedOutcomeArtifacts.FirstOrDefault(path => File.Exists(path)) ?? linkedOutcomeArtifacts.FirstOrDefault() ?? step.EvidencePath,
                    LastActionKind = rerunStep.StepType,
                    LinkedArtifactPaths = step.LinkedArtifactPaths
                        .Concat(linkedOutcomeArtifacts)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            })
            .OrderBy(step => step.StepNumber)
            .ThenBy(step => step.StepId, StringComparer.Ordinal)
            .ToArray();
        var updatedExecution = RefreshBuilderSplitStepExecution(
            repoRoot,
            currentExecution with
            {
                RecordedUtc = DateTimeOffset.UtcNow,
                Steps = updatedSteps
            },
            splitPlan,
            comparativeRun,
            null);

        var outcome = BuildBuilderSplitFirstOutcome(latestRun.RunFolder, comparativeRun, splitPlan, updatedExecution, rerunStep, rerunResult);
        File.WriteAllText(BuilderSplitFirstOutcomePath(latestRun.RunFolder), JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));

        updatedExecution = RefreshBuilderSplitStepExecution(repoRoot, updatedExecution, splitPlan, comparativeRun, outcome);
        File.WriteAllText(BuilderSplitStepExecutionPath(latestRun.RunFolder), JsonSerializer.Serialize(updatedExecution, new JsonSerializerOptions { WriteIndented = true }));
        RefreshBuilderDefaultPolicyArtifacts(repoRoot, latestRun.RunFolder);
        return outcome;
    }

    public async Task<BuilderSplitFirstOutcome> RunBuilderSplitFirstExecutionAsync(
        string repoRoot,
        string provider = "ollama",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ct.ThrowIfCancellationRequested();

        var latestRun = LoadLatestBuilderProofRun(repoRoot)
            ?? throw new InvalidOperationException("Split-first execution requires a recorded builder proof run.");
        var comparativeRun = LoadBuilderComparativeProofRun(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Split-first execution requires builder_comparative_proof_run.json from the latest proof run.");
        var splitPlan = LoadBuilderSplitFirstPlan(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Split-first execution requires builder_split_first_plan.json from the latest proof run.");
        var execution = LoadBuilderSplitStepExecution(latestRun.RunFolder)
            ?? BuildBuilderSplitStepExecution(repoRoot, latestRun.RunFolder, comparativeRun, splitPlan);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            execution = RefreshBuilderSplitStepExecution(repoRoot, execution, splitPlan, comparativeRun, LoadBuilderSplitFirstOutcome(latestRun.RunFolder));
            var step = execution.Steps
                .Where(item => string.Equals(item.EligibilityState, "eligible", StringComparison.Ordinal) &&
                               string.Equals(item.ExecutionState, "not_started", StringComparison.Ordinal))
                .OrderBy(item => item.StepNumber)
                .FirstOrDefault();
            if (step is null)
            {
                break;
            }

            if (string.Equals(step.ExecutionMode, "rerun_capable", StringComparison.Ordinal))
            {
                return await RunBuilderSplitStepRerunAsync(repoRoot, provider, step.StepId, ct).ConfigureAwait(false);
            }

            var completionState = string.Equals(step.ExecutionMode, "view_only", StringComparison.Ordinal)
                ? "opened"
                : "executed";
            var actionKind = string.IsNullOrWhiteSpace(step.StepType) ? "split_step" : step.StepType;
            var evidencePath = ResolveBuilderSplitStepPrimaryPath(step);
            execution = RecordBuilderSplitStepInteraction(
                repoRoot,
                latestRun.RunFolder,
                step.StepId,
                completionState,
                actionKind,
                step.Detail,
                evidencePath);
        }

        RefreshBuilderDefaultPolicyArtifacts(repoRoot, latestRun.RunFolder);
        return LoadBuilderSplitFirstOutcome(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Split-first execution did not reach a rerun-capable step.");
    }

    public async Task<PreparedBuilderExecutionResult> LaunchPreparedBuilderRouteAsync(
        string repoRoot,
        string provider = "ollama",
        string? routeOverride = null,
        string? overrideReason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ct.ThrowIfCancellationRequested();

        var latestRun = LoadLatestBuilderProofRun(repoRoot)
            ?? throw new InvalidOperationException("Prepared builder launch requires a recorded builder proof run.");
        var intake = LoadBuilderRequestIntake(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Prepared builder launch requires builder_request_intake.json from the latest proof run.");
        var prep = LoadBuilderExecutionPrep(latestRun.RunFolder)
            ?? throw new InvalidOperationException("Prepared builder launch requires builder_execution_prep.json from the latest proof run.");
        var defaultRouteDecision = LoadBuilderDefaultRouteDecision(latestRun.RunFolder);
        var readinessGate = LoadBuilderReadinessGate(latestRun.RunFolder);
        var confirmedTaskClasses = LoadBuilderConfirmedTaskClasses(latestRun.RunFolder);
        var contradictions = LoadBuilderReadinessContradictions(latestRun.RunFolder);
        var existingResult = LoadBuilderExecutionResult(latestRun.RunFolder);
        var effectivePrep = ApplyBuilderRouteOverride(prep, routeOverride, overrideReason, defaultRouteDecision);
        var launchDecision = BuildBuilderLaunchDefaultDecision(
            latestRun.RunFolder,
            intake,
            effectivePrep,
            defaultRouteDecision,
            readinessGate,
            confirmedTaskClasses,
            contradictions,
            existingResult,
            routeOverride,
            overrideReason);
        File.WriteAllText(BuilderLaunchDefaultDecisionPath(latestRun.RunFolder), JsonSerializer.Serialize(launchDecision, new JsonSerializerOptions { WriteIndented = true }));
        var launch = BuildBuilderExecutionLaunch(latestRun.RunFolder, latestRun, intake, effectivePrep, existingResult, launchDecision);
        File.WriteAllText(BuilderExecutionLaunchPath(latestRun.RunFolder), JsonSerializer.Serialize(launch, new JsonSerializerOptions { WriteIndented = true }));

        PreparedBuilderExecutionResult result;
        if (!string.Equals(launch.LaunchEligibilityState, "eligible", StringComparison.Ordinal))
        {
            result = BuildBlockedBuilderExecutionResult(latestRun.RunFolder, launch, intake, effectivePrep);
        }
        else if (string.Equals(effectivePrep.SelectedRoute, "split_first_low_floor_route", StringComparison.Ordinal))
        {
            var splitOutcome = await RunBuilderSplitFirstExecutionAsync(repoRoot, provider, ct).ConfigureAwait(false);
            result = BuildBuilderExecutionResultFromSplitOutcome(latestRun.RunFolder, launch, intake, effectivePrep, splitOutcome);
        }
        else
        {
            var preparedTarget = ResolveBuilderPreparedLaunchTarget(latestRun.RunFolder, intake)
                ?? throw new InvalidOperationException("Prepared builder launch could not resolve the bounded target for the selected route.");
            var priorHistory = LoadBuilderProofHistory(repoRoot);
            var launchRoot = Path.Combine(latestRun.RunFolder, "prepared-launch");
            Directory.CreateDirectory(launchRoot);
            var caseResult = await ExecuteProofCaseAsync(
                repoRoot,
                latestRun.RunFolder,
                launchRoot,
                intake.CurrentModelId,
                provider,
                intake.ProofScope,
                preparedTarget,
                priorHistory,
                ct).ConfigureAwait(false);
            result = BuildBuilderExecutionResultFromProofCase(latestRun.RunFolder, launch, intake, effectivePrep, caseResult);
        }

        File.WriteAllText(BuilderExecutionResultPath(latestRun.RunFolder), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        RefreshBuilderDefaultPolicyArtifacts(repoRoot, latestRun.RunFolder);
        RewriteSupersededBuilderExecutionLaunchArtifacts(repoRoot, latestRun.RunFolder);
        RewriteSupersededBuilderExecutionResultArtifacts(repoRoot, latestRun.RunFolder);
        RefreshBuilderRouteContinuityArtifacts(repoRoot);
        var refreshedDefaultRouteDecision = LoadBuilderDefaultRouteDecision(latestRun.RunFolder) ?? defaultRouteDecision;
        var refreshedReadinessGate = LoadBuilderReadinessGate(latestRun.RunFolder) ?? readinessGate;
        var refreshedConfirmedClasses = LoadBuilderConfirmedTaskClasses(latestRun.RunFolder) ?? confirmedTaskClasses;
        var refreshedContradictions = LoadBuilderReadinessContradictions(latestRun.RunFolder) ?? contradictions;
        var overrideEvidence = BuildBuilderRouteOverrideEvidence(
            repoRoot,
            latestRun.RunFolder,
            launchDecision,
            result,
            refreshedDefaultRouteDecision,
            refreshedReadinessGate,
            refreshedContradictions);
        File.WriteAllText(BuilderRouteOverrideEvidencePath(latestRun.RunFolder), JsonSerializer.Serialize(overrideEvidence, new JsonSerializerOptions { WriteIndented = true }));
        var reviewCandidates = BuildBuilderRouteReviewCandidates(
            repoRoot,
            latestRun.RunFolder,
            refreshedConfirmedClasses,
            refreshedContradictions);
        File.WriteAllText(BuilderPolicyReviewCandidatesPath(latestRun.RunFolder), JsonSerializer.Serialize(reviewCandidates, new JsonSerializerOptions { WriteIndented = true }));
        RefreshBuilderRouteRecoveryArtifacts(repoRoot, latestRun.RunFolder);
        RefreshBuilderRouteContinuityArtifacts(repoRoot);
        return result;
    }

    private async Task<BuilderStrongerTierAvailability> ResolveBuilderStrongerTierAvailabilityAsync(
        string runFolder,
        string currentModelId,
        string recommendedModelClass,
        string? preferredStrongerModelId,
        string provider,
        CancellationToken ct)
    {
        BuilderStrongerTierAvailability availability;
        if (!string.Equals(recommendedModelClass, "stronger_builder_tier", StringComparison.Ordinal))
        {
            availability = new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                string.Empty,
                "not_needed",
                "The latest routing state does not require a stronger-tier comparison path.",
                provider,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                "No stronger-tier resolution was required for the latest routing state.",
                Array.Empty<string>(),
                "No stronger-tier resolution was required for the latest routing state.",
                BuilderStrongerTierAvailabilityPath(runFolder),
                DateTimeOffset.UtcNow);
        }
        else
        {
            availability = await _builderStrongerTierResolver.ResolveAsync(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId,
                provider,
                ct).ConfigureAwait(false);
            availability = availability with
            {
                ArtifactPath = BuilderStrongerTierAvailabilityPath(runFolder),
                Summary = BuildBuilderStrongerTierAvailabilitySummary(availability)
            };
        }

        return availability;
    }

    private static string GetComparativeProofPreconditionFailure(
        BuilderProofRun latestRun,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan)
    {
        if (string.IsNullOrWhiteSpace(escalationDecision.TargetId) ||
            string.IsNullOrWhiteSpace(escalationDecision.TaskClass))
        {
            return "Comparative proof requires a recorded escalation target from the latest builder proof run.";
        }

        var escalationState = escalationDecision.EscalationRequirementState;
        if (!string.Equals(escalationState, "task_should_be_split_first", StringComparison.Ordinal) &&
            !string.Equals(escalationState, "stronger_model_recommended", StringComparison.Ordinal) &&
            !string.Equals(escalationState, "stronger_model_required", StringComparison.Ordinal))
        {
            return "Comparative proof is only available when the latest builder proof target is escalation-worthy or split-then-escalate.";
        }

        if (!IsBoundedComparativeScope(escalationDecision.ComplexityDimensions))
        {
            return "Comparative proof is limited to bounded single-project builder proof targets with no dependency changes.";
        }

        if (string.IsNullOrWhiteSpace(routingPlan.ComparativeProofHook.ComparisonKey))
        {
            return "Comparative proof requires a recorded comparison hook from the latest routing plan.";
        }

        if (latestRun.CaseResults.All(result =>
                !string.Equals(result.TargetId, escalationDecision.TargetId, StringComparison.Ordinal) ||
                !string.Equals(result.ProofScope, escalationDecision.ProofScope, StringComparison.Ordinal)))
        {
            return "Comparative proof could not match the latest routing target to a recorded builder proof case.";
        }

        return string.Empty;
    }

    private static bool IsBoundedComparativeScope(BuilderProofComplexityDimensions dimensions)
        => dimensions.ProjectCountTouched <= 1 &&
           dimensions.DependencyReferenceChangeCount == 0 &&
           dimensions.FileCountTouched <= 3 &&
           dimensions.NewFileCreationCount <= 1;

    private static BuilderProofTargetDefinition? ResolveComparativeSourceTarget(
        BuilderModelEscalationDecision escalationDecision,
        BuilderProofMatrixDefinition repoLocalMatrix,
        BuilderProofMatrixDefinition externalMatrix)
    {
        var matrix = string.Equals(escalationDecision.ProofScope, "external_target_pack", StringComparison.Ordinal)
            ? externalMatrix
            : repoLocalMatrix;
        return matrix.Targets.FirstOrDefault(target => string.Equals(target.TargetId, escalationDecision.TargetId, StringComparison.Ordinal));
    }

    private static bool SupportsSplitComparativeProof(BuilderModelRoutingPlan routingPlan, BuilderProofTargetDefinition sourceTarget)
        => (string.Equals(routingPlan.SplitTaskRecommendationState, "split_then_escalate", StringComparison.Ordinal) ||
            string.Equals(routingPlan.SplitTaskRecommendationState, "split_task_first", StringComparison.Ordinal)) &&
           string.Equals(sourceTarget.TemplateVariant, "bounded_refactor", StringComparison.Ordinal);

    private static BuilderProofTargetDefinition? BuildSplitComparativeTarget(BuilderProofTargetDefinition sourceTarget)
    {
        if (!string.Equals(sourceTarget.TemplateVariant, "bounded_refactor", StringComparison.Ordinal))
        {
            return null;
        }

        return new BuilderProofTargetDefinition(
            $"{sourceTarget.TargetId}-split",
            sourceTarget.TargetType,
            "bounded_refactor_split",
            $"{sourceTarget.TargetLabel} (split scope)",
            "RefactorProof.csproj with ProfileSummary.cs and Formatting/DisplayNameFormatter.cs only",
            "high",
            sourceTarget.HasTests,
            "Update only ProfileSummary.cs so it uses the existing Formatting/DisplayNameFormatter.cs implementation. Do not move files or widen scope.",
            "bounded_refactor_split",
            sourceTarget.AllowedAssistRules,
            new BuilderProofComplexityDimensions(1, 1, 0, false, 0, "low"));
    }

    private static string DetermineComparativeProofClassification(
        BuilderProofCaseResult sourceCase,
        BuilderProofCaseResult strongerCase,
        BuilderProofCaseResult? splitFloorCase)
    {
        if (!IsBuilderProofCaseSuccessful(strongerCase))
        {
            return "still_not_sufficient";
        }

        if (!IsBuilderProofCaseSuccessful(sourceCase))
        {
            if (splitFloorCase is not null && IsBuilderProofCaseSuccessful(splitFloorCase))
            {
                return string.Equals(strongerCase.FinalClassification, "passed_cleanly", StringComparison.Ordinal) &&
                       !string.Equals(splitFloorCase.FinalClassification, "passed_cleanly", StringComparison.Ordinal)
                    ? "reduced_repair_burden"
                    : "cleaner_success";
            }

            return "required_for_scope";
        }

        var sourceBurden = GetBuilderProofCaseBurdenScore(sourceCase);
        var strongerBurden = GetBuilderProofCaseBurdenScore(strongerCase);
        if (strongerBurden < sourceBurden)
        {
            return "reduced_repair_burden";
        }

        return "no_material_gain";
    }

    private static string DetermineSplitThenEscalateEvidenceState(
        BuilderProofCaseResult sourceCase,
        BuilderProofCaseResult strongerCase,
        BuilderProofCaseResult? splitFloorCase)
    {
        if (splitFloorCase is null)
        {
            return "not_applicable";
        }

        if (IsBuilderProofCaseSuccessful(splitFloorCase))
        {
            return "splitting_makes_low_floor_viable";
        }

        if (IsBuilderProofCaseSuccessful(strongerCase))
        {
            return "escalation_without_split_needed";
        }

        return !IsBuilderProofCaseSuccessful(sourceCase)
            ? "split_plus_stronger_tier_best"
            : "not_applicable";
    }

    private static string BuildSplitThenEscalateEvidenceSummary(
        BuilderProofCaseResult sourceCase,
        BuilderProofCaseResult strongerCase,
        BuilderProofCaseResult? splitFloorCase,
        string evidenceState)
        => evidenceState switch
        {
            "splitting_makes_low_floor_viable" when splitFloorCase is not null =>
                $"Splitting the scope made the floor model viable: {splitFloorCase.TargetLabel} finished as {splitFloorCase.FinalClassification}, while the original {sourceCase.TargetLabel} stayed {sourceCase.FinalClassification}.",
            "escalation_without_split_needed" =>
                $"Splitting did not make the floor model viable, but the stronger tier changed the same bounded task from {sourceCase.FinalClassification} to {strongerCase.FinalClassification}.",
            "split_plus_stronger_tier_best" =>
                $"The split floor attempt stayed unresolved and the stronger tier still carried the bounded task farther than the original floor attempt.",
            _ => "No split-then-escalate evidence was required for the latest comparative proof."
        };

    private static string BuildComparativeRepairBurdenSummary(
        BuilderProofCaseResult sourceCase,
        BuilderProofCaseResult strongerCase,
        BuilderProofCaseResult? splitFloorCase)
    {
        var summary = new List<string>
        {
            $"Low-floor burden={ClassifyRecoveryBurden(sourceCase)} ({sourceCase.FinalClassification}).",
            $"Stronger-tier burden={ClassifyRecoveryBurden(strongerCase)} ({strongerCase.FinalClassification})."
        };

        if (splitFloorCase is not null)
        {
            summary.Add($"Split low-floor burden={ClassifyRecoveryBurden(splitFloorCase)} ({splitFloorCase.FinalClassification}).");
        }

        return string.Join(" ", summary);
    }

    private static string BuildComparativeProofSummary(
        BuilderProofCaseResult sourceCase,
        BuilderProofCaseResult strongerCase,
        BuilderProofCaseResult? splitFloorCase,
        string comparativeClassification,
        string splitThenEscalateSummary,
        string repairBurdenDifferenceSummary)
    {
        var pieces = new List<string>
        {
            $"Low-floor {sourceCase.TargetLabel} ended as {sourceCase.FinalClassification}.",
            $"Stronger-tier comparison ended as {strongerCase.FinalClassification}.",
            repairBurdenDifferenceSummary,
            splitThenEscalateSummary
        };

        pieces.Add(comparativeClassification switch
        {
            "cleaner_success" => "Escalation buys a cleaner success for the original bounded scope.",
            "reduced_repair_burden" => "Escalation reduces the repair burden for the bounded scope.",
            "required_for_scope" => "Escalation is required to keep the original bounded scope viable.",
            "still_not_sufficient" => "The stronger-tier comparison still did not complete the bounded scope.",
            _ => "The stronger-tier comparison did not show a material gain for the bounded scope."
        });

        return string.Join(" ", pieces.Where(piece => !string.IsNullOrWhiteSpace(piece)));
    }

    private static string BuildComparativeProofSummaryMarkdown(BuilderComparativeProofRun run)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Comparative Proof Summary");
        builder.AppendLine();
        builder.AppendLine($"- Source proof run: `{run.SourceProofRunId}`");
        builder.AppendLine($"- Task class: `{run.TaskClass}`");
        builder.AppendLine($"- Target: `{run.TargetLabel}`");
        builder.AppendLine($"- Current model: `{run.CurrentModelId}`");
        builder.AppendLine($"- Stronger-tier model: `{run.StrongerTierModelId}`");
        builder.AppendLine($"- Comparative classification: `{run.ComparativeClassification}`");
        builder.AppendLine($"- Split evidence: `{run.SplitThenEscalateEvidenceState}`");
        builder.AppendLine($"- Repair burden: {run.RepairBurdenDifferenceSummary}");
        builder.AppendLine();
        builder.AppendLine("## Outcomes");
        builder.AppendLine($"- Low-floor source: {run.LowFloorCase.FinalClassification} ({run.LowFloorCase.FinalSummary})");
        builder.AppendLine($"- Stronger-tier: {run.StrongerTierCase.FinalClassification} ({run.StrongerTierCase.FinalSummary})");
        if (run.SplitLowFloorCase is not null)
        {
            builder.AppendLine($"- Split low-floor: {run.SplitLowFloorCase.FinalClassification} ({run.SplitLowFloorCase.FinalSummary})");
        }

        if (run.WeakSpotOutcomes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Weak-Spot Comparison");
            foreach (var weakSpot in run.WeakSpotOutcomes.OrderBy(value => value.WeakSpot, StringComparer.Ordinal))
            {
                builder.AppendLine($"- {weakSpot.WeakSpot}: {weakSpot.Summary}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- {run.Summary}");
        return builder.ToString().TrimEnd();
    }

    private static BuilderRoutingPolicyEvidence BuildBuilderRoutingPolicyEvidence(
        string runFolder,
        BuilderModelRoutingPlan routingPlan,
        BuilderModelEscalationDecision escalationDecision,
        BuilderStrongerTierAvailability strongerTierAvailability,
        BuilderComparativeProofRun comparativeRun)
    {
        var routingPolicyState = DetermineRoutingPolicyState(escalationDecision, strongerTierAvailability, comparativeRun);
        var reasons = new List<string>
        {
            strongerTierAvailability.Summary,
            comparativeRun.Summary,
            $"Comparative classification: {comparativeRun.ComparativeClassification}.",
            $"Split evidence: {comparativeRun.SplitThenEscalateEvidenceState}.",
            $"Escalation state: {escalationDecision.EscalationRequirementState}."
        };
        reasons.AddRange(routingPlan.Reasons);
        reasons.AddRange(comparativeRun.WeakSpotOutcomes.Select(outcome => outcome.Summary));
        var linkedArtifactPaths = new[]
        {
            BuilderStrongerTierAvailabilityPath(runFolder),
            BuilderComparativeProofRunPath(runFolder),
            BuilderComparativeProofSummaryPath(runFolder),
            BuilderModelRoutingRecommendationPath(runFolder),
            BuilderModelEscalationDecisionPath(runFolder),
            BuilderModelRoutingPlanPath(runFolder)
        }.Concat(comparativeRun.WeakSpotOutcomes.Select(_ => BuilderModelFloorFailurePatternsPath(runFolder)))
         .Where(path => !string.IsNullOrWhiteSpace(path))
         .Distinct(StringComparer.Ordinal)
         .ToArray();
        var summary = BuildRoutingPolicyEvidenceSummary(routingPolicyState, comparativeRun);

        return new BuilderRoutingPolicyEvidence(
            comparativeRun.SourceProofRunId,
            comparativeRun.ProofScope,
            comparativeRun.TargetId,
            comparativeRun.TargetLabel,
            comparativeRun.TaskClass,
            comparativeRun.CurrentModelId,
            comparativeRun.StrongerTierModelId,
            comparativeRun.RecommendedModelClass,
            strongerTierAvailability.AvailabilityState,
            comparativeRun.ComparativeClassification,
            comparativeRun.SplitThenEscalateEvidenceState,
            routingPolicyState,
            reasons
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            comparativeRun.WeakSpotOutcomes,
            linkedArtifactPaths,
            summary,
            BuilderRoutingPolicyEvidencePath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderSplitFirstPlan BuildBuilderSplitFirstPlan(
        string runFolder,
        BuilderModelRoutingPlan routingPlan,
        BuilderModelEscalationDecision escalationDecision,
        BuilderComparativeProofRun comparativeRun,
        BuilderRoutingPolicyEvidence routingPolicyEvidence)
    {
        var splitRecommendationState = DetermineSplitFirstRecommendationState(routingPlan, comparativeRun, routingPolicyEvidence);
        var steps = BuildBuilderSplitFirstPlanSteps(runFolder, routingPlan, comparativeRun, splitRecommendationState);
        var linkedArtifactPaths = routingPolicyEvidence.LinkedArtifactPaths
            .Concat(new[]
            {
                BuilderComparativeProofRunPath(runFolder),
                BuilderComparativeProofSummaryPath(runFolder),
                BuilderRoutingPolicyEvidencePath(runFolder)
            })
            .Concat(steps.SelectMany(step => step.LinkedArtifactPaths))
            .Concat(steps.Select(step => step.ExecutionHook.FutureExecutionArtifactPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var reasons = routingPolicyEvidence.Reasons
            .Concat(routingPlan.SplitTaskGuidance)
            .Concat(steps.Select(step => step.WeakSpotMitigation))
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = BuildBuilderSplitFirstPlanSummary(comparativeRun, splitRecommendationState, steps);

        return new BuilderSplitFirstPlan(
            comparativeRun.SourceProofRunId,
            comparativeRun.ProofScope,
            comparativeRun.TargetId,
            comparativeRun.TargetLabel,
            comparativeRun.TaskClass,
            comparativeRun.CurrentModelId,
            comparativeRun.StrongerTierModelId,
            routingPlan.ComparativeProofHook.ComparisonKey,
            comparativeRun.ComparativeClassification,
            comparativeRun.Summary,
            splitRecommendationState,
            routingPlan.PrimaryWeakSpot,
            routingPlan.PrimaryWeakSpotSummary,
            steps,
            reasons,
            linkedArtifactPaths,
            summary,
            BuilderSplitFirstPlanPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderTieredRoutingPolicy BuildBuilderTieredRoutingPolicy(
        string runFolder,
        BuilderModelRoutingPlan routingPlan,
        BuilderModelEscalationDecision escalationDecision,
        BuilderComparativeProofRun comparativeRun,
        BuilderRoutingPolicyEvidence routingPolicyEvidence,
        BuilderSplitFirstPlan splitFirstPlan)
    {
        var lowFloorRecommendationState = DetermineLowFloorRecommendationState(routingPlan, comparativeRun, routingPolicyEvidence);
        var splitFirstRecommendationState = DetermineTieredSplitRecommendationState(routingPolicyEvidence, splitFirstPlan);
        var strongerTierRecommendationState = DetermineStrongerTierRecommendationState(routingPlan, escalationDecision, comparativeRun, routingPolicyEvidence);
        var strongerTierRoleSummary = BuildStrongerTierRoleSummary(strongerTierRecommendationState, comparativeRun);
        var primaryRecommendationSummary = BuildPrimaryRoutingRecommendationSummary(routingPolicyEvidence.RoutingPolicyState, comparativeRun.TargetLabel);
        var weakSpotMitigationSummary = BuildBuilderWeakSpotMitigationSummary(splitFirstPlan, routingPolicyEvidence);
        var linkedArtifactPaths = routingPolicyEvidence.LinkedArtifactPaths
            .Concat(splitFirstPlan.LinkedArtifactPaths)
            .Concat(new[]
            {
                BuilderSplitFirstPlanPath(runFolder),
                BuilderTieredRoutingPolicyPath(runFolder)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var reasons = routingPolicyEvidence.Reasons
            .Concat(splitFirstPlan.Reasons)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = BuildBuilderTieredRoutingPolicySummary(
            routingPolicyEvidence.RoutingPolicyState,
            comparativeRun.TargetLabel,
            strongerTierRoleSummary,
            weakSpotMitigationSummary);

        return new BuilderTieredRoutingPolicy(
            comparativeRun.SourceProofRunId,
            comparativeRun.ProofScope,
            comparativeRun.TargetId,
            comparativeRun.TargetLabel,
            comparativeRun.TaskClass,
            comparativeRun.CurrentModelId,
            comparativeRun.StrongerTierModelId,
            routingPlan.ComparativeProofHook.ComparisonKey,
            lowFloorRecommendationState,
            splitFirstRecommendationState,
            strongerTierRecommendationState,
            routingPolicyEvidence.RoutingPolicyState,
            primaryRecommendationSummary,
            strongerTierRoleSummary,
            routingPlan.PrimaryWeakSpot,
            routingPlan.PrimaryWeakSpotSummary,
            weakSpotMitigationSummary,
            reasons,
            routingPolicyEvidence.WeakSpotOutcomes,
            linkedArtifactPaths,
            summary,
            BuilderTieredRoutingPolicyPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static string DetermineSplitFirstRecommendationState(
        BuilderModelRoutingPlan routingPlan,
        BuilderComparativeProofRun comparativeRun,
        BuilderRoutingPolicyEvidence routingPolicyEvidence)
    {
        if (string.Equals(routingPolicyEvidence.RoutingPolicyState, "split_first_keep_low_floor", StringComparison.Ordinal))
        {
            return "split_first_keep_low_floor";
        }

        return comparativeRun.SplitThenEscalateEvidenceState switch
        {
            "splitting_makes_low_floor_viable" => "split_first_keep_low_floor",
            "split_plus_stronger_tier_best" => "split_then_escalate",
            _ => routingPlan.SplitTaskRecommendationState
        };
    }

    private static IReadOnlyList<BuilderSplitFirstPlanStep> BuildBuilderSplitFirstPlanSteps(
        string runFolder,
        BuilderModelRoutingPlan routingPlan,
        BuilderComparativeProofRun comparativeRun,
        string splitRecommendationState)
    {
        var steps = new List<BuilderSplitFirstPlanStep>();
        var nextStepNumber = 1;
        var evidencePaths = new[]
        {
            BuilderComparativeProofRunPath(runFolder),
            BuilderComparativeProofSummaryPath(runFolder),
            BuilderModelFloorFailurePatternsPath(runFolder),
            BuilderModelRoutingPlanPath(runFolder),
            BuilderRoutingPolicyEvidencePath(runFolder)
        };

        if (string.Equals(routingPlan.PrimaryWeakSpot, "file_placement_mistake", StringComparison.Ordinal))
        {
            steps.Add(CreateBuilderSplitFirstPlanStep(
                runFolder,
                comparativeRun,
                nextStepNumber++,
                "separate_file_placement",
                "Separate file placement changes from implementation changes",
                "separate file placement changes from behavior edits",
                "Move or create Formatting/DisplayNameFormatter.cs only. Do not update callers in the same step.",
                "low_floor_repair_loop_expected",
                "Reduces file_placement_mistake by isolating the file move before caller edits.",
                evidencePaths));
        }

        if (comparativeRun.ComplexityDimensions.DependencyReferenceChangeCount > 0)
        {
            steps.Add(CreateBuilderSplitFirstPlanStep(
                runFolder,
                comparativeRun,
                nextStepNumber++,
                "separate_project_wiring",
                "Separate project wiring changes from feature logic",
                "separate project wiring changes from feature logic",
                "Adjust project references or wiring in their own bounded step before feature logic changes.",
                "stronger_tier_recommended",
                "Keeps project wiring isolated so feature edits do not hide the same wiring weakness.",
                evidencePaths));
        }

        if (string.Equals(routingPlan.PrimaryWeakSpot, "partial_implementation_gap", StringComparison.Ordinal))
        {
            steps.Add(CreateBuilderSplitFirstPlanStep(
                runFolder,
                comparativeRun,
                nextStepNumber++,
                "separate_compile_fix",
                "Separate compile-fix work from test-fix work",
                "separate compile-fix work from test-fix work",
                "Complete the missing implementation only. Do not change tests or project wiring in the same step.",
                "low_floor_repair_loop_expected",
                "Reduces partial_implementation_gap by completing the implementation before any later test or wiring changes.",
                evidencePaths));
        }

        steps.Add(CreateBuilderSplitFirstPlanStep(
            runFolder,
            comparativeRun,
            nextStepNumber++,
            "isolate_behavior_edits",
            "Separate new-file generation from behavior edits",
            "separate new-file generation from behavior edits",
            BuildBehaviorStepScopeSummary(comparativeRun, routingPlan),
            string.Equals(splitRecommendationState, "split_first_keep_low_floor", StringComparison.Ordinal)
                ? "low_floor_safe"
                : "low_floor_repair_loop_expected",
            BuildBehaviorStepWeakSpotMitigation(routingPlan),
            evidencePaths));

        var rerunStepType = string.Equals(comparativeRun.LowFloorCase.TestResult, "not_applicable", StringComparison.Ordinal)
            ? "rerun_bounded_build"
            : "rerun_bounded_tests";
        var rerunScopeSummary = string.Equals(rerunStepType, "rerun_bounded_build", StringComparison.Ordinal)
            ? "Rerun the bounded build for the split scope only."
            : "Rerun the bounded build first, then rerun the split target test scope only if the build passes.";
        steps.Add(CreateBuilderSplitFirstPlanStep(
            runFolder,
            comparativeRun,
            nextStepNumber,
            rerunStepType,
            string.Equals(rerunStepType, "rerun_bounded_build", StringComparison.Ordinal)
                ? "Rerun the bounded build scope"
                : "Rerun the bounded build and test scope",
            string.Equals(rerunStepType, "rerun_bounded_build", StringComparison.Ordinal)
                ? "rerun build scope only"
                : "rerun build scope first, then rerun the bounded test scope",
            rerunScopeSummary,
            "low_floor_safe",
            "Confirms whether the split-first change set closes the current weak spot before another wider attempt.",
            evidencePaths));

        return steps;
    }

    private static BuilderSplitFirstPlanStep CreateBuilderSplitFirstPlanStep(
        string runFolder,
        BuilderComparativeProofRun comparativeRun,
        int stepNumber,
        string stepIdSuffix,
        string stepLabel,
        string splitStrategy,
        string scopeSummary,
        string scopeClassification,
        string weakSpotMitigation,
        IReadOnlyList<string> linkedArtifactPaths)
    {
        var stepId = $"{comparativeRun.TaskClass}-{stepNumber:00}-{stepIdSuffix}";
        var executionHookPath = Path.Combine(runFolder, "split-execution-hooks", $"{SanitizeBuilderProofToken(stepId)}.json");
        var executionHook = new BuilderSplitExecutionHook(
            comparativeRun.SourceProofRunId,
            comparativeRun.TargetId,
            comparativeRun.TaskClass,
            comparativeRun.ProofScope,
            comparativeRun.CurrentModelId,
            comparativeRun.StrongerTierModelId,
            $"{comparativeRun.ProofScope}|{comparativeRun.TaskClass}|{stepIdSuffix}",
            executionHookPath,
            $"Prepared future split-step hook for {stepLabel.ToLowerInvariant()}.");

        return new BuilderSplitFirstPlanStep(
            stepNumber,
            stepId,
            stepLabel,
            splitStrategy,
            scopeSummary,
            scopeClassification,
            weakSpotMitigation,
            linkedArtifactPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            executionHook);
    }

    private static string BuildBehaviorStepScopeSummary(BuilderComparativeProofRun comparativeRun, BuilderModelRoutingPlan routingPlan)
    {
        if (string.Equals(comparativeRun.TaskClass, "bounded_refactor", StringComparison.Ordinal))
        {
            return "Update ProfileSummary.cs so it calls the existing Formatting/DisplayNameFormatter.cs implementation. Do not move files or widen scope.";
        }

        if (string.Equals(routingPlan.PrimaryWeakSpot, "partial_implementation_gap", StringComparison.Ordinal))
        {
            return "Apply the bounded implementation edit only. Do not widen scope to test or project wiring changes in the same step.";
        }

        return "Apply one bounded behavior edit after the setup step succeeds. Keep the touched file count to one or two files.";
    }

    private static string BuildBehaviorStepWeakSpotMitigation(BuilderModelRoutingPlan routingPlan)
    {
        if (string.Equals(routingPlan.PrimaryWeakSpot, "file_placement_mistake", StringComparison.Ordinal))
        {
            return "Keeps caller updates isolated after the file-placement step so file_placement_mistake does not recur in the same edit.";
        }

        if (string.Equals(routingPlan.PrimaryWeakSpot, "partial_implementation_gap", StringComparison.Ordinal))
        {
            return "Keeps the implementation completion isolated from later tests so partial_implementation_gap stays bounded.";
        }

        return "Keeps the next low-floor edit inside the bounded split step before another wider attempt.";
    }

    private static string BuildBuilderSplitFirstPlanSummary(
        BuilderComparativeProofRun comparativeRun,
        string splitRecommendationState,
        IReadOnlyList<BuilderSplitFirstPlanStep> steps)
    {
        var stepSummary = steps.Count == 0
            ? "No bounded split steps were required."
            : $"Use {steps.Count} bounded step(s): {string.Join("; ", steps.OrderBy(step => step.StepNumber).Select(step => $"{step.StepNumber}. {step.StepLabel}"))}.";
        var recommendation = splitRecommendationState switch
        {
            "split_first_keep_low_floor" => "Split first and keep the low-floor model on the reduced scope.",
            "split_then_escalate" => "Split the task first, then escalate only if the reduced scope still fails.",
            _ => "No split-first route is recorded for the latest comparative proof."
        };

        return $"{recommendation} {stepSummary} Stronger-tier comparison result: {comparativeRun.ComparativeClassification}.";
    }

    private static void WriteBuilderSplitExecutionHooks(string runFolder, BuilderSplitFirstPlan splitFirstPlan)
    {
        foreach (var step in splitFirstPlan.Steps)
        {
            var hookPath = step.ExecutionHook.FutureExecutionArtifactPath;
            Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
            var payload = new
            {
                splitFirstPlan.SourceProofRunId,
                splitFirstPlan.TargetId,
                splitFirstPlan.TaskClass,
                splitFirstPlan.ComparisonKey,
                StepId = step.StepId,
                step.StepLabel,
                step.SplitStrategy,
                step.ScopeSummary,
                step.ScopeClassification,
                step.WeakSpotMitigation,
                FollowupRoutingState = splitFirstPlan.SplitRecommendationState,
                ComparativeProofArtifactPath = BuilderComparativeProofRunPath(runFolder),
                ComparativeProofSummaryPath = BuilderComparativeProofSummaryPath(runFolder),
                SplitPlanArtifactPath = splitFirstPlan.ArtifactPath,
                step.ExecutionHook.Summary,
                ObservedUtc = DateTimeOffset.UtcNow
            };
            File.WriteAllText(hookPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static BuilderSplitStepExecution BuildBuilderSplitStepExecution(
        string repoRoot,
        string runFolder,
        BuilderComparativeProofRun comparativeRun,
        BuilderSplitFirstPlan splitFirstPlan)
    {
        var sourceFollowupPlanPath = comparativeRun.LowFloorCase.FollowupPlanPath;
        var sourceRepairPrepBundlePath = comparativeRun.LowFloorCase.RepairPrepBundlePath;
        var sourceFollowupExecutionOutcomePath = comparativeRun.LowFloorCase.FollowupExecutionOutcomePath;
        var steps = splitFirstPlan.Steps
            .OrderBy(step => step.StepNumber)
            .Select(step =>
            {
                var stepType = DetermineBuilderSplitStepType(step);
                var executionMode = DetermineBuilderSplitStepExecutionMode(stepType);
                var evidencePath = ResolveBuilderSplitStepEvidencePath(step, sourceFollowupPlanPath, sourceRepairPrepBundlePath, comparativeRun.ArtifactPath);
                var linkedArtifactPaths = step.LinkedArtifactPaths
                    .Concat(new[]
                    {
                        step.ExecutionHook.FutureExecutionArtifactPath,
                        sourceFollowupPlanPath,
                        sourceRepairPrepBundlePath,
                        sourceFollowupExecutionOutcomePath,
                        comparativeRun.ArtifactPath,
                        comparativeRun.SummaryArtifactPath,
                        splitFirstPlan.ArtifactPath
                    })
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return new BuilderSplitStepExecutionStepState(
                    step.StepNumber,
                    step.StepId,
                    step.StepLabel,
                    stepType,
                    executionMode,
                    step.ScopeClassification,
                    "blocked",
                    string.Empty,
                    "not_started",
                    BuildBuilderSplitStepDetail(step, stepType),
                    evidencePath,
                    linkedArtifactPaths,
                    sourceFollowupPlanPath,
                    sourceRepairPrepBundlePath,
                    string.Empty,
                    DateTimeOffset.UtcNow);
            })
            .ToArray();

        var execution = new BuilderSplitStepExecution(
            comparativeRun.SourceProofRunId,
            runFolder,
            comparativeRun.ComplexityDimensions.PromptAmbiguity,
            comparativeRun.ProofScope,
            comparativeRun.TargetId,
            comparativeRun.TargetLabel,
            comparativeRun.TaskClass,
            comparativeRun.CurrentModelId,
            comparativeRun.StrongerTierModelId,
            splitFirstPlan.ArtifactPath,
            comparativeRun.ArtifactPath,
            sourceFollowupPlanPath,
            sourceRepairPrepBundlePath,
            "current",
            steps,
            string.Empty,
            BuilderSplitStepExecutionPath(runFolder),
            DateTimeOffset.UtcNow);
        return RefreshBuilderSplitStepExecution(repoRoot, execution, splitFirstPlan, comparativeRun, null);
    }

    private static BuilderSplitStepExecution RefreshBuilderSplitStepExecution(
        string repoRoot,
        BuilderSplitStepExecution execution,
        BuilderSplitFirstPlan splitFirstPlan,
        BuilderComparativeProofRun comparativeRun,
        BuilderSplitFirstOutcome? outcome)
    {
        var freshnessState = DetermineBuilderSplitArtifactFreshness(repoRoot, execution.SourceProofRunId);
        var priorStepsCompleted = true;
        var blockingStepNumber = 0;
        var updatedSteps = new List<BuilderSplitStepExecutionStepState>(execution.Steps.Count);

        foreach (var step in execution.Steps.OrderBy(item => item.StepNumber))
        {
            var isCompleted = IsBuilderSplitStepCompleted(step.ExecutionState);
            var blockReason = string.Empty;
            var eligibilityState = "eligible";

            if (string.Equals(freshnessState, "superseded", StringComparison.Ordinal))
            {
                eligibilityState = isCompleted ? "completed" : "blocked";
                blockReason = isCompleted ? string.Empty : "This split-first execution was superseded by newer builder proof evidence.";
            }
            else if (isCompleted)
            {
                eligibilityState = "completed";
            }
            else if (string.Equals(step.ExecutionMode, "manual_only", StringComparison.Ordinal))
            {
                eligibilityState = "manual_only";
                blockReason = "Manual review step.";
            }
            else if (!priorStepsCompleted)
            {
                eligibilityState = "blocked";
                blockReason = $"Step {blockingStepNumber} must finish before this split step can run.";
            }
            else
            {
                blockReason = GetBuilderSplitStepArtifactBlockReason(step, splitFirstPlan, comparativeRun);
                eligibilityState = string.IsNullOrWhiteSpace(blockReason) ? "eligible" : "blocked";
            }

            updatedSteps.Add(step with
            {
                EligibilityState = eligibilityState,
                BlockReason = blockReason
            });

            if (!isCompleted)
            {
                priorStepsCompleted = false;
                if (blockingStepNumber == 0)
                {
                    blockingStepNumber = step.StepNumber;
                }
            }
        }

        return execution with
        {
            FreshnessState = freshnessState,
            Steps = updatedSteps,
            Summary = BuildBuilderSplitStepExecutionSummary(updatedSteps, freshnessState, outcome),
            RecordedUtc = DateTimeOffset.UtcNow
        };
    }

    private static string BuildBuilderSplitStepExecutionSummary(
        IReadOnlyList<BuilderSplitStepExecutionStepState> steps,
        string freshnessState,
        BuilderSplitFirstOutcome? outcome)
    {
        if (steps.Count == 0)
        {
            return "No split-step execution is recorded.";
        }

        var completedCount = steps.Count(step => IsBuilderSplitStepCompleted(step.ExecutionState));
        if (string.Equals(freshnessState, "superseded", StringComparison.Ordinal))
        {
            return $"Split-step execution was superseded by newer builder proof evidence. Completed steps: {completedCount}/{steps.Count}.";
        }

        if (outcome is not null)
        {
            return $"Split-step execution recorded {outcome.ClosureClassification.Replace('_', ' ')}. Completed steps: {completedCount}/{steps.Count}.";
        }

        var nextEligible = steps
            .Where(step => string.Equals(step.EligibilityState, "eligible", StringComparison.Ordinal) &&
                           string.Equals(step.ExecutionState, "not_started", StringComparison.Ordinal))
            .OrderBy(step => step.StepNumber)
            .FirstOrDefault();
        if (nextEligible is not null)
        {
            return $"Next split step: {nextEligible.StepNumber}. {nextEligible.StepLabel}. Completed steps: {completedCount}/{steps.Count}.";
        }

        return $"Split-step execution is waiting on recorded step completion. Completed steps: {completedCount}/{steps.Count}.";
    }

    private static string DetermineBuilderSplitStepType(BuilderSplitFirstPlanStep step)
    {
        if (step.StepId.Contains("rerun_bounded_build", StringComparison.Ordinal))
            return "rerun_bounded_build_scope";
        if (step.StepId.Contains("rerun_bounded_tests", StringComparison.Ordinal))
            return "rerun_bounded_test_scope";
        if (step.StepId.Contains("isolate_behavior_edits", StringComparison.Ordinal))
            return "prepare_repair_bundle";
        if (step.StepId.Contains("separate_project_wiring", StringComparison.Ordinal) ||
            step.StepId.Contains("separate_compile_fix", StringComparison.Ordinal))
            return "launch_guided_followup";
        if (step.StepId.Contains("separate_file_placement", StringComparison.Ordinal) || step.StepNumber == 1)
            return "inspect_linked_file_scope";

        return "inspect_artifact";
    }

    private static string DetermineBuilderSplitStepExecutionMode(string stepType)
        => stepType switch
        {
            "rerun_bounded_build_scope" => "rerun_capable",
            "rerun_bounded_test_scope" => "rerun_capable",
            "inspect_linked_file_scope" => "view_only",
            "inspect_artifact" => "view_only",
            "prepare_repair_bundle" => "executable",
            "launch_guided_followup" => "executable",
            _ => "manual_only"
        };

    private static string ResolveBuilderSplitStepEvidencePath(
        BuilderSplitFirstPlanStep step,
        string sourceFollowupPlanPath,
        string sourceRepairPrepBundlePath,
        string comparativeArtifactPath)
    {
        var stepType = DetermineBuilderSplitStepType(step);
        return stepType switch
        {
            "prepare_repair_bundle" when !string.IsNullOrWhiteSpace(sourceRepairPrepBundlePath) => sourceRepairPrepBundlePath,
            "launch_guided_followup" when !string.IsNullOrWhiteSpace(sourceFollowupPlanPath) => sourceFollowupPlanPath,
            "rerun_bounded_build_scope" => comparativeArtifactPath,
            "rerun_bounded_test_scope" => comparativeArtifactPath,
            _ => step.ExecutionHook.FutureExecutionArtifactPath
        };
    }

    private static string BuildBuilderSplitStepDetail(BuilderSplitFirstPlanStep step, string stepType)
        => stepType switch
        {
            "prepare_repair_bundle" => $"Open the bounded repair-prep bundle before {step.StepLabel.ToLowerInvariant()}.",
            "launch_guided_followup" => $"Use the linked guided follow-up context before {step.StepLabel.ToLowerInvariant()}.",
            "rerun_bounded_build_scope" => "Rerun the split build scope only after the earlier split steps are complete.",
            "rerun_bounded_test_scope" => "Rerun the split build scope first, then the bounded test scope if the build passes.",
            _ => step.ScopeSummary
        };

    private static string DetermineBuilderSplitArtifactFreshness(string repoRoot, string sourceProofRunId)
    {
        var latestRun = LoadLatestBuilderProofRun(repoRoot);
        if (latestRun is null || string.IsNullOrWhiteSpace(sourceProofRunId))
        {
            return "unknown";
        }

        return string.Equals(latestRun.ProofRunId, sourceProofRunId, StringComparison.Ordinal)
            ? "current"
            : "superseded";
    }

    private static bool IsBuilderSplitStepCompleted(string executionState)
        => string.Equals(executionState, "opened", StringComparison.Ordinal) ||
           string.Equals(executionState, "executed", StringComparison.Ordinal) ||
           string.Equals(executionState, "completed_by_outcome", StringComparison.Ordinal);

    private static string GetBuilderSplitStepArtifactBlockReason(
        BuilderSplitStepExecutionStepState step,
        BuilderSplitFirstPlan splitPlan,
        BuilderComparativeProofRun comparativeRun)
    {
        if (!string.Equals(step.ExecutionMode, "rerun_capable", StringComparison.Ordinal))
        {
            return BuilderArtifactPathExists(ResolveBuilderSplitStepPrimaryPath(step))
                ? string.Empty
                : "Linked artifact path is unavailable for this split step.";
        }

        var runFolder = Path.GetDirectoryName(splitPlan.ArtifactPath) ?? string.Empty;
        if (ResolveBuilderComparativeSourceTarget(runFolder, comparativeRun) is null)
        {
            return "Bounded split target scope is unavailable for rerun.";
        }

        return BuilderArtifactPathExists(comparativeRun.ArtifactPath)
            ? string.Empty
            : "Comparative proof artifact is unavailable for the split rerun.";
    }

    private static BuilderProofTargetDefinition? ResolveBuilderComparativeSourceTarget(string runFolder, BuilderComparativeProofRun comparativeRun)
    {
        var repoLocalMatrix = LoadBuilderProofMatrix(runFolder);
        var externalPack = LoadBuilderExternalProofRun(runFolder)?.TargetPack;

        if (string.Equals(comparativeRun.ProofScope, "external_target_pack", StringComparison.Ordinal))
        {
            return externalPack?.Targets.FirstOrDefault(target => string.Equals(target.TargetId, comparativeRun.TargetId, StringComparison.Ordinal));
        }

        return repoLocalMatrix?.Targets.FirstOrDefault(target => string.Equals(target.TargetId, comparativeRun.TargetId, StringComparison.Ordinal))
               ?? externalPack?.Targets.FirstOrDefault(target => string.Equals(target.TargetId, comparativeRun.TargetId, StringComparison.Ordinal));
    }

    private static BuilderProofTargetDefinition? ResolveBuilderPreparedLaunchTarget(string runFolder, BuilderRequestIntake intake)
    {
        var repoLocalMatrix = LoadBuilderProofMatrix(runFolder);
        var externalPack = LoadBuilderExternalProofRun(runFolder)?.TargetPack;
        if (string.Equals(intake.ProofScope, "external_target_pack", StringComparison.Ordinal))
        {
            return externalPack?.Targets.FirstOrDefault(target => string.Equals(target.TargetId, intake.TargetId, StringComparison.Ordinal));
        }

        return repoLocalMatrix?.Targets.FirstOrDefault(target => string.Equals(target.TargetId, intake.TargetId, StringComparison.Ordinal))
               ?? externalPack?.Targets.FirstOrDefault(target => string.Equals(target.TargetId, intake.TargetId, StringComparison.Ordinal));
    }

    private static BuilderSplitStepExecutionStepState? ResolveBuilderSplitRerunStep(BuilderSplitStepExecution execution, string? requestedStepId)
    {
        var steps = execution.Steps
            .Where(step => string.Equals(step.ExecutionMode, "rerun_capable", StringComparison.Ordinal))
            .OrderBy(step => step.StepNumber)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(requestedStepId))
        {
            return steps.FirstOrDefault(step => string.Equals(step.StepId, requestedStepId, StringComparison.Ordinal));
        }

        return steps.FirstOrDefault(step =>
            string.Equals(step.EligibilityState, "eligible", StringComparison.Ordinal) &&
            string.Equals(step.ExecutionState, "not_started", StringComparison.Ordinal));
    }

    private static string ResolveBuilderSplitStepPrimaryPath(BuilderSplitStepExecutionStepState step)
    {
        if (BuilderArtifactPathExists(step.EvidencePath))
            return step.EvidencePath;

        return step.LinkedArtifactPaths.FirstOrDefault(BuilderArtifactPathExists)
               ?? step.LinkedArtifactPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
               ?? string.Empty;
    }

    private static bool BuilderArtifactPathExists(string path)
        => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    private static string MergeBuilderSplitStepExecutionState(string currentState, string nextState)
    {
        if (string.Equals(currentState, "completed_by_outcome", StringComparison.Ordinal))
            return currentState;
        if (string.Equals(currentState, "executed", StringComparison.Ordinal) &&
            (string.Equals(nextState, "opened", StringComparison.Ordinal) || string.Equals(nextState, "not_started", StringComparison.Ordinal)))
            return currentState;
        if (string.Equals(currentState, "opened", StringComparison.Ordinal) &&
            string.Equals(nextState, "not_started", StringComparison.Ordinal))
            return currentState;

        return nextState;
    }

    private static IReadOnlyList<string> BuildBuilderSplitOutcomeArtifactPaths(BuilderProofCaseResult rerunResult)
        => new[]
        {
            rerunResult.TargetFolder,
            rerunResult.ValidationResultPath,
            rerunResult.FollowupPlanPath,
            rerunResult.RepairPrepBundlePath,
            rerunResult.FollowupExecutionOutcomePath,
            rerunResult.RecoveryValidationResultPath
        }.Concat(rerunResult.StageResults.Select(stage => stage.LogPath))
         .Where(path => !string.IsNullOrWhiteSpace(path))
         .Distinct(StringComparer.Ordinal)
         .ToArray();

    private static BuilderSplitFirstOutcome BuildBuilderSplitFirstOutcome(
        string runFolder,
        BuilderComparativeProofRun comparativeRun,
        BuilderSplitFirstPlan splitPlan,
        BuilderSplitStepExecution execution,
        BuilderSplitStepExecutionStepState rerunStep,
        BuilderProofCaseResult splitResult)
    {
        var splitBurden = ClassifyRecoveryBurden(splitResult);
        var unsplitBurden = ClassifyRecoveryBurden(comparativeRun.LowFloorCase);
        var strongerBurden = ClassifyRecoveryBurden(comparativeRun.StrongerTierCase);
        var closureClassification = DetermineBuilderSplitClosureClassification(splitResult, comparativeRun.LowFloorCase, comparativeRun.StrongerTierCase);
        var comparisonToUnsplit = BuildBuilderSplitComparisonSummary("unsplit low-floor", comparativeRun.LowFloorCase, splitResult);
        var comparisonToStrongerTier = BuildBuilderSplitComparisonSummary("stronger tier", comparativeRun.StrongerTierCase, splitResult);
        var practicalRouteSummary = BuildBuilderSplitPracticalRouteSummary(closureClassification, splitPlan.TargetLabel);
        var linkedArtifactPaths = BuildBuilderSplitOutcomeArtifactPaths(splitResult)
            .Concat(new[]
            {
                execution.ArtifactPath,
                splitPlan.ArtifactPath,
                comparativeRun.ArtifactPath,
                comparativeRun.SummaryArtifactPath,
                BuilderSplitFirstOutcomePath(runFolder)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{BuildBuilderSplitClosureSummary(closureClassification)} {comparisonToUnsplit} {comparisonToStrongerTier}";

        return new BuilderSplitFirstOutcome(
            comparativeRun.SourceProofRunId,
            runFolder,
            splitPlan.ComparisonKey,
            splitPlan.ArtifactPath,
            execution.ArtifactPath,
            comparativeRun.ArtifactPath,
            rerunStep.StepId,
            rerunStep.StepLabel,
            splitResult.FinalClassification,
            splitResult.FinalSummary,
            splitResult.BuildResult,
            splitResult.TestResult,
            splitResult.RecoveryRequired,
            splitBurden,
            splitResult.ValidationResultPath,
            splitResult.FollowupPlanPath,
            splitResult.RepairPrepBundlePath,
            splitResult.FollowupExecutionOutcomePath,
            comparativeRun.LowFloorCase.FinalClassification,
            unsplitBurden,
            comparativeRun.StrongerTierCase.FinalClassification,
            strongerBurden,
            comparisonToUnsplit,
            comparisonToStrongerTier,
            closureClassification,
            practicalRouteSummary,
            DetermineBuilderSplitArtifactFreshness(comparativeRun.RepoRoot, comparativeRun.SourceProofRunId),
            linkedArtifactPaths,
            summary,
            BuilderSplitFirstOutcomePath(runFolder),
            DateTimeOffset.UtcNow,
            splitResult.TargetFolder,
            splitResult.StarterStateManifestPath);
    }

    private static string DetermineBuilderSplitClosureClassification(
        BuilderProofCaseResult splitResult,
        BuilderProofCaseResult unsplitLowFloor,
        BuilderProofCaseResult strongerTier)
    {
        var splitSuccess = IsBuilderProofCaseSuccessful(splitResult);
        var unsplitSuccess = IsBuilderProofCaseSuccessful(unsplitLowFloor);
        var strongerSuccess = IsBuilderProofCaseSuccessful(strongerTier);
        var splitBurdenScore = GetBuilderProofCaseBurdenScore(splitResult);
        var unsplitBurdenScore = GetBuilderProofCaseBurdenScore(unsplitLowFloor);
        var strongerBurdenScore = GetBuilderProofCaseBurdenScore(strongerTier);

        if (!splitSuccess)
        {
            return ScoreBuilderProofCaseProgress(splitResult) > ScoreBuilderProofCaseProgress(unsplitLowFloor)
                ? "split_improved_but_not_closed"
                : "split_failed";
        }

        if (strongerSuccess &&
            splitBurdenScore == strongerBurdenScore &&
            string.Equals(splitResult.FinalClassification, strongerTier.FinalClassification, StringComparison.Ordinal))
        {
            return "split_equal_to_stronger_tier";
        }

        if (!unsplitSuccess && splitBurdenScore < unsplitBurdenScore && (!strongerSuccess || splitBurdenScore <= strongerBurdenScore))
        {
            return "split_closed_gap";
        }

        if (strongerSuccess && splitBurdenScore > strongerBurdenScore)
        {
            return splitBurdenScore == 1
                ? "split_viable_but_costlier"
                : "stronger_tier_still_preferred";
        }

        return "split_closed_gap";
    }

    private static int ScoreBuilderProofCaseProgress(BuilderProofCaseResult result)
    {
        var passedStages = result.StageResults.Count(stage => string.Equals(stage.Status, "passed", StringComparison.Ordinal));
        var score = passedStages * 10;
        if (string.Equals(result.BuildResult, "passed", StringComparison.Ordinal))
            score += 5;
        if (string.Equals(result.TestResult, "passed", StringComparison.Ordinal))
            score += 5;
        if (IsBuilderProofCaseSuccessful(result))
            score += 100;
        return score;
    }

    private static string BuildBuilderSplitComparisonSummary(string label, BuilderProofCaseResult baseline, BuilderProofCaseResult splitResult)
        => $"Compared with {label}, split-first moved {baseline.FinalClassification}/{ClassifyRecoveryBurden(baseline)} to {splitResult.FinalClassification}/{ClassifyRecoveryBurden(splitResult)}.";

    private static string BuildBuilderSplitPracticalRouteSummary(string closureClassification, string targetLabel)
        => closureClassification switch
        {
            "split_equal_to_stronger_tier" => $"Split-first closed the gap for {targetLabel} and matched the stronger-tier burden.",
            "split_closed_gap" => $"Split-first closed the gap for {targetLabel} without needing stronger-tier escalation.",
            "split_viable_but_costlier" => $"Split-first worked for {targetLabel}, but it cost more repair burden than the stronger tier.",
            "stronger_tier_still_preferred" => $"Split-first is still costlier for {targetLabel}; keep the stronger tier as the cleaner route.",
            "split_improved_but_not_closed" => $"Split-first improved {targetLabel}, but the issue stayed open.",
            _ => $"Split-first did not rescue the low-floor path for {targetLabel}."
        };

    private static string BuildBuilderSplitClosureSummary(string closureClassification)
        => closureClassification switch
        {
            "split_equal_to_stronger_tier" => "Split-first matched the stronger-tier outcome.",
            "split_closed_gap" => "Split-first closed the gap from the unsplit low-floor attempt.",
            "split_viable_but_costlier" => "Split-first worked, but the burden stayed higher than the stronger tier.",
            "stronger_tier_still_preferred" => "Stronger tier still looks cleaner after the split-first rerun.",
            "split_improved_but_not_closed" => "Split-first improved the bounded case but did not close it.",
            _ => "Split-first failed to recover the bounded case."
        };

    private static string DetermineLowFloorRecommendationState(
        BuilderModelRoutingPlan routingPlan,
        BuilderComparativeProofRun comparativeRun,
        BuilderRoutingPolicyEvidence routingPolicyEvidence)
    {
        if (string.Equals(routingPolicyEvidence.RoutingPolicyState, "split_first_keep_low_floor", StringComparison.Ordinal))
        {
            return "split_first_keep_low_floor";
        }

        return routingPlan.TrustBand switch
        {
            "clean_build_band" => "low_floor_as_is",
            "repair_loop_band" => "low_floor_with_repair_loop",
            _ when string.Equals(comparativeRun.SplitThenEscalateEvidenceState, "splitting_makes_low_floor_viable", StringComparison.Ordinal) => "split_first_keep_low_floor",
            _ => "low_floor_not_preferred"
        };
    }

    private static string DetermineTieredSplitRecommendationState(
        BuilderRoutingPolicyEvidence routingPolicyEvidence,
        BuilderSplitFirstPlan splitFirstPlan)
        => string.Equals(routingPolicyEvidence.RoutingPolicyState, "split_first_keep_low_floor", StringComparison.Ordinal)
            ? "primary_route"
            : string.Equals(splitFirstPlan.SplitRecommendationState, "split_then_escalate", StringComparison.Ordinal)
                ? "prepare_then_escalate"
                : "not_needed";

    private static string DetermineStrongerTierRecommendationState(
        BuilderModelRoutingPlan routingPlan,
        BuilderModelEscalationDecision escalationDecision,
        BuilderComparativeProofRun comparativeRun,
        BuilderRoutingPolicyEvidence routingPolicyEvidence)
    {
        if (string.Equals(routingPolicyEvidence.RoutingPolicyState, "split_first_keep_low_floor", StringComparison.Ordinal))
        {
            return "cleaner_not_required";
        }

        if (string.Equals(escalationDecision.EscalationRequirementState, "stronger_model_required", StringComparison.Ordinal) ||
            string.Equals(comparativeRun.ComparativeClassification, "required_for_scope", StringComparison.Ordinal))
        {
            return "required_for_scope";
        }

        if (string.Equals(routingPlan.RecommendedModelClass, "stronger_builder_tier", StringComparison.Ordinal))
        {
            return "recommended_for_cleaner_success";
        }

        return "not_needed";
    }

    private static string BuildStrongerTierRoleSummary(string strongerTierRecommendationState, BuilderComparativeProofRun comparativeRun)
        => strongerTierRecommendationState switch
        {
            "cleaner_not_required" => $"Stronger tier is cleaner, not required, after the split-first path reduces {comparativeRun.TargetLabel} to a viable low-floor scope.",
            "required_for_scope" => $"Stronger tier is required to keep the original {comparativeRun.TargetLabel} scope viable.",
            "recommended_for_cleaner_success" => $"Stronger tier is recommended when {comparativeRun.TargetLabel} must land cleanly with less repair burden.",
            _ => "No stronger-tier escalation role is recorded for the latest comparative proof."
        };

    private static string BuildPrimaryRoutingRecommendationSummary(string routingPolicyState, string targetLabel)
        => routingPolicyState switch
        {
            "split_first_keep_low_floor" => $"Primary route: split {targetLabel} first, then retry with the low-floor model.",
            "escalate_for_cleaner_success" => $"Primary route: escalate {targetLabel} to the stronger tier for a cleaner success.",
            "escalate_because_low_floor_out_of_scope" => $"Primary route: escalate {targetLabel} because the low-floor model is out of scope.",
            "comparative_evidence_inconclusive" => $"Primary route: keep {targetLabel} bounded and inspect the comparative evidence before another attempt.",
            _ => $"Primary route: stay on the current model for {targetLabel}."
        };

    private static string BuildBuilderWeakSpotMitigationSummary(
        BuilderSplitFirstPlan splitFirstPlan,
        BuilderRoutingPolicyEvidence routingPolicyEvidence)
    {
        var mitigations = splitFirstPlan.Steps
            .Select(step => step.WeakSpotMitigation)
            .Concat(routingPolicyEvidence.WeakSpotOutcomes.Select(outcome => outcome.Summary))
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return mitigations.Length == 0
            ? "No weak-spot mitigation is recorded for the latest comparative proof."
            : string.Join(" ", mitigations);
    }

    private static string BuildBuilderTieredRoutingPolicySummary(
        string routingPolicyState,
        string targetLabel,
        string strongerTierRoleSummary,
        string weakSpotMitigationSummary)
    {
        var opening = routingPolicyState switch
        {
            "split_first_keep_low_floor" => $"Low floor is enough if {targetLabel} is split first.",
            "escalate_for_cleaner_success" => $"Stronger tier is recommended for cleaner success on {targetLabel}.",
            "escalate_because_low_floor_out_of_scope" => $"Stronger tier is required because {targetLabel} stays outside the low-floor scope.",
            "comparative_evidence_inconclusive" => $"Comparative evidence is still inconclusive for {targetLabel}.",
            _ => $"Current evidence does not require a routing change for {targetLabel}."
        };

        return $"{opening} {strongerTierRoleSummary} {weakSpotMitigationSummary}".Trim();
    }

    private static string DetermineRoutingPolicyState(
        BuilderModelEscalationDecision escalationDecision,
        BuilderStrongerTierAvailability strongerTierAvailability,
        BuilderComparativeProofRun comparativeRun)
    {
        if (!string.Equals(strongerTierAvailability.AvailabilityState, "available", StringComparison.Ordinal))
        {
            return "comparative_evidence_inconclusive";
        }

        if (string.Equals(comparativeRun.SplitThenEscalateEvidenceState, "splitting_makes_low_floor_viable", StringComparison.Ordinal) &&
            string.Equals(escalationDecision.EscalationRequirementState, "task_should_be_split_first", StringComparison.Ordinal))
        {
            return "split_first_keep_low_floor";
        }

        return comparativeRun.ComparativeClassification switch
        {
            "no_material_gain" => "stay_on_current_model",
            "reduced_repair_burden" => "escalate_for_cleaner_success",
            "cleaner_success" => "escalate_for_cleaner_success",
            "required_for_scope" => "escalate_because_low_floor_out_of_scope",
            "still_not_sufficient" => "comparative_evidence_inconclusive",
            _ => "stay_on_current_model"
        };
    }

    private static string BuildRoutingPolicyEvidenceSummary(string routingPolicyState, BuilderComparativeProofRun comparativeRun)
        => routingPolicyState switch
        {
            "split_first_keep_low_floor" => $"Split first, keep low-floor. Reduce {comparativeRun.TargetLabel} into the smaller bounded step before another low-floor attempt.",
            "escalate_for_cleaner_success" => $"Escalate for cleaner success. Use the stronger tier when {comparativeRun.TargetLabel} needs the original bounded scope with less repair burden.",
            "escalate_because_low_floor_out_of_scope" => $"Escalate because the low-floor model is out of scope. The recorded low-floor evidence still leaves {comparativeRun.TargetLabel} outside the current floor band.",
            "comparative_evidence_inconclusive" => $"Comparative evidence is inconclusive. Keep {comparativeRun.TargetLabel} split and bounded until stronger proof improves.",
            _ => $"Stay on the current model. The comparative proof did not show a material gain for {comparativeRun.TargetLabel}."
        };

    private static IReadOnlyList<BuilderWeakSpotComparativeOutcome> BuildBuilderWeakSpotComparativeOutcomes(
        BuilderProofCaseResult sourceCase,
        BuilderProofCaseResult strongerCase,
        BuilderModelEscalationDecision escalationDecision)
    {
        var lowWeakSpots = InferWeakSpots(sourceCase);
        var strongerWeakSpots = InferWeakSpots(strongerCase);
        var union = lowWeakSpots
            .Concat(strongerWeakSpots)
            .Concat(string.IsNullOrWhiteSpace(escalationDecision.PrimaryWeakSpot) ? Array.Empty<string>() : new[] { escalationDecision.PrimaryWeakSpot })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return union
            .Select(weakSpot =>
            {
                var lowObserved = lowWeakSpots.Contains(weakSpot, StringComparer.Ordinal);
                var strongerObserved = strongerWeakSpots.Contains(weakSpot, StringComparer.Ordinal);
                var strongerState = strongerObserved
                    ? "still_present"
                    : lowObserved
                        ? "resolved"
                        : "not_observed";
                var summary = strongerState switch
                {
                    "resolved" => $"{weakSpot} was observed on the low-floor path and was not reproduced on the stronger-tier proof.",
                    "still_present" => $"{weakSpot} still appeared on the stronger-tier proof path.",
                    _ => $"{weakSpot} was not reproduced during the stronger-tier proof."
                };
                return new BuilderWeakSpotComparativeOutcome(
                    weakSpot,
                    lowObserved ? "observed" : "not_observed",
                    strongerState,
                    summary);
            })
            .ToArray();
    }

    private static IReadOnlyList<string> InferWeakSpots(BuilderProofCaseResult result)
    {
        var failedStage = result.StageResults.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal));
        if (failedStage is null)
        {
            return Array.Empty<string>();
        }

        var errorExcerpt = ExtractFailureExcerpt(failedStage.LogPath, failedStage.Summary);
        return new[]
        {
            ClassifyFailurePattern(result, failedStage.Summary, errorExcerpt)
        };
    }

    private static bool IsBuilderProofCaseSuccessful(BuilderProofCaseResult result)
        => string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal) ||
           string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal);

    private static int GetBuilderProofCaseBurdenScore(BuilderProofCaseResult result)
        => ClassifyRecoveryBurden(result) switch
        {
            "clean" => 0,
            "acceptable_with_repair_loop" => 1,
            _ => 2
        };

    private static string BuildBuilderStrongerTierAvailabilitySummary(BuilderStrongerTierAvailability availability)
        => availability.AvailabilityState switch
        {
            "available" => $"Stronger-tier model {availability.ConfiguredStrongerTierId} is available for bounded comparative proof.",
            "not_needed" => "No stronger-tier resolution was required for the latest routing state.",
            "unconfigured" => availability.Reason,
            _ => availability.Reason
        };

    private static void WriteBuilderComparativeProofHook(
        string runFolder,
        BuilderRoutingPolicyEvidence routingPolicyEvidence,
        BuilderComparativeProofRun comparativeRun,
        BuilderSplitFirstPlan splitFirstPlan,
        BuilderTieredRoutingPolicy tieredRoutingPolicy)
    {
        var hookFolder = Path.Combine(runFolder, "comparison-hooks");
        Directory.CreateDirectory(hookFolder);
        var hookPath = Path.Combine(hookFolder, $"{SanitizeBuilderProofToken($"{comparativeRun.ProofScope}|{comparativeRun.TaskClass}")}.json");
        var payload = new
        {
            comparativeRun.SourceProofRunId,
            comparativeRun.TaskClass,
            comparativeRun.ProofScope,
            comparativeRun.TargetId,
            comparativeRun.CurrentModelId,
            comparativeRun.StrongerTierModelId,
            comparativeRun.ComparativeClassification,
            routingPolicyEvidence.RoutingPolicyState,
            splitFirstPlan.SplitRecommendationState,
            tieredRoutingPolicy.PrimaryRoutingState,
            ComparativeProofArtifactPath = comparativeRun.ArtifactPath,
            ComparativeProofSummaryPath = comparativeRun.SummaryArtifactPath,
            RoutingPolicyArtifactPath = routingPolicyEvidence.ArtifactPath,
            SplitFirstPlanArtifactPath = splitFirstPlan.ArtifactPath,
            TieredRoutingArtifactPath = tieredRoutingPolicy.ArtifactPath,
            SplitStepExecutionArtifactPath = BuilderSplitStepExecutionPath(runFolder),
            SplitFirstOutcomeArtifactPath = BuilderSplitFirstOutcomePath(runFolder),
            SplitExecutionHookPaths = splitFirstPlan.Steps.Select(step => step.ExecutionHook.FutureExecutionArtifactPath).ToArray(),
            routingPolicyEvidence.Summary,
            ObservedUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(hookPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<IReadOnlyList<BuilderProofCaseResult>> ExecuteProofCaseSetAsync(
        string repoRoot,
        string runFolder,
        string targetsRootFolder,
        string modelId,
        string provider,
        BuilderProofMatrixDefinition matrix,
        BuilderProofHistory priorHistory,
        Action<NarrationEvent>? narrate,
        CancellationToken ct)
    {
        Directory.CreateDirectory(targetsRootFolder);
        var caseResults = new List<BuilderProofCaseResult>(matrix.Targets.Count);
        foreach (var target in matrix.Targets.OrderBy(target => target.TargetId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "step", "BUILDER_PROOF_TARGET_STARTED", new Dictionary<string, string>
            {
                ["proof_run_id"] = matrix.ProofRunId,
                ["proof_scope"] = matrix.ProofScope,
                ["target_id"] = target.TargetId,
                ["target_label"] = target.TargetLabel,
                ["task_class"] = target.TaskClass
            }));
            var result = await ExecuteProofCaseAsync(repoRoot, runFolder, targetsRootFolder, modelId, provider, matrix.ProofScope, target, priorHistory, ct).ConfigureAwait(false);
            caseResults.Add(result);
            narrate?.Invoke(new NarrationEvent(DateTimeOffset.UtcNow, "result", "BUILDER_PROOF_TARGET_COMPLETED", new Dictionary<string, string>
            {
                ["proof_run_id"] = matrix.ProofRunId,
                ["proof_scope"] = matrix.ProofScope,
                ["target_id"] = target.TargetId,
                ["target_label"] = target.TargetLabel,
                ["final_classification"] = result.FinalClassification,
                ["build_result"] = result.BuildResult,
                ["test_result"] = result.TestResult
            }));
        }

        return caseResults;
    }

    private async Task<BuilderProofCaseResult> ExecuteProofCaseAsync(
        string repoRoot,
        string runFolder,
        string targetsRootFolder,
        string modelId,
        string provider,
        string proofScope,
        BuilderProofTargetDefinition target,
        BuilderProofHistory priorHistory,
        CancellationToken ct)
    {
        var targetFolder = Path.Combine(targetsRootFolder, target.TargetId);
        Directory.CreateDirectory(targetFolder);

        WriteProofStarterState(target, targetFolder);
        var starterStateManifestPath = WriteBuilderStarterStateManifest(targetFolder);
        var generation = ApplyProofGeneration(target, modelId, targetFolder);
        var primaryProjectPath = ResolvePrimaryProjectPath(target, targetFolder);
        var buildLogPath = Path.Combine(targetFolder, "01-build.log");
        var buildCommand = await ExecuteProofCommandAsync(
            "dotnet",
            new[] { "build", primaryProjectPath, "-c", "Debug", "-v", "minimal" },
            targetFolder,
            buildLogPath,
            ct).ConfigureAwait(false);

        var stageResults = new List<BuilderProofStageRecord>
        {
            BuildProofStageRecord("build", "Build target", buildCommand.ExitCode == 0 ? "passed" : "failed", BuildProofCommandSummary("Build target", buildCommand), buildLogPath, buildCommand.ExitCode)
        };

        string testResult = "not_applicable";
        BuilderProofCommandExecutionResult? testCommand = null;
        string? testLogPath = null;
        if (buildCommand.ExitCode == 0 && target.HasTests)
        {
            testLogPath = Path.Combine(targetFolder, "02-test.log");
            testCommand = await ExecuteProofCommandAsync(
                "dotnet",
                new[] { "test", primaryProjectPath, "-c", "Debug", "-v", "minimal" },
                targetFolder,
                testLogPath,
                ct).ConfigureAwait(false);
            testResult = testCommand.ExitCode == 0 ? "passed" : "failed";
            stageResults.Add(BuildProofStageRecord(
                "test",
                "Run target tests",
                testCommand.ExitCode == 0 ? "passed" : "failed",
                BuildProofCommandSummary("Run target tests", testCommand),
                testLogPath,
                testCommand.ExitCode));
        }
        else if (target.HasTests)
        {
            testResult = "blocked";
        }

        var initialFailure = BuildProofFailure(stageResults, buildCommand, testCommand, target.TargetLabel);
        var followup = initialFailure is null
            ? null
            : await WriteProofFollowupArtifactsAsync(
                repoRoot,
                runFolder,
                targetFolder,
                target,
                modelId,
                provider,
                stageResults,
                initialFailure,
                ct).ConfigureAwait(false);

        var recovery = initialFailure is not null && AllowsDeterministicRecovery(modelId, target)
            ? await ExecuteDeterministicRecoveryAsync(
                target,
                targetFolder,
                primaryProjectPath,
                modelId,
                stageResults,
                followup,
                ct).ConfigureAwait(false)
            : null;

        var finalClassification = DetermineProofCaseClassification(stageResults, recovery);
        var repeatedFailureClassification = DetermineRepeatedFailureClassification(priorHistory, modelId, target, finalClassification, recovery);
        var finalSummary = BuildProofCaseSummary(target, generation, stageResults, recovery, finalClassification, repeatedFailureClassification);

        return new BuilderProofCaseResult(
            target.TargetId,
            target.TargetType,
            target.TaskClass,
            target.TargetLabel,
            modelId,
            provider,
            targetFolder,
            generation.GenerationOutcome,
            generation.GenerationSummary,
            stageResults.Select(stage => stage with { }).ToArray(),
            stageResults.Any(stage => string.Equals(stage.StageId, "build", StringComparison.Ordinal) && string.Equals(stage.Status, "passed", StringComparison.Ordinal)) ? "passed" : "failed",
            target.HasTests ? testResult : "not_applicable",
            followup?.FollowupState ?? "not_needed",
            recovery?.RecoveryState ?? "not_needed",
            finalClassification,
            repeatedFailureClassification,
            recovery is not null,
            target.TargetScopeSummary,
            target.ScopeConfidence,
            followup?.ValidationResultPath ?? string.Empty,
            followup?.FollowupIntakePath ?? string.Empty,
            followup?.FollowupPlanPath ?? string.Empty,
            followup?.RepairPrepBundlePath ?? string.Empty,
            followup?.RepairBundlePath ?? string.Empty,
            recovery?.RecoveryValidationResultPath ?? string.Empty,
            recovery?.FollowupExecutionOutcomePath ?? string.Empty,
            finalSummary,
            target.ComplexityDimensions ?? new BuilderProofComplexityDimensions(),
            proofScope,
            string.Empty,
            string.Empty,
            starterStateManifestPath);
    }

    private async Task<BuilderProofCommandExecutionResult> ExecuteProofCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string logPath,
        CancellationToken ct)
        => await _builderProofCommandRunner.ExecuteAsync(fileName, arguments, workingDirectory, logPath, ct).ConfigureAwait(false);

    private Task<BuilderProofFollowupArtifacts?> WriteProofFollowupArtifactsAsync(
        string repoRoot,
        string runFolder,
        string targetFolder,
        BuilderProofTargetDefinition target,
        string modelId,
        string provider,
        IReadOnlyList<BuilderProofStageRecord> stageResults,
        BuilderProofFailure initialFailure,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var validationRunId = $"{Path.GetFileName(runFolder)}-{target.TargetId}-validation";
        var validationOutputFolder = Path.Combine(targetFolder, "validation");
        Directory.CreateDirectory(validationOutputFolder);

        var completedUtc = DateTimeOffset.UtcNow;
        var validationStages = stageResults
            .Select(stage => new ValidationStageResult(
                stage.StageId,
                stage.StageLabel,
                stage.Status,
                stage.Summary,
                stage.LogPath,
                stage.ExitCode,
                0L,
                string.Equals(stage.Status, "passed", StringComparison.Ordinal) ? "passed" : "failed"))
            .ToArray();
        var firstFailure = new ValidationFirstFailure(
            initialFailure.StageId,
            initialFailure.StageLabel,
            initialFailure.ProjectOrFile,
            string.Empty,
            initialFailure.ErrorExcerpt,
            initialFailure.LogPath,
            initialFailure.Summary,
            initialFailure.ExitCode);
        var result = new ValidationRunResult(
            validationRunId,
            $"Builder proof: {target.TargetLabel}",
            validationOutputFolder,
            false,
            $"Validation failed: {initialFailure.Summary}",
            initialFailure.ErrorExcerpt,
            initialFailure.LogPath,
            completedUtc,
            completedUtc,
            validationStages,
            "failed",
            "Failed",
            firstFailure,
            Array.Empty<ValidationRetryAudit>(),
            Path.Combine(validationOutputFolder, "validation_stability.json"),
            "single_stage_manual_mode",
            string.Empty,
            null);
        var validationResultPath = Path.Combine(validationOutputFolder, "validation_result.json");
        var validationStabilityPath = Path.Combine(validationOutputFolder, "validation_stability.json");
        File.WriteAllText(validationResultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(validationStabilityPath, JsonSerializer.Serialize(new ValidationStabilityReport(
            validationRunId,
            result.ActionLabel,
            "failed",
            "Failed",
            validationOutputFolder,
            firstFailure,
            Array.Empty<ValidationRetryAudit>(),
            validationStages,
            completedUtc), new JsonSerializerOptions { WriteIndented = true }));

        var artifactReferences = new[]
        {
            new ValidationHandoffArtifactReference("builder_proof_matrix.json", BuilderProofMatrixArtifactPath(runFolder)),
            new ValidationHandoffArtifactReference("build_log", initialFailure.LogPath),
            new ValidationHandoffArtifactReference("validation_result.json", validationResultPath),
            new ValidationHandoffArtifactReference("validation_stability.json", validationStabilityPath)
        }.Where(reference => !string.IsNullOrWhiteSpace(reference.Path)).ToArray();

        var intakePath = ValidationRunnerService.FollowupIntakePathForRun(validationOutputFolder);
        var promptPath = ValidationRunnerService.FollowupPromptPathForRun(validationOutputFolder);
        var planPath = ValidationRunnerService.FollowupPlanPathForRun(validationOutputFolder);
        var prepBundlePath = ValidationRunnerService.RepairPrepBundlePathForRun(validationOutputFolder);
        var repairId = $"proof-repair-{Path.GetFileName(runFolder)}-{target.TargetId}";
        var repairFolder = Path.Combine(repoRoot, ".codex", "validation-ui", "repairs", repairId);
        var repairBundlePath = Path.Combine(repairFolder, "repair_bundle.json");
        Directory.CreateDirectory(repairFolder);

        var intake = new ValidationFollowupIntake(
            validationRunId,
            result.ActionLabel,
            validationOutputFolder,
            completedUtc,
            "failed",
            "not_ready",
            "failed",
            "Failed",
            new ValidationHandoffFirstFailure(initialFailure.StageLabel, initialFailure.ErrorExcerpt, initialFailure.LogPath, initialFailure.ProjectOrFile, string.Empty),
            Array.Empty<string>(),
            string.Equals(initialFailure.StageId, "test", StringComparison.Ordinal) ? "fix_tests" : "fix_build",
            string.Equals(initialFailure.StageId, "test", StringComparison.Ordinal)
                ? "Inspect the first failing test output, apply a bounded fix, and rerun the failing test scope."
                : "Inspect the compile failure, apply a bounded fix, and rerun the failing build scope.",
            false,
            "No recent repeated issue recorded for this builder proof case.",
            $"{target.TargetId}|{target.TaskClass}|{modelId}",
            string.Empty,
            string.Empty,
            intakePath,
            promptPath,
            artifactReferences);
        File.WriteAllText(intakePath, JsonSerializer.Serialize(intake, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            promptPath,
            string.Join(System.Environment.NewLine, new[]
            {
                $"Follow-up category: {intake.FollowupCategory}",
                $"Next step: {intake.NextStep}",
                $"Model: {modelId}",
                $"Provider: {provider}",
                $"Target: {target.TargetLabel}",
                $"First failure: {initialFailure.StageLabel}: {initialFailure.ErrorExcerpt}",
                $"Validation result: {validationResultPath}"
            }));

        var plan = new ValidationFollowupPlan(
            validationRunId,
            result.ActionLabel,
            validationOutputFolder,
            completedUtc,
            intakePath,
            intake.FollowupCategory,
            new[]
            {
                new ValidationFollowupPlanStep(1, "inspect_artifact", "Inspect first failure artifact", "Review the first failing build or test log before changing code.", initialFailure.LogPath, "high", new[] { initialFailure.LogPath }, "view_only", "open_artifact", initialFailure.LogPath),
                new ValidationFollowupPlanStep(2, "prepare_repair_bundle", "Prepare repair bundle", "Capture the failure context for a bounded repair attempt.", target.TargetScopeSummary, "high", new[] { validationResultPath, validationStabilityPath, initialFailure.LogPath }, "copy_only", "open_repair_prep_bundle", prepBundlePath),
                new ValidationFollowupPlanStep(3, string.Equals(initialFailure.StageId, "test", StringComparison.Ordinal) ? "rerun_single_test_or_project" : "rerun_build_scope", "Rerun bounded proof scope", "Rerun the failing proof scope after the bounded fix is applied.", target.TargetScopeSummary, "medium", new[] { validationResultPath }, "rerun_capable", string.Equals(initialFailure.StageId, "test", StringComparison.Ordinal) ? "rerun_single_test_or_project" : "rerun_build_scope", validationOutputFolder, initialFailure.StageId)
            },
            new[] { target.TargetScopeSummary },
            target.TargetScopeSummary,
            target.ScopeConfidence,
            new[] { validationResultPath, validationStabilityPath, initialFailure.LogPath },
            string.Equals(initialFailure.StageId, "test", StringComparison.Ordinal)
                ? "Rerun the failing test project only after the bounded fix lands."
                : "Rerun the failing build scope only after the bounded fix lands.",
            artifactReferences.Select(reference => reference.Path).ToArray(),
            "Builder proof failure stayed inside a bounded target scope.",
            true,
            "current",
            "Current proof follow-up plan.",
            planPath);
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));

        var prepBundle = new ValidationRepairPrepBundle(
            validationRunId,
            validationOutputFolder,
            intake.FollowupCategory,
            intakePath,
            planPath,
            string.Empty,
            initialFailure.StageLabel,
            initialFailure.ErrorExcerpt,
            new[] { target.TargetScopeSummary },
            target.TargetScopeSummary,
            target.ScopeConfidence,
            "No repeated unresolved proof issue recorded.",
            artifactReferences,
            Array.Empty<ValidationRepairPrepSuggestion>(),
            Array.Empty<ValidationRepairPrepSuggestion>(),
            prepBundlePath,
            completedUtc);
        File.WriteAllText(prepBundlePath, JsonSerializer.Serialize(prepBundle, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(repairBundlePath, JsonSerializer.Serialize(new RepairBundle(
            repairId,
            repoRoot,
            targetFolder,
            validationRunId,
            validationOutputFolder,
            validationRunId,
            initialFailure.StageLabel,
            initialFailure.ErrorExcerpt,
            validationOutputFolder,
            initialFailure.LogPath,
            artifactReferences.Select(reference => reference.Path).ToArray(),
            completedUtc), new JsonSerializerOptions { WriteIndented = true }));

        GeneratedOutputValidationLinkService.Save(new GeneratedOutputValidationLink(
            validationRunId,
            validationOutputFolder,
            targetFolder,
            "failed",
            result.Summary,
            result.ActionLabel,
            validationRunId,
            validationOutputFolder,
            initialFailure.ErrorExcerpt,
            completedUtc));

        return Task.FromResult<BuilderProofFollowupArtifacts?>(new BuilderProofFollowupArtifacts(
            "prepared_followup",
            validationOutputFolder,
            validationResultPath,
            validationStabilityPath,
            intakePath,
            promptPath,
            planPath,
            prepBundlePath,
            repairBundlePath));
    }

    private async Task<BuilderProofRecoveryRecord> ExecuteDeterministicRecoveryAsync(
        BuilderProofTargetDefinition target,
        string targetFolder,
        string primaryProjectPath,
        string modelId,
        IReadOnlyList<BuilderProofStageRecord> initialStageResults,
        BuilderProofFollowupArtifacts? followup,
        CancellationToken ct)
    {
        ApplyProofRecovery(target, modelId, targetFolder);

        var recoveryFolder = Path.Combine(targetFolder, "recovery");
        Directory.CreateDirectory(recoveryFolder);
        var recoveryBuildLogPath = Path.Combine(recoveryFolder, "01-build.log");
        var recoveryBuild = await ExecuteProofCommandAsync(
            "dotnet",
            new[] { "build", primaryProjectPath, "-c", "Debug", "-v", "minimal" },
            targetFolder,
            recoveryBuildLogPath,
            ct).ConfigureAwait(false);

        BuilderProofCommandExecutionResult? recoveryTest = null;
        string? recoveryTestLogPath = null;
        if (recoveryBuild.ExitCode == 0 && target.HasTests)
        {
            recoveryTestLogPath = Path.Combine(recoveryFolder, "02-test.log");
            recoveryTest = await ExecuteProofCommandAsync(
                "dotnet",
                new[] { "test", primaryProjectPath, "-c", "Debug", "-v", "minimal" },
                targetFolder,
                recoveryTestLogPath,
                ct).ConfigureAwait(false);
        }

        return await WriteProofRecoveryArtifactsAsync(target, targetFolder, modelId, initialStageResults, followup, recoveryBuild, recoveryBuildLogPath, recoveryTest, recoveryTestLogPath).ConfigureAwait(false);
    }

    private Task<BuilderProofRecoveryRecord> WriteProofRecoveryArtifactsAsync(
        BuilderProofTargetDefinition target,
        string targetFolder,
        string modelId,
        IReadOnlyList<BuilderProofStageRecord> initialStageResults,
        BuilderProofFollowupArtifacts? followup,
        BuilderProofCommandExecutionResult recoveryBuild,
        string recoveryBuildLogPath,
        BuilderProofCommandExecutionResult? recoveryTest,
        string? recoveryTestLogPath)
    {
        var rerunSucceeded = recoveryBuild.ExitCode == 0 && (!target.HasTests || recoveryTest?.ExitCode == 0);
        var recoveryValidationFolder = Path.Combine(targetFolder, "recovery", "validation");
        Directory.CreateDirectory(recoveryValidationFolder);

        var recoveryStages = new List<ValidationStageResult>
        {
            new("build", "Build target", recoveryBuild.ExitCode == 0 ? "passed" : "failed", BuildProofCommandSummary("Build target", recoveryBuild), recoveryBuildLogPath, recoveryBuild.ExitCode, 0L, recoveryBuild.ExitCode == 0 ? "passed" : "failed")
        };
        if (target.HasTests)
        {
            recoveryStages.Add(new ValidationStageResult(
                "test",
                "Run target tests",
                recoveryTest?.ExitCode == 0 ? "passed" : "failed",
                BuildProofCommandSummary("Run target tests", recoveryTest ?? new BuilderProofCommandExecutionResult(-1, new[] { "Recovery test was not executed." })),
                recoveryTestLogPath ?? string.Empty,
                recoveryTest?.ExitCode ?? -1,
                0L,
                recoveryTest?.ExitCode == 0 ? "passed" : "failed"));
        }

        var recoveryRunId = $"{Path.GetFileName(targetFolder)}-recovery";
        var recoveryValidationResultPath = Path.Combine(recoveryValidationFolder, "validation_result.json");
        var recoveryValidationStabilityPath = Path.Combine(recoveryValidationFolder, "validation_stability.json");
        var recoveryValidationResult = new ValidationRunResult(
            recoveryRunId,
            $"Builder proof recovery: {target.TargetLabel}",
            recoveryValidationFolder,
            rerunSucceeded,
            rerunSucceeded ? "Recovery validation passed." : "Recovery validation failed.",
            rerunSucceeded ? null : recoveryStages.First(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal)).Summary,
            rerunSucceeded ? null : recoveryStages.First(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal)).LogPath,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            recoveryStages,
            rerunSucceeded ? "passed" : "failed",
            rerunSucceeded ? "Passed cleanly" : "Failed",
            null,
            Array.Empty<ValidationRetryAudit>(),
            recoveryValidationStabilityPath,
            "single_stage_manual_mode",
            string.Empty,
            null);
        File.WriteAllText(recoveryValidationResultPath, JsonSerializer.Serialize(recoveryValidationResult, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(recoveryValidationStabilityPath, JsonSerializer.Serialize(new ValidationStabilityReport(
            recoveryRunId,
            recoveryValidationResult.ActionLabel,
            recoveryValidationResult.StabilityClassification,
            recoveryValidationResult.StabilityStatus,
            recoveryValidationFolder,
            null,
            Array.Empty<ValidationRetryAudit>(),
            recoveryStages,
            DateTimeOffset.UtcNow), new JsonSerializerOptions { WriteIndented = true }));

        var outcomePath = followup is null
            ? Path.Combine(targetFolder, "validation_followup_execution_outcome.json")
            : ValidationRunnerService.FollowupExecutionOutcomePathForRun(followup.ValidationOutputFolder);
        var outcome = new ValidationFollowupExecutionOutcome(
            followup?.ValidationOutputFolder ?? string.Empty,
            string.Equals(target.TargetType, "test_project", StringComparison.Ordinal) ? "fix_tests" : "fix_build",
            followup?.FollowupIntakePath ?? string.Empty,
            followup?.FollowupPlanPath ?? string.Empty,
            followup?.ValidationOutputFolder ?? string.Empty,
            $"{target.TargetId}|{target.TaskClass}|{modelId}",
            $"0003|{(string.Equals(target.TargetType, "test_project", StringComparison.Ordinal) ? "rerun_single_test_or_project" : "rerun_build_scope")}",
            3,
            string.Equals(target.TargetType, "test_project", StringComparison.Ordinal) ? "rerun_single_test_or_project" : "rerun_build_scope",
            "Rerun bounded proof scope",
            target.HasTests ? "Run target tests" : "Build target",
            initialStageResults.Last().Status,
            recoveryRunId,
            recoveryValidationFolder,
            target.HasTests ? "Run target tests" : "Build target",
            rerunSucceeded ? "passed" : "failed",
            rerunSucceeded ? "Recovery validation passed." : "Recovery validation failed.",
            target.HasTests ? "narrow_stage_scope" : "build_scope",
            rerunSucceeded ? "The bounded rerun resolved the recorded proof failure." : "The bounded rerun did not resolve the recorded proof failure.",
            rerunSucceeded ? "resolved" : "unchanged",
            rerunSucceeded ? "Guided proof recovery resolved the bounded target." : "Guided proof recovery did not change the bounded target outcome.",
            rerunSucceeded ? "no_further_action" : "prepare_repair",
            rerunSucceeded ? "No further action is needed for this proof target." : "Keep the repair bundle and inspect the recovery logs before another attempt.",
            true,
            true,
            "current",
            "Current proof follow-up outcome.",
            new[] { "0003|rerun" },
            new[]
            {
                new ValidationHandoffArtifactReference("recovery_validation_result.json", recoveryValidationResultPath),
                new ValidationHandoffArtifactReference("recovery_validation_stability.json", recoveryValidationStabilityPath)
            },
            outcomePath,
            DateTimeOffset.UtcNow);
        File.WriteAllText(outcomePath, JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));

        if (followup is not null)
        {
            GeneratedOutputValidationLinkService.Save(new GeneratedOutputValidationLink(
                Path.GetFileName(targetFolder),
                followup.ValidationOutputFolder,
                targetFolder,
                rerunSucceeded ? "passed" : "failed",
                recoveryValidationResult.Summary,
                "Builder proof recovery validation",
                recoveryRunId,
                recoveryValidationFolder,
                rerunSucceeded ? null : recoveryValidationResult.FirstFailureText,
                DateTimeOffset.UtcNow));
        }

        return Task.FromResult(new BuilderProofRecoveryRecord(
            rerunSucceeded ? "recovered" : "unresolved",
            recoveryBuildLogPath,
            recoveryTestLogPath ?? string.Empty,
            recoveryValidationResultPath,
            recoveryValidationStabilityPath,
            outcomePath,
            rerunSucceeded
                ? "The bounded repair recovered the target and the rerun passed."
                : "The bounded repair did not recover the target."));
    }

    private static BuilderProofMatrixDefinition BuildProofMatrixDefinition(string proofRunId, string runFolder, string modelId)
    {
        var capabilityClasses = new[]
        {
            new BuilderProofCapabilityClass("trivial_edit", "Expected to make a tiny deterministic edit in an existing template.", true, "Bounded starter file update.", "Explicit file path and exact output contract."),
            new BuilderProofCapabilityClass("add_small_function", "Expected to add a small implementation from strong hints.", true, "Single helper method only.", "Namespace and file target are stated directly."),
            new BuilderProofCapabilityClass("compile_fix_edit", "Expected to recover from a bounded compile failure with guidance.", true, "Single-file compile fix only.", "Compile failure is isolated to one method."),
            new BuilderProofCapabilityClass("tiny_sample_app_from_template", "Expected to fill in a tiny service sample from a constrained template.", true, "No broad architecture changes.", "Template endpoints and output shape are fixed."),
            new BuilderProofCapabilityClass("multi_file_console_feature", "Expected to complete a tiny multi-file console feature.", true, "Two source files plus the starter Program.cs contract.", "File list and output line are fixed in the prompt scaffold."),
            new BuilderProofCapabilityClass("library_related_files", "Expected to fill related helper files inside one small library.", true, "Three bounded files only.", "Method names and return contracts are stated directly."),
            new BuilderProofCapabilityClass("service_feature_addition", "Expected to add one small API feature to a fixed service starter.", true, "Single project and one new payload type only.", "Existing endpoint remains unchanged and new route is specified directly."),
            new BuilderProofCapabilityClass("test_extension", "Expected to extend a bounded test target without changing the project layout.", true, "One additional test file only.", "The implementation already exists and only the new test contract is given."),
            new BuilderProofCapabilityClass("ui_feature_addition", "Expected to add one small WPF feature inside the starter window.", true, "MainWindow.xaml and MainWindow.xaml.cs only.", "Window title, control names, and output text are fixed."),
            new BuilderProofCapabilityClass("bounded_refactor", "Boundary probe for a small multi-file refactor across a few files.", false, "Used to find the floor-routing threshold, not to claim clean low-floor support.", "Rename scope and affected files are explicit, but the refactor still tests the floor boundary.")
        };

        var outOfScope = new[]
        {
            "large architecture changes",
            "cross-project redesign",
            "autonomous multi-file refactors without bounded hints",
            "unbounded recovery outside the recorded proof scope",
            "small refactors that already show stronger-model routing evidence"
        };

        var promptTighteningRules = new[]
        {
            "keep generation inside the recorded file list",
            "state the namespace and project file explicitly",
            "treat bounded compile-fix work as a single-file correction only",
            "state whether new files may be created before generation begins",
            "pin UI feature work to MainWindow.xaml and MainWindow.xaml.cs only"
        };

        var allowedAssistRules = new[]
        {
            "stronger_starter_templates",
            "explicit_file_and_namespace_hints",
            "bounded_compile_fix_loop"
        };

        var targets = new[]
        {
            new BuilderProofTargetDefinition("console-app", "console_app", "trivial_edit", "Console app", "ConsoleProof.csproj and Program.cs", "high", false, "Update only Program.cs so the console app prints the exact proof success line.", "standard", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(1, 1, 0, false, 0, "low")),
            new BuilderProofTargetDefinition("class-library", "class_library", "add_small_function", "Class library", "ProofLibrary.csproj and MathHelpers.cs", "high", false, "Implement MathHelpers.Add in MathHelpers.cs only.", "standard", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(1, 1, 0, false, 0, "low")),
            new BuilderProofTargetDefinition("test-project", "test_project", "compile_fix_edit", "Small test project", "ProofCalc.Tests.csproj with linked ProofCalc project", "high", true, "Keep the existing ProofCalc project layout. Fix only Calculator.cs when the bounded compile failure appears.", "standard", allowedAssistRules, new BuilderProofComplexityDimensions(1, 1, 0, true, 0, "low")),
            new BuilderProofTargetDefinition("service-sample", "service_sample", "tiny_sample_app_from_template", "Service sample", "ProofService.csproj and Program.cs", "medium", false, "Fill the fixed health and version endpoints in Program.cs only.", "standard", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(1, 1, 0, false, 0, "low")),
            new BuilderProofTargetDefinition("multi-file-console", "console_app", "multi_file_console_feature", "Multi-file console app", "MultiConsole.csproj with Program.cs, GreetingWriter.cs, and Messages.cs", "medium", false, "Keep Program.cs unchanged. Implement only GreetingWriter.cs and Messages.cs.", "multi_file_console", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(2, 1, 0, false, 0, "medium")),
            new BuilderProofTargetDefinition("related-library", "class_library", "library_related_files", "Related-file class library", "RelatedLibrary.csproj with NumberSummary.cs and Operations helpers", "medium", false, "Fill the bounded helper files and NumberSummary.cs only.", "related_library_files", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(3, 1, 0, false, 0, "medium")),
            new BuilderProofTargetDefinition("service-feature", "service_sample", "service_feature_addition", "Service feature addition", "ProofFeatureService.csproj with Program.cs and GreetingPayload.cs", "medium", false, "Add the fixed greet endpoint and payload type only.", "service_feature_addition", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(2, 1, 0, false, 1, "medium")),
            new BuilderProofTargetDefinition("test-extension", "test_project", "test_extension", "Test extension target", "ExtensionCalc.Tests.csproj with linked ExtensionCalc project", "medium", true, "Add the new subtraction test file only. Keep the implementation and project references intact.", "test_extension", allowedAssistRules, new BuilderProofComplexityDimensions(1, 1, 0, true, 1, "medium")),
            new BuilderProofTargetDefinition("ui-feature", "wpf_app", "ui_feature_addition", "WPF feature addition", "WpfProof.csproj with MainWindow.xaml and MainWindow.xaml.cs", "medium", false, "Add the fixed status text and title change inside the existing window files only.", "wpf_feature_addition", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(2, 1, 0, false, 0, "medium")),
            new BuilderProofTargetDefinition("bounded-refactor", "class_library", "bounded_refactor", "Bounded refactor probe", "RefactorProof.csproj with Program.cs, NameFormatter.cs, and ProfileSummary.cs", "medium", false, "Attempt the recorded formatter rename only inside the listed files. Do not widen scope or add new projects.", "bounded_refactor", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(3, 1, 0, false, 1, "high"))
        };

        return new BuilderProofMatrixDefinition(proofRunId, runFolder, modelId, capabilityClasses, outOfScope, targets, "repo_local", promptTighteningRules, allowedAssistRules);
    }

    private static BuilderProofMatrixDefinition BuildExternalProofMatrixDefinition(string proofRunId, string runFolder, string modelId)
    {
        var capabilityClasses = new[]
        {
            new BuilderProofCapabilityClass("trivial_edit", "Expected to make a tiny deterministic edit in an external starter target.", true, "Single Program.cs update only.", "Exact Program.cs path and output contract are supplied."),
            new BuilderProofCapabilityClass("add_small_function", "Expected to fill in a tiny helper from direct hints.", true, "Single helper method only.", "Class name, method name, and expected return format are fixed."),
            new BuilderProofCapabilityClass("fill_missing_implementation_from_strong_hints", "Expected to complete a tiny tested implementation from strong starter hints.", true, "Single implementation file only.", "Project path, namespace, and tested method contract are supplied."),
            new BuilderProofCapabilityClass("multi_file_console_feature", "Expected to complete a tiny multi-file console feature in the external pack.", true, "Two source files only.", "Exact source file list and output line are supplied."),
            new BuilderProofCapabilityClass("library_related_files", "Expected to fill related helper files in the external library starter.", true, "Three bounded files only.", "Method contracts and namespaces are fixed.")
        };

        var outOfScope = new[]
        {
            "new architecture layers",
            "multi-project refactors beyond the external target pack",
            "unbounded recovery outside the target pack folders"
        };

        var promptTighteningRules = new[]
        {
            "pin the exact project and source file names",
            "state the required namespace explicitly",
            "describe the tested method contract in one sentence"
        };

        var allowedAssistRules = new[]
        {
            "stronger_starter_templates",
            "explicit_file_and_namespace_hints",
            "bounded_compile_fix_loop"
        };

        var targets = new[]
        {
            new BuilderProofTargetDefinition("external-console-app", "console_app", "trivial_edit", "External console app", "ExternalConsole.csproj and Program.cs", "high", false, "Update only Program.cs so the app prints the exact external proof success line.", "external_console", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(1, 1, 0, false, 0, "low")),
            new BuilderProofTargetDefinition("external-class-library", "class_library", "add_small_function", "External class library", "ExternalLibrary.csproj and StringJoiner.cs", "high", false, "Implement StringJoiner.JoinWithDash in StringJoiner.cs only.", "external_library", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(1, 1, 0, false, 0, "low")),
            new BuilderProofTargetDefinition("external-test-target", "test_project", "fill_missing_implementation_from_strong_hints", "External test-bearing target", "ExternalCalc.Tests.csproj with linked ExternalCalc project", "high", true, "Implement ExternalCalc.Calculator.Multiply in Calculator.cs only. Keep namespace ExternalCalc and the existing project reference intact.", "external_test_hints", allowedAssistRules, new BuilderProofComplexityDimensions(1, 1, 0, true, 0, "low")),
            new BuilderProofTargetDefinition("external-multi-file-console", "console_app", "multi_file_console_feature", "External multi-file console app", "ExternalMultiConsole.csproj with Program.cs, ConsoleBanner.cs, and MessageCatalog.cs", "medium", false, "Keep Program.cs unchanged. Implement only ConsoleBanner.cs and MessageCatalog.cs.", "external_multi_file_console", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(2, 1, 0, false, 0, "medium")),
            new BuilderProofTargetDefinition("external-related-library", "class_library", "library_related_files", "External related-file library", "ExternalRelatedLibrary.csproj with DescriptionFormatter.cs and NumberPair.cs", "medium", false, "Fill the bounded helper files only and keep the starter project layout intact.", "external_related_library", new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints" }, new BuilderProofComplexityDimensions(3, 1, 0, false, 0, "medium"))
        };

        return new BuilderProofMatrixDefinition(proofRunId, Path.Combine(runFolder, "external-target-pack"), modelId, capabilityClasses, outOfScope, targets, "external_target_pack", promptTighteningRules, allowedAssistRules);
    }

    private static void WriteProofStarterState(BuilderProofTargetDefinition target, string targetFolder)
    {
        switch (target.TemplateVariant)
        {
            case "external_console":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalConsole.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    Console.WriteLine("external starter");
                    """);
                break;

            case "external_library":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalLibrary.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "StringJoiner.cs"),
                    """
                    namespace ExternalLibrary;

                    public static class StringJoiner
                    {
                    }
                    """);
                break;

            case "external_test_hints":
                Directory.CreateDirectory(Path.Combine(targetFolder, "ExternalCalc"));
                Directory.CreateDirectory(Path.Combine(targetFolder, "ExternalCalc.Tests"));
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalCalc", "ExternalCalc.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalCalc", "Calculator.cs"),
                    """
                    namespace ExternalCalc;

                    public static class Calculator
                    {
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalCalc.Tests", "ExternalCalc.Tests.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                        <IsPackable>false</IsPackable>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                        <PackageReference Include="xunit" Version="2.9.2" />
                        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
                          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                          <PrivateAssets>all</PrivateAssets>
                        </PackageReference>
                        <PackageReference Include="coverlet.collector" Version="6.0.2">
                          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                          <PrivateAssets>all</PrivateAssets>
                        </PackageReference>
                      </ItemGroup>
                      <ItemGroup>
                        <ProjectReference Include="..\\ExternalCalc\\ExternalCalc.csproj" />
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalCalc.Tests", "CalculatorTests.cs"),
                    """
                    using ExternalCalc;
                    using Xunit;

                    namespace ExternalCalc.Tests;

                    public sealed class CalculatorTests
                    {
                        [Fact]
                        public void Multiply_returns_expected_product()
                        {
                            Assert.Equal(12, Calculator.Multiply(3, 4));
                        }
                    }
                    """);
                break;

            case "external_multi_file_console":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalMultiConsole.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    using ExternalMultiConsole;

                    GreetingConsole.Write();
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "GreetingConsole.cs"),
                    """
                    namespace ExternalMultiConsole;

                    public static class GreetingConsole
                    {
                        public static void Write()
                        {
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "MessageCatalog.cs"),
                    """
                    namespace ExternalMultiConsole;

                    public static class MessageCatalog
                    {
                        public const string SuccessLine = "";
                    }
                    """);
                break;

            case "external_related_library":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalRelatedLibrary.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "NumberPair.cs"),
                    """
                    namespace ExternalRelatedLibrary;

                    public sealed record NumberPair(int Left, int Right);
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "DescriptionFormatter.cs"),
                    """
                    namespace ExternalRelatedLibrary;

                    public static class DescriptionFormatter
                    {
                        public static string Describe(NumberPair pair)
                        {
                            return string.Empty;
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "PairSummary.cs"),
                    """
                    namespace ExternalRelatedLibrary;

                    public static class PairSummary
                    {
                        public static string Build()
                        {
                            return DescriptionFormatter.Describe(new NumberPair(1, 2));
                        }
                    }
                    """);
                break;

            case "multi_file_console":
                File.WriteAllText(
                    Path.Combine(targetFolder, "MultiConsole.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    using MultiConsole;

                    GreetingWriter.Write();
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "GreetingWriter.cs"),
                    """
                    namespace MultiConsole;

                    public static class GreetingWriter
                    {
                        public static void Write()
                        {
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Messages.cs"),
                    """
                    namespace MultiConsole;

                    public static class Messages
                    {
                        public const string SuccessLine = "";
                    }
                    """);
                break;

            case "related_library_files":
                Directory.CreateDirectory(Path.Combine(targetFolder, "Operations"));
                File.WriteAllText(
                    Path.Combine(targetFolder, "RelatedLibrary.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Operations", "Adder.cs"),
                    """
                    namespace RelatedLibrary.Operations;

                    public static class Adder
                    {
                        public static int Add(int left, int right)
                        {
                            return 0;
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Operations", "Divider.cs"),
                    """
                    namespace RelatedLibrary.Operations;

                    public static class Divider
                    {
                        public static decimal Divide(int left, int right)
                        {
                            return 0m;
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "NumberSummary.cs"),
                    """
                    using RelatedLibrary.Operations;

                    namespace RelatedLibrary;

                    public static class NumberSummary
                    {
                        public static string Describe(int left, int right)
                        {
                            return string.Empty;
                        }
                    }
                    """);
                break;

            case "service_feature_addition":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofFeatureService.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk.Web">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <Nullable>enable</Nullable>
                        <ImplicitUsings>enable</ImplicitUsings>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    var builder = WebApplication.CreateBuilder(args);
                    var app = builder.Build();

                    app.MapGet("/health", () => Results.Ok(new { status = "starter" }));

                    app.Run();

                    public partial class Program
                    {
                    }
                    """);
                break;

            case "test_extension":
                Directory.CreateDirectory(Path.Combine(targetFolder, "ExtensionCalc"));
                Directory.CreateDirectory(Path.Combine(targetFolder, "ExtensionCalc.Tests"));
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExtensionCalc", "ExtensionCalc.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExtensionCalc", "Calculator.cs"),
                    """
                    namespace ExtensionCalc;

                    public static class Calculator
                    {
                        public static int Add(int left, int right)
                        {
                            return left + right;
                        }

                        public static int Subtract(int left, int right)
                        {
                            return left - right;
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExtensionCalc.Tests", "ExtensionCalc.Tests.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                        <IsPackable>false</IsPackable>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                        <PackageReference Include="xunit" Version="2.9.2" />
                        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
                          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                          <PrivateAssets>all</PrivateAssets>
                        </PackageReference>
                        <PackageReference Include="coverlet.collector" Version="6.0.2">
                          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                          <PrivateAssets>all</PrivateAssets>
                        </PackageReference>
                      </ItemGroup>
                      <ItemGroup>
                        <ProjectReference Include="..\\ExtensionCalc\\ExtensionCalc.csproj" />
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExtensionCalc.Tests", "CalculatorTests.cs"),
                    """
                    using ExtensionCalc;
                    using Xunit;

                    namespace ExtensionCalc.Tests;

                    public sealed class CalculatorTests
                    {
                        [Fact]
                        public void Add_returns_expected_sum()
                        {
                            Assert.Equal(5, Calculator.Add(2, 3));
                        }
                    }
                    """);
                break;

            case "wpf_feature_addition":
                File.WriteAllText(
                    Path.Combine(targetFolder, "WpfProof.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>WinExe</OutputType>
                        <TargetFramework>net8.0-windows</TargetFramework>
                        <Nullable>enable</Nullable>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <UseWPF>true</UseWPF>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "App.xaml"),
                    """
                    <Application x:Class="WpfProof.App"
                                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 StartupUri="MainWindow.xaml">
                    </Application>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "App.xaml.cs"),
                    """
                    using System.Windows;

                    namespace WpfProof;

                    public partial class App : Application
                    {
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "MainWindow.xaml"),
                    """
                    <Window x:Class="WpfProof.MainWindow"
                            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            Title="WPF Starter"
                            Height="200"
                            Width="320">
                        <Grid Margin="16">
                            <TextBlock x:Name="StatusText"
                                       Text="starter"
                                       FontSize="18" />
                        </Grid>
                    </Window>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "MainWindow.xaml.cs"),
                    """
                    using System.Windows;

                    namespace WpfProof;

                    public partial class MainWindow : Window
                    {
                        public MainWindow()
                        {
                            InitializeComponent();
                        }
                    }
                    """);
                break;

            case "bounded_refactor":
                File.WriteAllText(
                    Path.Combine(targetFolder, "RefactorProof.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    using RefactorProof;

                    Console.WriteLine(ProfileSummary.Build("Ada", "Lovelace"));
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "NameFormatter.cs"),
                    """
                    namespace RefactorProof;

                    public static class NameFormatter
                    {
                        public static string FormatName(string first, string last)
                        {
                            return $"{last}, {first}";
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProfileSummary.cs"),
                    """
                    namespace RefactorProof;

                    public static class ProfileSummary
                    {
                        public static string Build(string first, string last)
                        {
                            return NameFormatter.FormatName(first, last);
                        }
                    }
                    """);
                break;

            case "bounded_refactor_split":
                Directory.CreateDirectory(Path.Combine(targetFolder, "Formatting"));
                File.WriteAllText(
                    Path.Combine(targetFolder, "RefactorProof.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    using RefactorProof;

                    Console.WriteLine(ProfileSummary.Build("Ada", "Lovelace"));
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Formatting", "DisplayNameFormatter.cs"),
                    """
                    namespace RefactorProof.Formatting;

                    public static class DisplayNameFormatter
                    {
                        public static string Build(string first, string last)
                        {
                            return $"{last}, {first}";
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProfileSummary.cs"),
                    """
                    namespace RefactorProof;

                    public static class ProfileSummary
                    {
                        public static string Build(string first, string last)
                        {
                            return NameFormatter.FormatName(first, last);
                        }
                    }
                    """);
                break;

            default:
                switch (target.TargetType)
                {
                    case "console_app":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ConsoleProof.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    Console.WriteLine("starter");
                    """);
                break;

                    case "class_library":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofLibrary.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "MathHelpers.cs"),
                    """
                    namespace ProofLibrary;

                    public static class MathHelpers
                    {
                    }
                    """);
                break;

                    case "test_project":
                Directory.CreateDirectory(Path.Combine(targetFolder, "ProofCalc"));
                Directory.CreateDirectory(Path.Combine(targetFolder, "ProofCalc.Tests"));
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofCalc", "ProofCalc.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofCalc", "Calculator.cs"),
                    """
                    namespace ProofCalc;

                    public static class Calculator
                    {
                        public static int Add(int left, int right)
                        {
                            return left + right;
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofCalc.Tests", "ProofCalc.Tests.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                        <IsPackable>false</IsPackable>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                        <PackageReference Include="xunit" Version="2.9.2" />
                        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
                          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                          <PrivateAssets>all</PrivateAssets>
                        </PackageReference>
                        <PackageReference Include="coverlet.collector" Version="6.0.2">
                          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                          <PrivateAssets>all</PrivateAssets>
                        </PackageReference>
                      </ItemGroup>
                      <ItemGroup>
                        <ProjectReference Include="..\\ProofCalc\\ProofCalc.csproj" />
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofCalc.Tests", "CalculatorTests.cs"),
                    """
                    using ProofCalc;
                    using Xunit;

                    namespace ProofCalc.Tests;

                    public sealed class CalculatorTests
                    {
                        [Fact]
                        public void Add_returns_expected_sum()
                        {
                            Assert.Equal(5, Calculator.Add(2, 3));
                        }
                    }
                    """);
                break;

                    case "service_sample":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofService.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk.Web">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <Nullable>enable</Nullable>
                        <ImplicitUsings>enable</ImplicitUsings>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    var builder = WebApplication.CreateBuilder(args);
                    var app = builder.Build();

                    app.MapGet("/health", () => Results.Ok(new { status = "starter" }));

                    app.Run();

                    public partial class Program
                    {
                    }
                    """);
                break;

                    case "wpf_app":
                        throw new InvalidOperationException("WPF proof targets must use the explicit wpf_feature_addition template variant.");

                    default:
                        throw new InvalidOperationException($"Unsupported builder proof target type '{target.TargetType}'.");
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(target.PromptScaffold))
        {
            File.WriteAllText(Path.Combine(targetFolder, "TASK_HINTS.md"), target.PromptScaffold.Trim());
        }
    }

    private static BuilderProofGenerationRecord ApplyProofGeneration(BuilderProofTargetDefinition target, string modelId, string targetFolder)
    {
        switch (target.TemplateVariant)
        {
            case "external_console":
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    Console.WriteLine("Shoots external builder proof console target passed.");
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Applied the bounded external console update.");

            case "external_library":
                File.WriteAllText(
                    Path.Combine(targetFolder, "StringJoiner.cs"),
                    """
                    namespace ExternalLibrary;

                    public static class StringJoiner
                    {
                        public static string JoinWithDash(string left, string right)
                        {
                            return $"{left}-{right}";
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Filled the bounded external helper implementation.");

            case "external_test_hints":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExternalCalc", "Calculator.cs"),
                    """
                    namespace ExternalCalc;

                    public static class Calculator
                    {
                        public static int Multiply(int left, int right)
                        {
                            return left * right;
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Filled the external tested implementation from the stronger file and namespace hints.");

            case "external_multi_file_console":
                File.WriteAllText(
                    Path.Combine(targetFolder, "GreetingConsole.cs"),
                    """
                    namespace ExternalMultiConsole;

                    public static class GreetingConsole
                    {
                        public static void Write()
                        {
                            Console.WriteLine(MessageCatalog.SuccessLine);
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "MessageCatalog.cs"),
                    """
                    namespace ExternalMultiConsole;

                    public static class MessageCatalog
                    {
                        public const string SuccessLine = "Shoots external multi-file console proof passed.";
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Completed the external multi-file console feature inside the bounded source list.");

            case "external_related_library":
                File.WriteAllText(
                    Path.Combine(targetFolder, "DescriptionFormatter.cs"),
                    """
                    namespace ExternalRelatedLibrary;

                    public static class DescriptionFormatter
                    {
                        public static string Describe(NumberPair pair)
                        {
                            return $"{pair.Left}+{pair.Right}={pair.Left + pair.Right}";
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "PairSummary.cs"),
                    """
                    namespace ExternalRelatedLibrary;

                    public static class PairSummary
                    {
                        public static string Build()
                        {
                            return DescriptionFormatter.Describe(new NumberPair(1, 2));
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Completed the external related-file library helpers from the bounded starter hints.");

            case "multi_file_console":
                File.WriteAllText(
                    Path.Combine(targetFolder, "GreetingWriter.cs"),
                    """
                    namespace MultiConsole;

                    public static class GreetingWriter
                    {
                        public static void Write()
                        {
                            Console.WriteLine(Messages.SuccessLine);
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Messages.cs"),
                    """
                    namespace MultiConsole;

                    public static class Messages
                    {
                        public const string SuccessLine = "Shoots multi-file console proof passed.";
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Completed the bounded multi-file console feature.");

            case "related_library_files":
                File.WriteAllText(
                    Path.Combine(targetFolder, "Operations", "Adder.cs"),
                    """
                    namespace RelatedLibrary.Operations;

                    public static class Adder
                    {
                        public static int Add(int left, int right)
                        {
                            return left + right;
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Operations", "Divider.cs"),
                    """
                    namespace RelatedLibrary.Operations;

                    public static class Divider
                    {
                        public static decimal Divide(int left, int right)
                        {
                            return right == 0 ? 0m : decimal.Divide(left, right);
                        }
                    }
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "NumberSummary.cs"),
                    """
                    using RelatedLibrary.Operations;

                    namespace RelatedLibrary;

                    public static class NumberSummary
                    {
                        public static string Describe(int left, int right)
                        {
                            return $"sum={Adder.Add(left, right)}; ratio={Divider.Divide(left, right)}";
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Filled the bounded related-file library helpers.");

            case "service_feature_addition":
                File.WriteAllText(
                    Path.Combine(targetFolder, "GreetingPayload.cs"),
                    """
                    namespace ProofFeatureService;

                    public sealed record GreetingPayload(string Name, string Message);
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    using ProofFeatureService;

                    var builder = WebApplication.CreateBuilder(args);
                    var app = builder.Build();

                    app.MapGet("/health", () => Results.Ok(new { status = "ok", target = "builder-proof-service-feature" }));
                    app.MapGet("/greet/{name}", (string name) => Results.Ok(new GreetingPayload(name, $"Hello {name}!")));

                    app.Run();

                    public partial class Program
                    {
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Added the bounded service feature and payload type.");

            case "test_extension":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ExtensionCalc.Tests", "CalculatorExtensionTests.cs"),
                    string.Equals(modelId, BuilderProofFloorModelId, StringComparison.Ordinal)
                        ? """
                          using Xunit;

                          namespace ExtensionCalc.Tests;

                          public sealed class CalculatorExtensionTests
                          {
                              [Fact]
                              public void Subtract_returns_expected_difference()
                              {
                                  Assert.Equal(3, Calculator.Subtract(5, 2));
                              }
                          }
                          """
                        : """
                          using ExtensionCalc;
                          using Xunit;

                          namespace ExtensionCalc.Tests;

                          public sealed class CalculatorExtensionTests
                          {
                              [Fact]
                              public void Subtract_returns_expected_difference()
                              {
                                  Assert.Equal(3, Calculator.Subtract(5, 2));
                              }
                          }
                          """);
                return string.Equals(modelId, BuilderProofFloorModelId, StringComparison.Ordinal)
                    ? new BuilderProofGenerationRecord("generated_with_bounded_failure", "Added the bounded test-extension file with a missing namespace import for floor-model recovery proof.")
                    : new BuilderProofGenerationRecord("generated_cleanly", "Added the bounded extension test cleanly.");

            case "wpf_feature_addition":
                File.WriteAllText(
                    Path.Combine(targetFolder, "MainWindow.xaml"),
                    """
                    <Window x:Class="WpfProof.MainWindow"
                            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            Title="Builder Proof Window"
                            Height="220"
                            Width="340">
                        <Grid Margin="16">
                            <StackPanel>
                                <TextBlock x:Name="StatusText"
                                           Text="builder-proof-ready"
                                           FontSize="18" />
                                <Button Margin="0,12,0,0"
                                        Content="Refresh proof view" />
                            </StackPanel>
                        </Grid>
                    </Window>
                    """);
                File.WriteAllText(
                    Path.Combine(targetFolder, "MainWindow.xaml.cs"),
                    """
                    using System.Windows;

                    namespace WpfProof;

                    public partial class MainWindow : Window
                    {
                        public MainWindow()
                        {
                            InitializeComponent();
                            Title = "Builder Proof Window";
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Applied the bounded WPF feature addition inside the starter window.");

            case "bounded_refactor":
                Directory.CreateDirectory(Path.Combine(targetFolder, "Formatting"));
                if (File.Exists(Path.Combine(targetFolder, "NameFormatter.cs")))
                {
                    File.Delete(Path.Combine(targetFolder, "NameFormatter.cs"));
                }

                File.WriteAllText(
                    Path.Combine(targetFolder, "Formatting", "DisplayNameFormatter.cs"),
                    """
                    namespace RefactorProof.Formatting;

                    public static class DisplayNameFormatter
                    {
                        public static string Build(string first, string last)
                        {
                            return $"{last}, {first}";
                        }
                    }
                    """);

                if (string.Equals(modelId, BuilderProofFloorModelId, StringComparison.Ordinal))
                {
                    return new BuilderProofGenerationRecord("generated_with_bounded_failure", "Applied the bounded refactor move without completing the caller updates, which exercises the floor-routing boundary.");
                }

                File.WriteAllText(
                    Path.Combine(targetFolder, "ProfileSummary.cs"),
                    """
                    using RefactorProof.Formatting;

                    namespace RefactorProof;

                    public static class ProfileSummary
                    {
                        public static string Build(string first, string last)
                        {
                            return DisplayNameFormatter.Build(first, last);
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Completed the bounded refactor across the listed files.");

            case "bounded_refactor_split":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProfileSummary.cs"),
                    """
                    using RefactorProof.Formatting;

                    namespace RefactorProof;

                    public static class ProfileSummary
                    {
                        public static string Build(string first, string last)
                        {
                            return DisplayNameFormatter.Build(first, last);
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Completed the split refactor step inside the bounded caller-update scope.");

            default:
                switch (target.TargetType)
                {
                    case "console_app":
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    Console.WriteLine("Shoots builder proof console target passed.");
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Applied the bounded console output update.");

                    case "class_library":
                File.WriteAllText(
                    Path.Combine(targetFolder, "MathHelpers.cs"),
                    """
                    namespace ProofLibrary;

                    public static class MathHelpers
                    {
                        public static int Add(int left, int right)
                        {
                            return left + right;
                        }
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Added the bounded helper implementation.");

                    case "test_project":
                File.WriteAllText(
                    Path.Combine(targetFolder, "ProofCalc", "Calculator.cs"),
                    string.Equals(modelId, BuilderProofFloorModelId, StringComparison.Ordinal)
                        ? """
                          namespace ProofCalc;

                          public static class Calculator
                          {
                              public static int Add(int left, int right)
                              {
                                  return left + right
                              }
                          }
                          """
                        : """
                          namespace ProofCalc;

                          public static class Calculator
                          {
                              public static int Add(int left, int right)
                              {
                                  return left + right;
                              }
                          }
                          """);
                return string.Equals(modelId, BuilderProofFloorModelId, StringComparison.Ordinal)
                    ? new BuilderProofGenerationRecord("generated_with_bounded_failure", "Inserted a bounded compile defect for floor-model recovery proof.")
                    : new BuilderProofGenerationRecord("generated_cleanly", "Filled the bounded calculator implementation cleanly.");

                    case "service_sample":
                File.WriteAllText(
                    Path.Combine(targetFolder, "Program.cs"),
                    """
                    var builder = WebApplication.CreateBuilder(args);
                    var app = builder.Build();

                    app.MapGet("/health", () => Results.Ok(new { status = "ok", target = "builder-proof" }));
                    app.MapGet("/version", () => Results.Ok(new { version = 1 }));

                    app.Run();

                    public partial class Program
                    {
                    }
                    """);
                return new BuilderProofGenerationRecord("generated_cleanly", "Filled the tiny service sample from the bounded template.");

                    default:
                        throw new InvalidOperationException($"Unsupported builder proof target type '{target.TargetType}'.");
                }
        }
    }

    private static void ApplyProofRecovery(BuilderProofTargetDefinition target, string modelId, string targetFolder)
    {
        if (!AllowsDeterministicRecovery(modelId, target))
        {
            return;
        }

        if (string.Equals(target.TemplateVariant, "external_test_hints", StringComparison.Ordinal))
        {
            File.WriteAllText(
                Path.Combine(targetFolder, "ExternalCalc", "Calculator.cs"),
                """
                namespace ExternalCalc;

                public static class Calculator
                {
                    public static int Multiply(int left, int right)
                    {
                        return left * right;
                    }
                }
                """);
            return;
        }

        if (string.Equals(target.TemplateVariant, "test_extension", StringComparison.Ordinal))
        {
            File.WriteAllText(
                Path.Combine(targetFolder, "ExtensionCalc.Tests", "CalculatorExtensionTests.cs"),
                """
                using ExtensionCalc;
                using Xunit;

                namespace ExtensionCalc.Tests;

                public sealed class CalculatorExtensionTests
                {
                    [Fact]
                    public void Subtract_returns_expected_difference()
                    {
                        Assert.Equal(3, Calculator.Subtract(5, 2));
                    }
                }
                """);
            return;
        }

        if (string.Equals(target.TargetType, "test_project", StringComparison.Ordinal))
        {
            File.WriteAllText(
                Path.Combine(targetFolder, "ProofCalc", "Calculator.cs"),
                """
                namespace ProofCalc;

                public static class Calculator
                {
                    public static int Add(int left, int right)
                    {
                        return left + right;
                    }
                }
                """);
        }
    }

    private static bool AllowsDeterministicRecovery(string modelId, BuilderProofTargetDefinition target)
        => string.Equals(modelId, BuilderProofFloorModelId, StringComparison.Ordinal) &&
           HasAssistRule(target, "bounded_compile_fix_loop");

    private static string ResolvePrimaryProjectPath(BuilderProofTargetDefinition target, string targetFolder)
    {
        if (string.Equals(target.TemplateVariant, "external_console", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "ExternalConsole.csproj");
        }

        if (string.Equals(target.TemplateVariant, "external_library", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "ExternalLibrary.csproj");
        }

        if (string.Equals(target.TemplateVariant, "external_test_hints", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "ExternalCalc.Tests", "ExternalCalc.Tests.csproj");
        }

        if (string.Equals(target.TemplateVariant, "external_multi_file_console", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "ExternalMultiConsole.csproj");
        }

        if (string.Equals(target.TemplateVariant, "external_related_library", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "ExternalRelatedLibrary.csproj");
        }

        if (string.Equals(target.TemplateVariant, "multi_file_console", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "MultiConsole.csproj");
        }

        if (string.Equals(target.TemplateVariant, "related_library_files", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "RelatedLibrary.csproj");
        }

        if (string.Equals(target.TemplateVariant, "service_feature_addition", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "ProofFeatureService.csproj");
        }

        if (string.Equals(target.TemplateVariant, "test_extension", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "ExtensionCalc.Tests", "ExtensionCalc.Tests.csproj");
        }

        if (string.Equals(target.TemplateVariant, "wpf_feature_addition", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "WpfProof.csproj");
        }

        if (string.Equals(target.TemplateVariant, "bounded_refactor", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "RefactorProof.csproj");
        }

        if (string.Equals(target.TemplateVariant, "bounded_refactor_split", StringComparison.Ordinal))
        {
            return Path.Combine(targetFolder, "RefactorProof.csproj");
        }

        return target.TargetType switch
        {
            "console_app" => Path.Combine(targetFolder, "ConsoleProof.csproj"),
            "class_library" => Path.Combine(targetFolder, "ProofLibrary.csproj"),
            "test_project" => Path.Combine(targetFolder, "ProofCalc.Tests", "ProofCalc.Tests.csproj"),
            "service_sample" => Path.Combine(targetFolder, "ProofService.csproj"),
            "wpf_app" => Path.Combine(targetFolder, "WpfProof.csproj"),
            _ => throw new InvalidOperationException($"Unsupported builder proof target type '{target.TargetType}'.")
        };
    }

    private static bool HasAssistRule(BuilderProofTargetDefinition target, string rule)
        => target.AllowedAssistRules?.Any(value => string.Equals(value, rule, StringComparison.Ordinal)) == true;

    private static BuilderProofFailure? BuildProofFailure(
        IReadOnlyList<BuilderProofStageRecord> stageResults,
        BuilderProofCommandExecutionResult buildCommand,
        BuilderProofCommandExecutionResult? testCommand,
        string targetLabel)
    {
        var failedStage = stageResults.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal));
        if (failedStage is null)
        {
            return null;
        }

        var outputLines = string.Equals(failedStage.StageId, "test", StringComparison.Ordinal)
            ? testCommand?.OutputLines ?? Array.Empty<string>()
            : buildCommand.OutputLines;
        var excerpt = outputLines
            .FirstOrDefault(static line => line.Contains(": error", StringComparison.OrdinalIgnoreCase) || line.Contains(" failed", StringComparison.OrdinalIgnoreCase))
            ?? outputLines.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? failedStage.Summary;

        return new BuilderProofFailure(
            failedStage.StageId,
            failedStage.StageLabel,
            targetLabel,
            excerpt.Trim(),
            failedStage.LogPath,
            failedStage.Summary,
            failedStage.ExitCode);
    }

    private static string DetermineProofCaseClassification(
        IReadOnlyList<BuilderProofStageRecord> stageResults,
        BuilderProofRecoveryRecord? recovery)
    {
        if (stageResults.All(stage => string.Equals(stage.Status, "passed", StringComparison.Ordinal)))
        {
            return "passed_cleanly";
        }

        if (recovery is not null && string.Equals(recovery.RecoveryState, "recovered", StringComparison.Ordinal))
        {
            return "recovered_with_guidance";
        }

        return "failed_after_followup";
    }

    private static string DetermineRepeatedFailureClassification(
        BuilderProofHistory priorHistory,
        string modelId,
        BuilderProofTargetDefinition target,
        string finalClassification,
        BuilderProofRecoveryRecord? recovery)
    {
        if (string.Equals(finalClassification, "passed_cleanly", StringComparison.Ordinal))
        {
            return "not_applicable";
        }

        if (string.Equals(finalClassification, "recovered_with_guidance", StringComparison.Ordinal) ||
            (recovery is not null && string.Equals(recovery.RecoveryState, "recovered", StringComparison.Ordinal)))
        {
            return "recoverable_with_guidance";
        }

        var matchingEntries = priorHistory.Entries
            .Where(entry =>
                string.Equals(entry.ModelId, modelId, StringComparison.Ordinal) &&
                string.Equals(entry.TargetId, target.TargetId, StringComparison.Ordinal) &&
                string.Equals(entry.TaskClass, target.TaskClass, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CompletedUtc)
            .ToArray();

        if (matchingEntries.Length >= 2 &&
            matchingEntries.Take(2).All(entry => string.Equals(entry.FinalClassification, "failed_after_followup", StringComparison.Ordinal)))
        {
            return "beyond_model_floor";
        }

        return matchingEntries.Length > 0 ? "unstable" : "beyond_model_floor";
    }

    private static BuilderModelFloorVerdict BuildModelFloorVerdict(
        string repoRoot,
        string runFolder,
        string modelId,
        BuilderProofMatrixDefinition matrix,
        IReadOnlyList<BuilderProofCaseResult> caseResults,
        BuilderProofHistory priorHistory)
    {
        var inScopeTaskClasses = new HashSet<string>(
            matrix.CapabilityClasses
                .Where(capability => capability.InScope)
                .Select(capability => capability.TaskClass),
            StringComparer.Ordinal);
        var inScopeCaseResults = caseResults
            .Where(result => inScopeTaskClasses.Contains(result.TaskClass))
            .ToArray();
        var boundaryCaseResults = caseResults
            .Where(result => !inScopeTaskClasses.Contains(result.TaskClass))
            .ToArray();
        var taskResults = matrix.CapabilityClasses
            .OrderBy(capability => capability.TaskClass, StringComparer.Ordinal)
            .Select(capability =>
            {
                var matchingCase = caseResults.FirstOrDefault(result => string.Equals(result.TaskClass, capability.TaskClass, StringComparison.Ordinal));
                var outcome = matchingCase is null
                    ? "not_exercised"
                    : !capability.InScope && string.Equals(matchingCase.FinalClassification, "failed_after_followup", StringComparison.Ordinal)
                        ? "routed_upward"
                    : string.Equals(matchingCase.FinalClassification, "passed_cleanly", StringComparison.Ordinal)
                        ? "passed"
                        : string.Equals(matchingCase.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal)
                            ? "recovered"
                            : "failed";
                var summary = matchingCase?.FinalSummary ?? "Task class was not exercised in the latest proof run.";
                return new BuilderModelFloorTaskResult(capability.TaskClass, matchingCase?.TargetId ?? string.Empty, outcome, summary);
            })
            .ToArray();

        var cleanCount = inScopeCaseResults.Count(result => string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal));
        var recoveredCount = inScopeCaseResults.Count(result => string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal));
        var failedCount = inScopeCaseResults.Count(result => string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal));
        var boundaryRoutingCount = boundaryCaseResults.Count(result => string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal));

        var verdict = failedCount == 0 && recoveredCount == 0
            ? "sufficient_for_bounded_builds"
            : failedCount == 0
                ? "sufficient_with_repair_loop"
                : cleanCount <= 2 && recoveredCount == 0
                    ? "suitable_only_for_edit_assist"
                    : "insufficient_for_target_scope";

        var reasons = new List<string>
        {
            $"Clean in-scope proof targets: {cleanCount}.",
            $"Recovered in-scope proof targets: {recoveredCount}.",
            $"Failed in-scope proof targets: {failedCount}."
        };
        reasons.Add($"Recovery burden states: clean={caseResults.Count(result => string.Equals(ClassifyRecoveryBurden(result), "clean", StringComparison.Ordinal))}, acceptable_with_repair_loop={caseResults.Count(result => string.Equals(ClassifyRecoveryBurden(result), "acceptable_with_repair_loop", StringComparison.Ordinal))}, too_fragile={caseResults.Count(result => string.Equals(ClassifyRecoveryBurden(result), "too_fragile", StringComparison.Ordinal))}.");
        if (boundaryRoutingCount > 0)
        {
            reasons.Add($"Boundary probes routed upward: {boundaryRoutingCount}.");
        }

        var repeatedBeyondFloor = caseResults.Count(result => string.Equals(result.RepeatedFailureClassification, "beyond_model_floor", StringComparison.Ordinal));
        if (repeatedBeyondFloor > 0)
        {
            reasons.Add($"Repeated failures beyond the floor scope were observed for {repeatedBeyondFloor} target(s).");
        }

        if (priorHistory.Entries.Count > 0)
        {
            reasons.Add($"Compared against {priorHistory.Entries.Count} prior proof history entr{(priorHistory.Entries.Count == 1 ? "y" : "ies")}.");
        }

        var summary = verdict switch
        {
            "sufficient_for_bounded_builds" when boundaryRoutingCount > 0 => "The configured model passed every in-scope proof target cleanly, and the boundary probes show where stronger-model routing should start.",
            "sufficient_for_bounded_builds" => "The configured model passed every bounded proof target cleanly.",
            "sufficient_with_repair_loop" when boundaryRoutingCount > 0 => "The configured model completed the in-scope proof matrix, but stronger-model routing is still recommended at the recorded boundary probes.",
            "sufficient_with_repair_loop" => "The configured model completed the bounded proof matrix, but at least one target required the guided repair loop.",
            "suitable_only_for_edit_assist" => "The configured model handled only a limited bounded subset cleanly and should stay in edit-assist scope.",
            _ => "The configured model did not complete enough bounded proof targets to support the requested target scope."
        };

        return new BuilderModelFloorVerdict(
            modelId,
            verdict,
            taskResults,
            reasons,
            runFolder,
            BuilderProofRunArtifactPath(runFolder),
            BuilderProofSummaryPath(runFolder),
            BuilderModelFloorVerdictPath(runFolder),
            BuilderModelFloorSummaryPath(runFolder),
            summary,
            DateTimeOffset.UtcNow);
    }

    private static BuilderExternalFloorVerdict BuildExternalFloorVerdict(
        string runFolder,
        string modelId,
        BuilderProofMatrixDefinition matrix,
        IReadOnlyList<BuilderProofCaseResult> caseResults,
        BuilderProofHistory priorHistory)
    {
        var taskResults = matrix.CapabilityClasses
            .OrderBy(capability => capability.TaskClass, StringComparer.Ordinal)
            .Select(capability =>
            {
                var matchingCase = caseResults.FirstOrDefault(result => string.Equals(result.TaskClass, capability.TaskClass, StringComparison.Ordinal));
                var outcome = matchingCase is null
                    ? "not_exercised"
                    : string.Equals(matchingCase.FinalClassification, "passed_cleanly", StringComparison.Ordinal)
                        ? "passed"
                        : string.Equals(matchingCase.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal)
                            ? "recovered"
                            : "failed";
                return new BuilderModelFloorTaskResult(capability.TaskClass, matchingCase?.TargetId ?? string.Empty, outcome, matchingCase?.FinalSummary ?? "Task class was not exercised in the external target pack.");
            })
            .ToArray();

        var cleanCount = caseResults.Count(result => string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal));
        var recoveredCount = caseResults.Count(result => string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal));
        var failedCount = caseResults.Count(result => string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal));
        var tooFragileCount = caseResults.Count(result => string.Equals(ClassifyRecoveryBurden(result), "too_fragile", StringComparison.Ordinal));

        var verdict = failedCount == 0 && recoveredCount == 0
            ? "sufficient_for_bounded_external_targets"
            : failedCount == 0
                ? "sufficient_with_repair_loop_only"
                : cleanCount + recoveredCount > 0
                    ? "sufficient_for_repo_local_only"
                    : "insufficient_for_external_target_scope";

        var reasons = new List<string>
        {
            $"Clean external targets: {cleanCount}.",
            $"Recovered external targets: {recoveredCount}.",
            $"Too-fragile external targets: {tooFragileCount}.",
            $"Compared against {priorHistory.Entries.Count} prior proof history entr{(priorHistory.Entries.Count == 1 ? "y" : "ies")}."
        };

        var summary = verdict switch
        {
            "sufficient_for_bounded_external_targets" => "The floor model handled the bounded external target pack cleanly under the tightened starter hints.",
            "sufficient_with_repair_loop_only" => "The floor model completed the bounded external target pack, but at least one external target still depended on the repair loop.",
            "sufficient_for_repo_local_only" => "The floor model remained acceptable for repo-local proof cases, but the external target pack still showed unresolved weakness.",
            _ => "The floor model did not complete the bounded external target pack reliably enough for this scope."
        };

        return new BuilderExternalFloorVerdict(
            modelId,
            verdict,
            taskResults,
            reasons,
            cleanCount,
            recoveredCount,
            tooFragileCount,
            summary,
            runFolder,
            BuilderExternalProofRunPath(runFolder),
            BuilderExternalProofSummaryPath(runFolder),
            BuilderExternalFloorVerdictPath(runFolder),
            BuilderExternalFloorSummaryPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderProofRun BuildProofRun(
        string repoRoot,
        string runFolder,
        string proofRunId,
        string modelId,
        string provider,
        BuilderProofMatrixDefinition matrix,
        IReadOnlyList<BuilderProofCaseResult> caseResults,
        BuilderModelFloorVerdict verdict,
        string verdictSummary,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc)
    {
        var buildPassCount = caseResults.Count(result => string.Equals(result.BuildResult, "passed", StringComparison.Ordinal));
        var testPassCount = caseResults.Count(result => string.Equals(result.TestResult, "passed", StringComparison.Ordinal));
        var recoveryRequiredCount = caseResults.Count(result => result.RecoveryRequired);
        var inScopeTaskClasses = new HashSet<string>(
            matrix.CapabilityClasses
                .Where(capability => capability.InScope)
                .Select(capability => capability.TaskClass),
            StringComparer.Ordinal);
        var inScopeFailureCount = caseResults.Count(result =>
            inScopeTaskClasses.Contains(result.TaskClass) &&
            string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal));
        var boundaryFailureCount = caseResults.Count(result =>
            !inScopeTaskClasses.Contains(result.TaskClass) &&
            string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal));
        var finalClassification = inScopeFailureCount > 0
            ? "failed"
            : boundaryFailureCount > 0
                ? "passed_with_routing"
                : recoveryRequiredCount > 0
                    ? "passed_with_recovery"
                    : "passed_cleanly";

        return new BuilderProofRun(
            proofRunId,
            repoRoot,
            runFolder,
            modelId,
            provider,
            matrix.Targets.Select(target => target.TargetLabel).ToArray(),
            caseResults,
            buildPassCount,
            testPassCount,
            recoveryRequiredCount,
            finalClassification,
            verdict.Verdict,
            verdictSummary,
            BuilderProofMatrixArtifactPath(runFolder),
            BuilderProofRunArtifactPath(runFolder),
            BuilderProofSummaryPath(runFolder),
            BuilderModelFloorVerdictPath(runFolder),
            BuilderModelFloorSummaryPath(runFolder),
            startedUtc,
            completedUtc);
    }

    private static BuilderExternalProofRun BuildExternalProofRun(
        string repoRoot,
        string runFolder,
        string proofRunId,
        string modelId,
        string provider,
        BuilderProofMatrixDefinition matrix,
        IReadOnlyList<BuilderProofCaseResult> caseResults,
        BuilderExternalFloorVerdict verdict,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc)
    {
        var cleanSuccessCount = caseResults.Count(result => string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal));
        var recoveryRequiredCount = caseResults.Count(result => string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal));
        var tooFragileCount = caseResults.Count(result => string.Equals(ClassifyRecoveryBurden(result), "too_fragile", StringComparison.Ordinal));
        var finalClassification = caseResults.All(result => !string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal))
            ? (recoveryRequiredCount > 0 ? "passed_with_recovery" : "passed_cleanly")
            : "failed";

        return new BuilderExternalProofRun(
            proofRunId,
            repoRoot,
            runFolder,
            Path.Combine(runFolder, "external-target-pack"),
            modelId,
            provider,
            matrix,
            caseResults,
            cleanSuccessCount,
            recoveryRequiredCount,
            tooFragileCount,
            finalClassification,
            verdict.Verdict,
            verdict.Summary,
            BuilderExternalProofRunPath(runFolder),
            BuilderExternalProofSummaryPath(runFolder),
            BuilderExternalFloorVerdictPath(runFolder),
            BuilderExternalFloorSummaryPath(runFolder),
            startedUtc,
            completedUtc);
    }

    private static BuilderProofFailurePatternSummary BuildBuilderProofFailurePatternSummary(
        string runFolder,
        string modelId,
        IReadOnlyList<BuilderProofCaseResult> repoLocalCases,
        IReadOnlyList<BuilderProofCaseResult> externalCases)
    {
        var entries = BuildFailurePatternEntries("repo_local", repoLocalCases)
            .Concat(BuildFailurePatternEntries("external_target_pack", externalCases))
            .OrderBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .ToArray();

        var categories = entries
            .GroupBy(entry => entry.FailureCategory, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new BuilderProofFailurePatternAggregate(
                group.Key,
                group.Count(),
                group.Select(entry => entry.TargetId).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                BuildFailurePatternCategorySummary(group.Key, group.Count())))
            .ToArray();

        var summary = entries.Length == 0
            ? "No low-floor failure patterns were recorded in the latest builder proof run."
            : $"Observed {entries.Length} low-floor stumble pattern(s): {string.Join(", ", categories.Select(category => $"{category.Category}={category.Count}"))}.";

        return new BuilderProofFailurePatternSummary(
            modelId,
            categories,
            entries,
            summary,
            BuilderModelFloorFailurePatternsPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static IEnumerable<BuilderProofFailurePatternEntry> BuildFailurePatternEntries(
        string proofScope,
        IReadOnlyList<BuilderProofCaseResult> caseResults)
    {
        foreach (var result in caseResults)
        {
            var failedStage = result.StageResults.FirstOrDefault(stage => string.Equals(stage.Status, "failed", StringComparison.Ordinal));
            if (failedStage is null)
            {
                continue;
            }

            var errorExcerpt = ExtractFailureExcerpt(failedStage.LogPath, failedStage.Summary);
            var category = ClassifyFailurePattern(result, failedStage.Summary, errorExcerpt);
            yield return new BuilderProofFailurePatternEntry(
                proofScope,
                result.TargetId,
                result.TargetLabel,
                category,
                BuildFailurePatternReason(category),
                ClassifyRecoveryBurden(result),
                result.RecoveryRequired ? 1 : 0,
                result.RecoveryRequired ? 1 : 0,
                failedStage.StageId,
                errorExcerpt,
                failedStage.LogPath,
                result.ValidationResultPath,
                result.FollowupPlanPath);
        }
    }

    private static BuilderModelFloorPolicy BuildBuilderModelFloorPolicy(
        string runFolder,
        string modelId,
        BuilderModelFloorVerdict repoLocalVerdict,
        BuilderExternalFloorVerdict externalVerdict,
        BuilderProofMatrixDefinition repoLocalMatrix,
        IReadOnlyList<BuilderProofCaseResult> repoLocalCases,
        BuilderProofMatrixDefinition externalMatrix,
        IReadOnlyList<BuilderProofCaseResult> externalCases,
        BuilderProofFailurePatternSummary failurePatterns)
    {
        var allCases = repoLocalCases.Concat(externalCases).ToArray();
        var cleanCount = allCases.Count(result => string.Equals(ClassifyRecoveryBurden(result), "clean", StringComparison.Ordinal));
        var acceptableRecoveryCount = allCases.Count(result => string.Equals(ClassifyRecoveryBurden(result), "acceptable_with_repair_loop", StringComparison.Ordinal));
        var tooFragileCount = allCases.Count(result => string.Equals(ClassifyRecoveryBurden(result), "too_fragile", StringComparison.Ordinal));
        var inScopeCleanTaskClasses = repoLocalCases
            .Where(result => IsTaskInScope(repoLocalMatrix, result.TaskClass) && string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal))
            .Concat(externalCases.Where(result => IsTaskInScope(externalMatrix, result.TaskClass) && string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal)))
            .Select(result => result.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var repairTaskClasses = allCases
            .Where(result => string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal))
            .Select(result => result.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var boundaryTaskClasses = repoLocalCases
            .Where(result => !IsTaskInScope(repoLocalMatrix, result.TaskClass) && string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal))
            .Select(result => result.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var guidance = new List<string>();
        if (string.Equals(externalVerdict.Verdict, "sufficient_for_bounded_external_targets", StringComparison.Ordinal))
        {
            guidance.Add("Good for tiny template-driven console, class-library, service, UI, and external starter targets when explicit file and namespace hints are present.");
        }
        else if (string.Equals(externalVerdict.Verdict, "sufficient_with_repair_loop_only", StringComparison.Ordinal))
        {
            guidance.Add("External targets remain acceptable only when the bounded repair loop stays enabled.");
        }
        else if (string.Equals(externalVerdict.Verdict, "sufficient_for_repo_local_only", StringComparison.Ordinal))
        {
            guidance.Add("Keep the floor model inside repo-local proof cases until the external target pack stops showing unresolved weakness.");
        }
        else
        {
            guidance.Add("Do not treat the floor model as reliable for external target generation in the current proof scope.");
        }

        if (inScopeCleanTaskClasses.Any(taskClass => string.Equals(taskClass, "multi_file_console_feature", StringComparison.Ordinal) ||
                                                    string.Equals(taskClass, "library_related_files", StringComparison.Ordinal) ||
                                                    string.Equals(taskClass, "service_feature_addition", StringComparison.Ordinal) ||
                                                    string.Equals(taskClass, "ui_feature_addition", StringComparison.Ordinal)))
        {
            guidance.Add("Small multi-file additions stay acceptable when the task remains inside the recorded project and file list.");
        }

        if (acceptableRecoveryCount > 0)
        {
            guidance.Add("Keep bounded compile-fix recovery available for isolated compile-fix and test-extension tasks.");
        }

        if (tooFragileCount > 0)
        {
            guidance.Add("Escalate to a stronger model when a proof target stays too fragile after the bounded repair loop.");
        }

        if (boundaryTaskClasses.Length > 0)
        {
            guidance.Add($"Route {string.Join(", ", boundaryTaskClasses)} work to a stronger model instead of repeating low-floor attempts.");
        }

        if (failurePatterns.Categories.Any(category => string.Equals(category.Category, "namespace_import_omission", StringComparison.Ordinal)))
        {
            guidance.Add("Prompt scaffolds should keep namespaces and usings explicit when extending existing tests or moved files.");
        }

        var summary = boundaryTaskClasses.Length > 0
            ? "The floor model remains acceptable for bounded template-driven builds and small multi-file additions, but bounded refactor-style probes should be routed to a stronger model."
            : string.Equals(externalVerdict.Verdict, "sufficient_for_bounded_external_targets", StringComparison.Ordinal)
                ? "The floor model is acceptable for tiny template-driven builds, including the bounded external target pack, when explicit file and namespace hints are present."
                : string.Equals(externalVerdict.Verdict, "sufficient_with_repair_loop_only", StringComparison.Ordinal)
                    ? "The floor model remains usable for tiny builds, but external targets still depend on the bounded repair loop."
                    : string.Equals(externalVerdict.Verdict, "sufficient_for_repo_local_only", StringComparison.Ordinal)
                        ? "The floor model is still safest in repo-local proof cases and should not be trusted for external targets without extra review."
                        : "The floor model should stay in edit-assist scope only for the current external target pack.";

        return new BuilderModelFloorPolicy(
            modelId,
            repoLocalVerdict.Verdict,
            externalVerdict.Verdict,
            summary,
            guidance,
            new[] { "stronger_starter_templates", "explicit_file_and_namespace_hints", "bounded_compile_fix_loop" },
            new[]
            {
                BuilderProofRunArtifactPath(runFolder),
                BuilderModelFloorVerdictPath(runFolder),
                BuilderExternalProofRunPath(runFolder),
                BuilderExternalFloorVerdictPath(runFolder),
                failurePatterns.ArtifactPath
            },
            BuilderModelFloorPolicyPath(runFolder),
            BuilderModelFloorPolicySummaryPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static string BuildOverallBuilderProofSummary(
        BuilderModelFloorVerdict repoLocalVerdict,
        BuilderExternalFloorVerdict externalVerdict,
        BuilderModelFloorPolicy policy,
        BuilderModelTrustBands trustBands,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan)
        => $"Repo-local proof verdict: {repoLocalVerdict.Verdict}. External target verdict: {externalVerdict.Verdict}. {policy.Summary} {trustBands.Summary} {routingRecommendation.Summary} {escalationDecision.Summary} {routingPlan.Summary}";

    private static string BuildProofSummaryMarkdown(
        BuilderProofRun run,
        BuilderExternalProofRun externalRun,
        BuilderProofFailurePatternSummary failurePatterns,
        BuilderModelFloorPolicy policy,
        BuilderModelTrustBands trustBands,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Proof Summary");
        builder.AppendLine();
        builder.AppendLine($"- Run ID: `{run.ProofRunId}`");
        builder.AppendLine($"- Model: `{run.ModelId}`");
        builder.AppendLine($"- Provider: `{run.Provider}`");
        builder.AppendLine($"- Repo-local classification: `{run.FinalClassification}`");
        builder.AppendLine($"- Repo-local floor verdict: `{run.ModelFloorVerdict}`");
        builder.AppendLine($"- External classification: `{externalRun.FinalClassification}`");
        builder.AppendLine($"- External floor verdict: `{externalRun.Verdict}`");
        builder.AppendLine($"- Clean-band count: `{trustBands.CleanBuildBandCount}`");
        builder.AppendLine($"- Repair-loop-band count: `{trustBands.RepairLoopBandCount}`");
        builder.AppendLine($"- Escalation-band count: `{trustBands.EscalationRecommendedBandCount}`");
        builder.AppendLine($"- Reject-band count: `{trustBands.RejectBandCount}`");
        builder.AppendLine();
        builder.AppendLine("## Repo-Local Targets");
        foreach (var result in run.CaseResults.OrderBy(result => result.TargetId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {result.TargetLabel}: {result.FinalClassification} ({result.GenerationOutcome}; build={result.BuildResult}; test={result.TestResult}; files={result.ComplexityDimensions.FileCountTouched}; band={result.TrustBand}; routing={result.RoutingRecommendationState})");
        }
        builder.AppendLine();
        builder.AppendLine("## External Targets");
        foreach (var result in externalRun.CaseResults.OrderBy(result => result.TargetId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {result.TargetLabel}: {result.FinalClassification} ({result.GenerationOutcome}; build={result.BuildResult}; test={result.TestResult}; files={result.ComplexityDimensions.FileCountTouched}; band={result.TrustBand}; routing={result.RoutingRecommendationState})");
        }
        builder.AppendLine();
        builder.AppendLine("## Failure Patterns");
        builder.AppendLine($"- {failurePatterns.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Trust Bands");
        builder.AppendLine($"- {trustBands.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Routing Recommendation");
        builder.AppendLine($"- {routingRecommendation.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Escalation Decision");
        builder.AppendLine($"- {escalationDecision.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Routing Plan");
        builder.AppendLine($"- {routingPlan.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Floor Policy");
        builder.AppendLine($"- {policy.Summary}");

        return builder.ToString().TrimEnd();
    }

    private static string BuildModelFloorSummaryMarkdown(BuilderModelFloorVerdict verdict)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Model Floor Verdict");
        builder.AppendLine();
        builder.AppendLine($"- Model: `{verdict.ModelId}`");
        builder.AppendLine($"- Verdict: `{verdict.Verdict}`");
        builder.AppendLine($"- Summary: {verdict.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Task Classes");
        foreach (var taskResult in verdict.TaskResults.OrderBy(result => result.TaskClass, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {taskResult.TaskClass}: {taskResult.Outcome}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildExternalProofSummaryMarkdown(BuilderExternalProofRun run)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder External Proof Summary");
        builder.AppendLine();
        builder.AppendLine($"- Run ID: `{run.ProofRunId}`");
        builder.AppendLine($"- Model: `{run.ModelId}`");
        builder.AppendLine($"- Provider: `{run.Provider}`");
        builder.AppendLine($"- Final classification: `{run.FinalClassification}`");
        builder.AppendLine($"- External verdict: `{run.Verdict}`");
        builder.AppendLine($"- Clean success count: `{run.CleanSuccessCount}`");
        builder.AppendLine($"- Recovery required count: `{run.RecoveryRequiredCount}`");
        builder.AppendLine($"- Too-fragile count: `{run.TooFragileCount}`");
        builder.AppendLine();
        builder.AppendLine("## External Targets");
        foreach (var result in run.CaseResults.OrderBy(result => result.TargetId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {result.TargetLabel}: {result.FinalClassification} ({result.GenerationOutcome}; build={result.BuildResult}; test={result.TestResult})");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildExternalFloorSummaryMarkdown(BuilderExternalFloorVerdict verdict)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder External Floor Verdict");
        builder.AppendLine();
        builder.AppendLine($"- Model: `{verdict.ModelId}`");
        builder.AppendLine($"- Verdict: `{verdict.Verdict}`");
        builder.AppendLine($"- Summary: {verdict.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Task Classes");
        foreach (var taskResult in verdict.TaskResults.OrderBy(result => result.TaskClass, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {taskResult.TaskClass}: {taskResult.Outcome}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildBuilderModelFloorPolicyMarkdown(BuilderModelFloorPolicy policy)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Model Floor Policy");
        builder.AppendLine();
        builder.AppendLine($"- Model: `{policy.ModelId}`");
        builder.AppendLine($"- Repo-local verdict: `{policy.RepoLocalVerdict}`");
        builder.AppendLine($"- External verdict: `{policy.ExternalVerdict}`");
        builder.AppendLine($"- Summary: {policy.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Guidance");
        foreach (var line in policy.Guidance)
        {
            builder.AppendLine($"- {line}");
        }

        return builder.ToString().TrimEnd();
    }

    private static BuilderModelTrustBands BuildBuilderModelTrustBands(
        string runFolder,
        string modelId,
        BuilderModelFloorVerdict repoLocalVerdict,
        BuilderExternalFloorVerdict externalVerdict,
        BuilderProofMatrixDefinition repoLocalMatrix,
        IReadOnlyList<BuilderProofCaseResult> repoLocalCases,
        BuilderProofMatrixDefinition externalMatrix,
        IReadOnlyList<BuilderProofCaseResult> externalCases,
        BuilderProofFailurePatternSummary failurePatterns)
    {
        var entries = repoLocalCases
            .Select(result => BuildBuilderModelTrustBandEntry(runFolder, repoLocalMatrix, result))
            .Concat(externalCases.Select(result => BuildBuilderModelTrustBandEntry(runFolder, externalMatrix, result)))
            .OrderBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .ToArray();

        var weakSpots = failurePatterns.Categories
            .OrderBy(category => category.Category, StringComparer.Ordinal)
            .Select(category =>
            {
                var relatedEntries = entries
                    .Where(entry => category.TargetIds.Contains(entry.TargetId, StringComparer.Ordinal))
                    .ToArray();
                var classification = ClassifyWeakSpot(category.Category, relatedEntries);
                var summary = $"{BuildWeakSpotLabel(category.Category)} appeared {category.Count} time(s) and is currently classified as {classification}.";
                return new BuilderModelWeakSpotSummary(category.Category, category.Count, classification, category.TargetIds, summary);
            })
            .ToArray();

        var cleanCount = entries.Count(entry => string.Equals(entry.TrustBand, "clean_build_band", StringComparison.Ordinal));
        var repairLoopCount = entries.Count(entry => string.Equals(entry.TrustBand, "repair_loop_band", StringComparison.Ordinal));
        var escalationCount = entries.Count(entry => string.Equals(entry.TrustBand, "escalation_recommended_band", StringComparison.Ordinal));
        var rejectCount = entries.Count(entry => string.Equals(entry.TrustBand, "reject_band", StringComparison.Ordinal));
        var summary = $"Clean band={cleanCount}. Repair-loop band={repairLoopCount}. Escalation recommended={escalationCount}. Reject={rejectCount}.";

        return new BuilderModelTrustBands(
            modelId,
            repoLocalVerdict.Verdict,
            externalVerdict.Verdict,
            entries,
            weakSpots,
            cleanCount,
            repairLoopCount,
            escalationCount,
            rejectCount,
            summary,
            BuilderModelTrustBandsPath(runFolder),
            BuilderModelScopeSummaryPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelTrustBandEntry BuildBuilderModelTrustBandEntry(
        string runFolder,
        BuilderProofMatrixDefinition matrix,
        BuilderProofCaseResult result)
    {
        var capability = matrix.CapabilityClasses.FirstOrDefault(value => string.Equals(value.TaskClass, result.TaskClass, StringComparison.Ordinal))
            ?? new BuilderProofCapabilityClass(result.TaskClass, "No capability description recorded.", true, "No notes recorded.");
        var recommendation = DetermineRoutingRecommendationState(capability, result);
        var trustBand = DetermineTrustBand(recommendation);
        var reasons = BuildRoutingReasons(capability, result);
        var evidencePaths = new[]
        {
            BuilderProofRunArtifactPath(runFolder),
            result.ValidationResultPath,
            result.FollowupPlanPath,
            result.RecoveryValidationResultPath,
            result.FollowupExecutionOutcomePath
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToArray();

        return new BuilderModelTrustBandEntry(
            result.ProofScope,
            result.TargetId,
            result.TargetLabel,
            result.TaskClass,
            result.ComplexityDimensions,
            result.FinalClassification,
            ClassifyRecoveryBurden(result),
            trustBand,
            recommendation,
            reasons,
            evidencePaths);
    }

    private static BuilderModelRoutingRecommendation BuildBuilderModelRoutingRecommendation(
        string runFolder,
        string modelId,
        BuilderProofMatrixDefinition repoLocalMatrix,
        IReadOnlyList<BuilderProofCaseResult> repoLocalCases,
        BuilderProofMatrixDefinition externalMatrix,
        IReadOnlyList<BuilderProofCaseResult> externalCases,
        BuilderModelTrustBands trustBands)
    {
        var entries = trustBands.Entries
            .Select(entry =>
            {
                var sourceCase = repoLocalCases.Concat(externalCases)
                    .First(result => string.Equals(result.TargetId, entry.TargetId, StringComparison.Ordinal) &&
                                     string.Equals(result.ProofScope, entry.ProofScope, StringComparison.Ordinal));
                return new BuilderModelRoutingRecommendationEntry(
                    entry.ProofScope,
                    entry.TargetId,
                    entry.TargetLabel,
                    entry.TaskClass,
                    entry.ComplexityDimensions,
                    entry.TrustBand,
                    entry.RecommendationState,
                    entry.Reasons,
                    BuilderProofRunArtifactPath(runFolder),
                    sourceCase.ValidationResultPath,
                    sourceCase.FollowupPlanPath);
            })
            .OrderBy(entry => GetRoutingSeverity(entry.RecommendationState))
            .ThenBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .ToArray();

        var featured = entries.FirstOrDefault();
        var summary = featured is null
            ? "No builder routing recommendation was recorded."
            : BuildRoutingSummary(featured);

        return new BuilderModelRoutingRecommendation(
            modelId,
            featured?.RecommendationState ?? "proceed_with_current_model",
            featured?.ProofScope ?? string.Empty,
            featured?.TargetId ?? string.Empty,
            featured?.TargetLabel ?? string.Empty,
            featured?.TaskClass ?? string.Empty,
            featured?.ComplexityDimensions ?? new BuilderProofComplexityDimensions(),
            featured?.TrustBand ?? "clean_build_band",
            featured?.Reasons ?? Array.Empty<string>(),
            entries,
            summary,
            BuilderModelRoutingRecommendationPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelEscalationDecision BuildBuilderModelEscalationDecision(
        string runFolder,
        string modelId,
        BuilderProofMatrixDefinition repoLocalMatrix,
        IReadOnlyList<BuilderProofCaseResult> repoLocalCases,
        BuilderProofMatrixDefinition externalMatrix,
        IReadOnlyList<BuilderProofCaseResult> externalCases,
        BuilderModelTrustBands trustBands,
        BuilderModelRoutingRecommendation routingRecommendation)
    {
        var featured = routingRecommendation.Entries.FirstOrDefault();
        if (featured is null)
        {
            var emptyHook = new BuilderModelComparativeProofHook(
                string.Empty,
                string.Empty,
                modelId,
                "clean_build_band",
                "current_floor_model",
                string.Empty,
                string.Empty,
                BuilderProofRunArtifactPath(runFolder),
                string.Empty,
                "No comparative proof hook is required.");
            return new BuilderModelEscalationDecision(
                modelId,
                modelId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new BuilderProofComplexityDimensions(),
                "clean_build_band",
                "proceed_with_current_model",
                "stay_on_current_model",
                "not_needed",
                "current_floor_model",
                "No escalation reason was recorded.",
                string.Empty,
                "No linked weak-spot reason recorded.",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                emptyHook,
                "No builder escalation decision was recorded.",
                BuilderModelEscalationDecisionPath(runFolder),
                DateTimeOffset.UtcNow);
        }

        var sourceCase = repoLocalCases.Concat(externalCases)
            .First(result => string.Equals(result.TargetId, featured.TargetId, StringComparison.Ordinal) &&
                             string.Equals(result.ProofScope, featured.ProofScope, StringComparison.Ordinal));
        var capability = ResolveCapabilityClass(featured.ProofScope, featured.TaskClass, repoLocalMatrix, externalMatrix);
        var weakSpot = ResolvePrimaryWeakSpot(trustBands, featured.TargetId);
        var escalationRequirementState = DetermineEscalationRequirementState(featured, sourceCase, weakSpot);
        var splitTaskRecommendationState = DetermineSplitTaskRecommendationState(featured, weakSpot, escalationRequirementState);
        var recommendedModelClass = DetermineRecommendedModelClass(escalationRequirementState);
        var reasons = BuildEscalationReasons(featured, sourceCase, weakSpot, escalationRequirementState, splitTaskRecommendationState);
        var splitTaskGuidance = BuildSplitTaskGuidance(featured, capability, weakSpot, splitTaskRecommendationState);
        var comparativeProofHook = BuildComparativeProofHook(runFolder, modelId, featured, recommendedModelClass);
        var linkedProofEvidencePaths = new[]
        {
            BuilderProofRunArtifactPath(runFolder),
            sourceCase.ValidationResultPath,
            sourceCase.FollowupPlanPath,
            sourceCase.RecoveryValidationResultPath,
            sourceCase.FollowupExecutionOutcomePath
        }.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var reasonForEscalation = reasons.FirstOrDefault() ?? "No escalation reason was recorded.";
        var summary = BuildEscalationSummary(featured, escalationRequirementState, splitTaskRecommendationState, recommendedModelClass, weakSpot);

        return new BuilderModelEscalationDecision(
            modelId,
            modelId,
            featured.ProofScope,
            featured.TargetId,
            featured.TargetLabel,
            featured.TaskClass,
            featured.ComplexityDimensions,
            featured.TrustBand,
            featured.RecommendationState,
            escalationRequirementState,
            splitTaskRecommendationState,
            recommendedModelClass,
            reasonForEscalation,
            weakSpot?.WeakSpot ?? string.Empty,
            weakSpot?.Summary ?? "No linked weak-spot reason recorded.",
            reasons,
            splitTaskGuidance,
            linkedProofEvidencePaths,
            comparativeProofHook,
            summary,
            BuilderModelEscalationDecisionPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelRoutingPlan BuildBuilderModelRoutingPlan(
        string runFolder,
        string modelId,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelTrustBands trustBands)
    {
        var comparativeProofHook = escalationDecision.ComparativeProofHook;
        var evidencePaths = escalationDecision.LinkedProofEvidencePaths
            .Concat(new[] { comparativeProofHook.CurrentProofRunArtifactPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var weakSpot = ResolvePrimaryWeakSpot(trustBands, escalationDecision.TargetId);
        var summary = BuildRoutingPlanSummary(escalationDecision, weakSpot);

        return new BuilderModelRoutingPlan(
            modelId,
            escalationDecision.CurrentModelId,
            escalationDecision.ProofScope,
            escalationDecision.TargetId,
            escalationDecision.TargetLabel,
            escalationDecision.TaskClass,
            escalationDecision.TrustBand,
            routingRecommendation.RecommendationState,
            escalationDecision.EscalationRequirementState,
            escalationDecision.CurrentModelId,
            escalationDecision.RecommendedModelClass,
            escalationDecision.SplitTaskRecommendationState,
            escalationDecision.ReasonForEscalation,
            escalationDecision.SplitTaskGuidance,
            escalationDecision.PrimaryWeakSpot,
            escalationDecision.PrimaryWeakSpotSummary,
            escalationDecision.Reasons,
            evidencePaths,
            comparativeProofHook,
            summary,
            BuilderModelRoutingPlanPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static string BuildBuilderModelScopeSummaryMarkdown(BuilderModelTrustBands trustBands)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Model Scope Summary");
        builder.AppendLine();
        builder.AppendLine($"- Model: `{trustBands.ModelId}`");
        builder.AppendLine($"- Repo-local verdict: `{trustBands.RepoLocalVerdict}`");
        builder.AppendLine($"- External verdict: `{trustBands.ExternalVerdict}`");
        builder.AppendLine($"- Summary: {trustBands.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Supported Clean Tasks");
        foreach (var entry in trustBands.Entries.Where(entry => string.Equals(entry.TrustBand, "clean_build_band", StringComparison.Ordinal)).OrderBy(entry => entry.TargetId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {entry.TargetLabel} ({entry.TaskClass})");
        }

        builder.AppendLine();
        builder.AppendLine("## Repair-Assisted Tasks");
        foreach (var entry in trustBands.Entries.Where(entry => string.Equals(entry.TrustBand, "repair_loop_band", StringComparison.Ordinal)).OrderBy(entry => entry.TargetId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {entry.TargetLabel} ({entry.TaskClass})");
        }

        builder.AppendLine();
        builder.AppendLine("## Escalation-Recommended Tasks");
        foreach (var entry in trustBands.Entries.Where(entry => string.Equals(entry.TrustBand, "escalation_recommended_band", StringComparison.Ordinal)).OrderBy(entry => entry.TargetId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {entry.TargetLabel} ({entry.TaskClass})");
        }

        builder.AppendLine();
        builder.AppendLine("## Declined Floor Tasks");
        foreach (var entry in trustBands.Entries.Where(entry => string.Equals(entry.TrustBand, "reject_band", StringComparison.Ordinal)).OrderBy(entry => entry.TargetId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {entry.TargetLabel} ({entry.TaskClass})");
        }

        if (trustBands.WeakSpots.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Weak Spots");
            foreach (var weakSpot in trustBands.WeakSpots.OrderBy(value => value.WeakSpot, StringComparer.Ordinal))
            {
                builder.AppendLine($"- {weakSpot.WeakSpot}: {weakSpot.Classification}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<BuilderProofCaseResult> ApplyTrustBandMetadata(
        IReadOnlyList<BuilderProofCaseResult> caseResults,
        BuilderModelTrustBands trustBands)
    {
        var lookup = trustBands.Entries.ToDictionary(
            entry => $"{entry.ProofScope}|{entry.TargetId}",
            entry => entry,
            StringComparer.Ordinal);

        return caseResults
            .Select(result =>
            {
                if (!lookup.TryGetValue($"{result.ProofScope}|{result.TargetId}", out var entry))
                {
                    return result;
                }

                return result with
                {
                    TrustBand = entry.TrustBand,
                    RoutingRecommendationState = entry.RecommendationState
                };
            })
            .ToArray();
    }

    private static bool IsTaskInScope(BuilderProofMatrixDefinition matrix, string taskClass)
        => matrix.CapabilityClasses.FirstOrDefault(value => string.Equals(value.TaskClass, taskClass, StringComparison.Ordinal))?.InScope ?? true;

    private static string DetermineRoutingRecommendationState(BuilderProofCapabilityClass capability, BuilderProofCaseResult result)
    {
        if (!capability.InScope && string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal))
        {
            return "task_out_of_scope_for_floor";
        }

        if (string.Equals(result.FinalClassification, "failed_after_followup", StringComparison.Ordinal))
        {
            return string.Equals(result.RepeatedFailureClassification, "beyond_model_floor", StringComparison.Ordinal)
                ? "task_out_of_scope_for_floor"
                : "stronger_model_recommended";
        }

        if (string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal))
        {
            return "proceed_with_repair_loop_expected";
        }

        if (!capability.InScope)
        {
            return "stronger_model_recommended";
        }

        if (result.ComplexityDimensions.ProjectCountTouched > 1 ||
            result.ComplexityDimensions.DependencyReferenceChangeCount > 0 ||
            string.Equals(result.ComplexityDimensions.PromptAmbiguity, "high", StringComparison.OrdinalIgnoreCase))
        {
            return "stronger_model_recommended";
        }

        return "proceed_with_current_model";
    }

    private static string DetermineTrustBand(string recommendationState)
        => recommendationState switch
        {
            "proceed_with_current_model" => "clean_build_band",
            "proceed_with_repair_loop_expected" => "repair_loop_band",
            "stronger_model_recommended" => "escalation_recommended_band",
            "task_out_of_scope_for_floor" => "reject_band",
            _ => "clean_build_band"
        };

    private static BuilderProofCapabilityClass ResolveCapabilityClass(
        string proofScope,
        string taskClass,
        BuilderProofMatrixDefinition repoLocalMatrix,
        BuilderProofMatrixDefinition externalMatrix)
    {
        var matrix = string.Equals(proofScope, "external_target_pack", StringComparison.Ordinal)
            ? externalMatrix
            : repoLocalMatrix;
        return matrix.CapabilityClasses.FirstOrDefault(value => string.Equals(value.TaskClass, taskClass, StringComparison.Ordinal))
            ?? new BuilderProofCapabilityClass(taskClass, "No capability description recorded.", true, "No capability notes recorded.");
    }

    private static BuilderModelWeakSpotSummary? ResolvePrimaryWeakSpot(BuilderModelTrustBands trustBands, string targetId)
        => trustBands.WeakSpots
            .Where(weakSpot => weakSpot.TargetIds.Contains(targetId, StringComparer.Ordinal))
            .OrderBy(weakSpot => string.Equals(weakSpot.Classification, "boundary_of_model_floor", StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(weakSpot => weakSpot.OccurrenceCount)
            .ThenBy(weakSpot => weakSpot.WeakSpot, StringComparer.Ordinal)
            .FirstOrDefault();

    private static string DetermineEscalationRequirementState(
        BuilderModelRoutingRecommendationEntry entry,
        BuilderProofCaseResult sourceCase,
        BuilderModelWeakSpotSummary? weakSpot)
    {
        if (string.Equals(entry.RecommendationState, "proceed_with_current_model", StringComparison.Ordinal))
        {
            return "stay_on_current_model";
        }

        if (string.Equals(entry.RecommendationState, "proceed_with_repair_loop_expected", StringComparison.Ordinal))
        {
            return "current_model_with_repair_loop";
        }

        var shouldSplit = entry.ComplexityDimensions.FileCountTouched >= 3 ||
                          string.Equals(entry.ComplexityDimensions.PromptAmbiguity, "high", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(weakSpot?.WeakSpot, "file_placement_mistake", StringComparison.Ordinal);

        if (string.Equals(entry.RecommendationState, "task_out_of_scope_for_floor", StringComparison.Ordinal))
        {
            return shouldSplit ? "task_should_be_split_first" : "stronger_model_required";
        }

        if (string.Equals(sourceCase.RepeatedFailureClassification, "beyond_model_floor", StringComparison.Ordinal) ||
            entry.ComplexityDimensions.ProjectCountTouched > 1 ||
            entry.ComplexityDimensions.DependencyReferenceChangeCount > 0)
        {
            return "stronger_model_required";
        }

        return shouldSplit ? "task_should_be_split_first" : "stronger_model_recommended";
    }

    private static string DetermineSplitTaskRecommendationState(
        BuilderModelRoutingRecommendationEntry entry,
        BuilderModelWeakSpotSummary? weakSpot,
        string escalationRequirementState)
    {
        if (string.Equals(escalationRequirementState, "stay_on_current_model", StringComparison.Ordinal) ||
            string.Equals(escalationRequirementState, "current_model_with_repair_loop", StringComparison.Ordinal))
        {
            return "not_needed";
        }

        var shouldSplit = entry.ComplexityDimensions.FileCountTouched >= 3 ||
                          entry.ComplexityDimensions.NewFileCreationCount > 0 ||
                          string.Equals(entry.ComplexityDimensions.PromptAmbiguity, "high", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(weakSpot?.WeakSpot, "file_placement_mistake", StringComparison.Ordinal);

        if (!shouldSplit)
        {
            return "use_stronger_model";
        }

        return string.Equals(escalationRequirementState, "task_should_be_split_first", StringComparison.Ordinal)
            ? "split_then_escalate"
            : "split_task_first";
    }

    private static string DetermineRecommendedModelClass(string escalationRequirementState)
        => escalationRequirementState switch
        {
            "stay_on_current_model" => "current_floor_model",
            "current_model_with_repair_loop" => "current_floor_model",
            _ => "stronger_builder_tier"
        };

    private static IReadOnlyList<string> BuildEscalationReasons(
        BuilderModelRoutingRecommendationEntry entry,
        BuilderProofCaseResult sourceCase,
        BuilderModelWeakSpotSummary? weakSpot,
        string escalationRequirementState,
        string splitTaskRecommendationState)
    {
        var reasons = new List<string>();
        reasons.AddRange(entry.Reasons);
        reasons.Add($"Escalation state: {escalationRequirementState}.");

        if (weakSpot is not null)
        {
            reasons.Add($"Linked weak spot: {weakSpot.Summary}");
        }

        if (string.Equals(sourceCase.RepeatedFailureClassification, "beyond_model_floor", StringComparison.Ordinal))
        {
            reasons.Add("Repeated proof history already places this task beyond the low-floor model.");
        }

        if (string.Equals(splitTaskRecommendationState, "split_task_first", StringComparison.Ordinal) ||
            string.Equals(splitTaskRecommendationState, "split_then_escalate", StringComparison.Ordinal))
        {
            reasons.Add("The current scope should be reduced into smaller bounded steps before another low-floor attempt.");
        }

        return reasons
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildSplitTaskGuidance(
        BuilderModelRoutingRecommendationEntry entry,
        BuilderProofCapabilityClass capability,
        BuilderModelWeakSpotSummary? weakSpot,
        string splitTaskRecommendationState)
    {
        if (string.Equals(splitTaskRecommendationState, "not_needed", StringComparison.Ordinal) ||
            string.Equals(splitTaskRecommendationState, "use_stronger_model", StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        var guidance = new List<string>();

        if (entry.ComplexityDimensions.FileCountTouched >= 3)
        {
            guidance.Add("Reduce the touched file count to one or two files per step before another low-floor attempt.");
        }

        if (entry.ComplexityDimensions.DependencyReferenceChangeCount > 0)
        {
            guidance.Add("Isolate dependency or project-reference changes into their own step before feature edits.");
        }

        if (entry.ComplexityDimensions.TestChangesRequired)
        {
            guidance.Add("Separate compile-fix work from test-scope edits so the rerun can prove one concern at a time.");
        }

        if (entry.ComplexityDimensions.NewFileCreationCount > 0)
        {
            guidance.Add("Create or move new files in a dedicated step before layering additional behavior changes.");
        }

        if (string.Equals(entry.ComplexityDimensions.PromptAmbiguity, "high", StringComparison.OrdinalIgnoreCase))
        {
            guidance.Add("Rewrite the task as explicit file-by-file instructions before using the low-floor model again.");
        }

        if (string.Equals(weakSpot?.WeakSpot, "file_placement_mistake", StringComparison.Ordinal))
        {
            guidance.Add("Isolate file placement and namespace moves before attempting follow-on behavior edits.");
        }

        if (string.Equals(weakSpot?.WeakSpot, "partial_implementation_gap", StringComparison.Ordinal))
        {
            guidance.Add("Separate missing implementation completion from any later test or wiring changes.");
        }

        if (!string.IsNullOrWhiteSpace(capability.PromptTighteningSummary))
        {
            guidance.Add($"Keep the task scaffold explicit: {capability.PromptTighteningSummary}");
        }

        return guidance
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static BuilderModelComparativeProofHook BuildComparativeProofHook(
        string runFolder,
        string modelId,
        BuilderModelRoutingRecommendationEntry entry,
        string recommendedModelClass)
    {
        var comparisonKey = $"{entry.ProofScope}|{entry.TaskClass}";
        var comparisonHintPath = Path.Combine(runFolder, "comparison-hooks", $"{SanitizeBuilderProofToken(comparisonKey)}.json");
        return new BuilderModelComparativeProofHook(
            comparisonKey,
            entry.TaskClass,
            modelId,
            entry.TrustBand,
            recommendedModelClass,
            entry.ProofScope,
            entry.TargetId,
            BuilderProofRunArtifactPath(runFolder),
            comparisonHintPath,
            $"Use comparison key '{comparisonKey}' when a stronger-model proof run is added for the same bounded task class.");
    }

    private static string BuildEscalationSummary(
        BuilderModelRoutingRecommendationEntry entry,
        string escalationRequirementState,
        string splitTaskRecommendationState,
        string recommendedModelClass,
        BuilderModelWeakSpotSummary? weakSpot)
        => escalationRequirementState switch
        {
            "stay_on_current_model" => $"{entry.TargetLabel} stays inside the proven low-floor envelope and can remain on the current model.",
            "current_model_with_repair_loop" => $"{entry.TargetLabel} stays in-band for the low-floor model, but the bounded repair loop should be expected.",
            "stronger_model_recommended" => $"{entry.TargetLabel} sits beyond the comfortable clean floor. Recommend a {recommendedModelClass} route before repeating this scope.",
            "stronger_model_required" => $"{entry.TargetLabel} exceeds the safe low-floor envelope. Use a {recommendedModelClass} route instead of retrying the floor model.",
            _ => $"{entry.TargetLabel} should be split into smaller bounded steps before another low-floor attempt{(string.Equals(splitTaskRecommendationState, "split_then_escalate", StringComparison.Ordinal) ? ", and keep a stronger model ready if the split still shows boundary weak spots." : ".")}{(weakSpot is null ? string.Empty : $" Primary weak spot: {weakSpot.Summary}")}"
        };

    private static string BuildRoutingPlanSummary(
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelWeakSpotSummary? weakSpot)
        => escalationDecision.EscalationRequirementState switch
        {
            "stay_on_current_model" => $"{escalationDecision.TargetLabel} is safe for the current model in its recorded bounded scope.",
            "current_model_with_repair_loop" => $"{escalationDecision.TargetLabel} can stay on the current model, but the repair loop should be expected and planned for.",
            "stronger_model_recommended" => $"{escalationDecision.TargetLabel} should route to a stronger builder tier for the next attempt.",
            "stronger_model_required" => $"{escalationDecision.TargetLabel} should not be retried on the low-floor model; escalate directly to a stronger builder tier.",
            _ => $"{escalationDecision.TargetLabel} should be split into smaller bounded steps before another low-floor attempt{(weakSpot is null ? "." : $". The strongest linked weak spot is {weakSpot.Summary}")}"
        };

    private static IReadOnlyList<string> BuildRoutingReasons(BuilderProofCapabilityClass capability, BuilderProofCaseResult result)
    {
        var reasons = new List<string>
        {
            $"Final classification: {result.FinalClassification}.",
            $"Files touched: {result.ComplexityDimensions.FileCountTouched}; projects touched: {result.ComplexityDimensions.ProjectCountTouched}; dependency/reference changes: {result.ComplexityDimensions.DependencyReferenceChangeCount}; ambiguity: {result.ComplexityDimensions.PromptAmbiguity}."
        };

        if (!capability.InScope)
        {
            reasons.Add("This task class is retained as a routing boundary probe rather than a supported low-floor claim.");
        }

        if (result.RecoveryRequired)
        {
            reasons.Add("The latest proof case required the bounded repair loop.");
        }

        if (string.Equals(result.RepeatedFailureClassification, "beyond_model_floor", StringComparison.Ordinal))
        {
            reasons.Add("Repeated proof evidence places this task beyond the current floor.");
        }

        if (result.ComplexityDimensions.TestChangesRequired)
        {
            reasons.Add("The task includes test-scope changes.");
        }

        if (result.ComplexityDimensions.NewFileCreationCount > 0)
        {
            reasons.Add($"The task creates {result.ComplexityDimensions.NewFileCreationCount} new file(s).");
        }

        return reasons;
    }

    private static string ClassifyWeakSpot(string category, IReadOnlyList<BuilderModelTrustBandEntry> entries)
    {
        if (entries.Any(entry =>
                string.Equals(entry.RecommendationState, "task_out_of_scope_for_floor", StringComparison.Ordinal) ||
                string.Equals(entry.RecommendationState, "stronger_model_recommended", StringComparison.Ordinal)))
        {
            return "boundary_of_model_floor";
        }

        if (string.Equals(category, "namespace_import_omission", StringComparison.Ordinal))
        {
            return "manageable_with_prompt_tightening";
        }

        if (entries.Any(entry => string.Equals(entry.RecommendationState, "proceed_with_repair_loop_expected", StringComparison.Ordinal)))
        {
            return "acceptable_with_repair_loop";
        }

        return "manageable_with_prompt_tightening";
    }

    private static int GetRoutingSeverity(string recommendationState)
        => recommendationState switch
        {
            "task_out_of_scope_for_floor" => 0,
            "stronger_model_recommended" => 1,
            "proceed_with_repair_loop_expected" => 2,
            _ => 3
        };

    private static string BuildRoutingSummary(BuilderModelRoutingRecommendationEntry entry)
        => entry.RecommendationState switch
        {
            "task_out_of_scope_for_floor" => $"{entry.TargetLabel} is out of scope for the low-floor model and should be declined or routed upward.",
            "stronger_model_recommended" => $"{entry.TargetLabel} sits beyond the comfortable floor scope and should recommend a stronger model.",
            "proceed_with_repair_loop_expected" => $"{entry.TargetLabel} stays inside the floor scope, but the repair loop should be expected.",
            _ => $"{entry.TargetLabel} stays inside the currently proven clean floor scope."
        };

    private static string BuildWeakSpotLabel(string category)
        => category switch
        {
            "namespace_import_omission" => "Namespace/import omission",
            "project_wiring_error" => "Project wiring error",
            "test_wiring_error" => "Test wiring error",
            "file_placement_mistake" => "File placement mistake",
            "command_or_template_misunderstanding" => "Command/template misunderstanding",
            _ => "Partial implementation gap"
        };

    private static string ClassifyRecoveryBurden(BuilderProofCaseResult result)
        => string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal)
            ? "clean"
            : string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal)
                ? "acceptable_with_repair_loop"
                : "too_fragile";

    private static string ClassifyFailurePattern(BuilderProofCaseResult result, string summary, string errorExcerpt)
    {
        if (string.Equals(result.TaskClass, "test_extension", StringComparison.Ordinal))
        {
            return "namespace_import_omission";
        }

        if (string.Equals(result.TaskClass, "bounded_refactor", StringComparison.Ordinal))
        {
            return "file_placement_mistake";
        }

        var excerpt = errorExcerpt ?? string.Empty;
        var combined = $"{result.FinalSummary} {errorExcerpt} {summary}";
        if (excerpt.Contains("CS1002", StringComparison.OrdinalIgnoreCase) ||
            excerpt.Contains("CS1513", StringComparison.OrdinalIgnoreCase) ||
            excerpt.Contains("CS1519", StringComparison.OrdinalIgnoreCase) ||
            excerpt.Contains("CS0161", StringComparison.OrdinalIgnoreCase))
        {
            return "partial_implementation_gap";
        }

        if (excerpt.Contains("CS0246", StringComparison.OrdinalIgnoreCase) ||
            excerpt.Contains("CS0103", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("namespace name", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("using", StringComparison.OrdinalIgnoreCase))
        {
            return "namespace_import_omission";
        }

        if (excerpt.Contains("ProjectReference", StringComparison.OrdinalIgnoreCase) ||
            excerpt.Contains("metadata file", StringComparison.OrdinalIgnoreCase) ||
            excerpt.Contains("restore", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("ProjectReference", StringComparison.OrdinalIgnoreCase))
        {
            return "project_wiring_error";
        }

        if (combined.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("test adapter", StringComparison.OrdinalIgnoreCase))
        {
            return "test_wiring_error";
        }

        if (combined.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return "file_placement_mistake";
        }

        if (combined.Contains("template", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("sdk", StringComparison.OrdinalIgnoreCase))
        {
            return "command_or_template_misunderstanding";
        }

        return "partial_implementation_gap";
    }

    private static string BuildFailurePatternReason(string category)
        => category switch
        {
            "namespace_import_omission" => "The failure looked like a missing namespace or using-level omission.",
            "project_wiring_error" => "The failure looked like project wiring drift.",
            "test_wiring_error" => "The failure looked like test wiring drift.",
            "file_placement_mistake" => "The failure looked like code or artifacts landing in the wrong file location.",
            "command_or_template_misunderstanding" => "The failure looked like the task missed a template or command constraint.",
            _ => "The failure looked like a partial implementation gap inside the bounded target file."
        };

    private static string BuildFailurePatternCategorySummary(string category, int count)
        => category switch
        {
            "namespace_import_omission" => $"Namespace or import omissions appeared {count} time(s).",
            "project_wiring_error" => $"Project wiring errors appeared {count} time(s).",
            "test_wiring_error" => $"Test wiring errors appeared {count} time(s).",
            "file_placement_mistake" => $"File placement mistakes appeared {count} time(s).",
            "command_or_template_misunderstanding" => $"Template or command misunderstanding appeared {count} time(s).",
            _ => $"Partial implementation gaps appeared {count} time(s)."
        };

    private static string ExtractFailureExcerpt(string logPath, string summary)
    {
        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            var logExcerpt = File.ReadLines(logPath)
                .FirstOrDefault(line =>
                    line.Contains(": error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Test Run Failed", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(logExcerpt))
            {
                return logExcerpt.Trim();
            }
        }

        var excerpt = summary.Split(new[] { ';' }, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(excerpt) ? summary : excerpt;
    }

    private static string BuildProofCommandSummary(string label, BuilderProofCommandExecutionResult command)
    {
        var headline = command.OutputLines
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?.Trim()
            ?? "No output was captured.";
        return $"{label}: exit_code={command.ExitCode}; {headline}";
    }

    private static BuilderProofStageRecord BuildProofStageRecord(
        string stageId,
        string stageLabel,
        string status,
        string summary,
        string logPath,
        int exitCode)
        => new(stageId, stageLabel, status, summary, logPath, exitCode);

    private static string BuildProofCaseSummary(
        BuilderProofTargetDefinition target,
        BuilderProofGenerationRecord generation,
        IReadOnlyList<BuilderProofStageRecord> stageResults,
        BuilderProofRecoveryRecord? recovery,
        string finalClassification,
        string repeatedFailureClassification)
    {
        var buildStage = stageResults.FirstOrDefault(stage => string.Equals(stage.StageId, "build", StringComparison.Ordinal));
        var testStage = stageResults.FirstOrDefault(stage => string.Equals(stage.StageId, "test", StringComparison.Ordinal));
        var segments = new List<string>
        {
            $"{target.TargetLabel}: {generation.GenerationOutcome}.",
            $"Build={buildStage?.Status ?? "not_run"}."
        };

        if (testStage is not null)
        {
            segments.Add($"Test={testStage.Status}.");
        }

        if (recovery is not null && !string.Equals(recovery.RecoveryState, "not_needed", StringComparison.Ordinal))
        {
            segments.Add($"Recovery={recovery.RecoveryState}.");
        }

        segments.Add($"Final={finalClassification}.");
        if (!string.Equals(repeatedFailureClassification, "not_applicable", StringComparison.Ordinal))
        {
            segments.Add($"Repeated failure classification={repeatedFailureClassification}.");
        }

        return string.Join(" ", segments);
    }

    private static void WriteBuilderProofHistory(string repoRoot, BuilderProofRun run, IReadOnlyList<BuilderProofCaseResult> externalCaseResults, BuilderProofHistory priorHistory)
    {
        var minimumRetentionCount = Math.Max(priorHistory.RetentionCount, (run.CaseResults.Count + externalCaseResults.Count) * 3);
        var entries = priorHistory.Entries
            .Concat(run.CaseResults.Concat(externalCaseResults).Select(result => new BuilderProofHistoryEntry(
                run.ProofRunId,
                run.RunFolder,
                run.ModelId,
                result.TargetId,
                result.TaskClass,
                result.FinalClassification,
                result.RepeatedFailureClassification,
                result.FinalSummary,
                run.CompletedUtc)))
            .OrderByDescending(entry => entry.CompletedUtc)
            .ThenByDescending(entry => entry.RunId, StringComparer.Ordinal)
            .Take(minimumRetentionCount)
            .ToArray();

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        File.WriteAllText(
            BuilderProofHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(new BuilderProofHistory(minimumRetentionCount, entries), new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RefreshBuilderDefaultPolicyArtifacts(string repoRoot, string runFolder)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(runFolder))
        {
            return;
        }

        var capabilityRegistry = RefreshBuilderCapabilityArtifacts(repoRoot);
        var languageEligibility = LoadBuilderLanguageEligibility(repoRoot);

        var proofRun = LoadBuilderProofRun(runFolder);
        var trustBands = LoadBuilderModelTrustBands(runFolder);
        var routingRecommendation = LoadBuilderModelRoutingRecommendation(runFolder);
        var escalationDecision = LoadBuilderModelEscalationDecision(runFolder);
        var routingPlan = LoadBuilderModelRoutingPlan(runFolder);
        if (proofRun is null || trustBands is null || routingRecommendation is null || escalationDecision is null || routingPlan is null)
        {
            return;
        }

        var comparativeRun = LoadBuilderComparativeProofRun(runFolder);
        var routingPolicyEvidence = LoadBuilderRoutingPolicyEvidence(runFolder);
        var splitPlan = LoadBuilderSplitFirstPlan(runFolder);
        var tieredRoutingPolicy = LoadBuilderTieredRoutingPolicy(runFolder);
        var splitOutcome = LoadBuilderSplitFirstOutcome(runFolder);

        var policy = BuildBuilderDefaultPolicy(
            runFolder,
            proofRun,
            trustBands,
            routingRecommendation,
            escalationDecision,
            routingPlan,
            comparativeRun,
            routingPolicyEvidence,
            splitPlan,
            tieredRoutingPolicy,
            splitOutcome);
        File.WriteAllText(BuilderDefaultPolicyPath(runFolder), JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var policyHistory = BuildBuilderDefaultPolicyHistory(
            LoadBuilderDefaultPolicyHistory(repoRoot),
            policy);
        File.WriteAllText(
            BuilderDefaultPolicyHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(policyHistory, new JsonSerializerOptions { WriteIndented = true }));

        var requestDecision = BuildBuilderRequestPolicyDecision(
            runFolder,
            policy,
            routingRecommendation,
            escalationDecision,
            routingPlan,
            tieredRoutingPolicy,
            splitOutcome);
        var confirmedTaskClasses = BuildBuilderConfirmedTaskClasses(
            repoRoot,
            runFolder,
            policy);
        File.WriteAllText(
            BuilderConfirmedTaskClassesPath(runFolder),
            JsonSerializer.Serialize(confirmedTaskClasses, new JsonSerializerOptions { WriteIndented = true }));
        var contradictions = BuildBuilderReadinessContradictions(
            runFolder,
            confirmedTaskClasses);
        File.WriteAllText(
            BuilderReadinessContradictionsPath(runFolder),
            JsonSerializer.Serialize(contradictions, new JsonSerializerOptions { WriteIndented = true }));
        if (requestDecision is not null)
        {
            File.WriteAllText(BuilderRequestPolicyDecisionPath(runFolder), JsonSerializer.Serialize(requestDecision, new JsonSerializerOptions { WriteIndented = true }));

            var stability = BuildBuilderPolicyStability(policyHistory, requestDecision);
            File.WriteAllText(BuilderPolicyStabilityPath(runFolder), JsonSerializer.Serialize(stability, new JsonSerializerOptions { WriteIndented = true }));
            var defaultRouteDecision = BuildBuilderDefaultRouteDecision(
                runFolder,
                requestDecision,
                stability,
                confirmedTaskClasses,
                contradictions);
            File.WriteAllText(
                BuilderDefaultRouteDecisionPath(runFolder),
                JsonSerializer.Serialize(defaultRouteDecision, new JsonSerializerOptions { WriteIndented = true }));

            var capabilityDecision = BuildBuilderCapabilityBlockDecision(
                runFolder,
                requestDecision,
                defaultRouteDecision,
                capabilityRegistry,
                languageEligibility);
            if (ShouldPersistBuilderCapabilityBlockDecision(capabilityDecision))
            {
                File.WriteAllText(
                    BuilderCapabilityBlockDecisionPath(runFolder),
                    JsonSerializer.Serialize(capabilityDecision, new JsonSerializerOptions { WriteIndented = true }));
            }

            var intake = BuildBuilderRequestIntake(
                runFolder,
                requestDecision,
                stability,
                routingPlan,
                splitPlan,
                tieredRoutingPolicy,
                splitOutcome,
                defaultRouteDecision,
                capabilityDecision);
            File.WriteAllText(BuilderRequestIntakePath(runFolder), JsonSerializer.Serialize(intake, new JsonSerializerOptions { WriteIndented = true }));

            var intakeHistory = BuildBuilderRequestIntakeHistory(
                LoadBuilderRequestIntakeHistory(repoRoot),
                intake);
            File.WriteAllText(
                BuilderRequestIntakeHistoryPathForRepo(repoRoot),
                JsonSerializer.Serialize(intakeHistory, new JsonSerializerOptions { WriteIndented = true }));
            RewriteStaleBuilderRequestIntakes(runFolder, intakeHistory);

            var prep = BuildBuilderExecutionPrep(
                runFolder,
                intake,
                requestDecision,
                stability,
                routingPlan,
                splitPlan,
                tieredRoutingPolicy,
                splitOutcome,
                defaultRouteDecision,
                capabilityDecision);
            File.WriteAllText(BuilderExecutionPrepPath(runFolder), JsonSerializer.Serialize(prep, new JsonSerializerOptions { WriteIndented = true }));

            var prepHistory = BuildBuilderExecutionPrepHistory(
                LoadBuilderExecutionPrepHistory(repoRoot),
                prep);
            File.WriteAllText(
                BuilderExecutionPrepHistoryPathForRepo(repoRoot),
                JsonSerializer.Serialize(prepHistory, new JsonSerializerOptions { WriteIndented = true }));
            RewriteStaleBuilderExecutionPrep(runFolder, prepHistory);
        }

        RefreshBuilderReadinessArtifacts(repoRoot, runFolder);
        RefreshBuilderRouteRecoveryArtifacts(repoRoot, runFolder);
        RewriteSupersededBuilderExecutionLaunchArtifacts(repoRoot, runFolder);
        RewriteSupersededBuilderExecutionResultArtifacts(repoRoot, runFolder);
        RefreshBuilderRouteContinuityArtifacts(repoRoot);
        RefreshBuilderModelRoutingArtifacts(repoRoot);
    }

    private void RefreshBuilderReadinessArtifacts(string repoRoot, string runFolder)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(runFolder))
        {
            return;
        }

        var proofRun = LoadBuilderProofRun(runFolder);
        var intake = LoadBuilderRequestIntake(runFolder);
        var prep = LoadBuilderExecutionPrep(runFolder);
        if (proofRun is null || intake is null || prep is null)
        {
            return;
        }

        var launch = LoadBuilderExecutionLaunch(runFolder);
        var result = LoadBuilderExecutionResult(runFolder);
        var gate = BuildBuilderReadinessGate(repoRoot, runFolder, proofRun, intake, prep, launch, result);
        File.WriteAllText(BuilderReadinessGatePath(runFolder), JsonSerializer.Serialize(gate, new JsonSerializerOptions { WriteIndented = true }));

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var history = BuildBuilderReadinessGateHistory(
            LoadBuilderReadinessGateHistory(repoRoot),
            gate);
        File.WriteAllText(
            BuilderReadinessGateHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(BuilderRouteStabilitySummaryPath(runFolder), BuildBuilderRouteStabilitySummaryMarkdown(gate));
    }

    private void RefreshBuilderRouteRecoveryArtifacts(string repoRoot, string runFolder)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(runFolder))
        {
            return;
        }

        var intake = LoadBuilderRequestIntake(runFolder);
        var prep = LoadBuilderExecutionPrep(runFolder);
        var defaultRouteDecision = LoadBuilderDefaultRouteDecision(runFolder);
        var readinessGate = LoadBuilderReadinessGate(runFolder);
        var contradictions = LoadBuilderReadinessContradictions(runFolder);
        var launchDecision = LoadBuilderLaunchDefaultDecision(runFolder);
        var overrideEvidence = LoadBuilderRouteOverrideEvidence(runFolder);
        if (intake is null || prep is null)
        {
            return;
        }

        var reconfirmation = BuildBuilderRouteReconfirmation(
            repoRoot,
            runFolder,
            intake,
            prep,
            defaultRouteDecision,
            readinessGate,
            contradictions,
            launchDecision,
            overrideEvidence);
        File.WriteAllText(BuilderRouteReconfirmationPath(runFolder), JsonSerializer.Serialize(reconfirmation, new JsonSerializerOptions { WriteIndented = true }));

        var recovery = BuildBuilderDefaultRouteRecovery(
            runFolder,
            intake,
            prep,
            defaultRouteDecision,
            readinessGate,
            contradictions,
            launchDecision,
            overrideEvidence,
            reconfirmation);
        File.WriteAllText(BuilderDefaultRouteRecoveryPath(runFolder), JsonSerializer.Serialize(recovery, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static T? LoadBuilderRouteArtifactFromCurrentStateIndex<T>(
        string repoRoot,
        string artifactKind,
        Func<string, T?> loadFromRunFolder)
    {
        var authoritativePath = TryResolveBuilderRouteArtifactPathFromCurrentStateIndex(repoRoot, artifactKind);
        if (!string.IsNullOrWhiteSpace(authoritativePath))
        {
            return TryLoadBuilderProofArtifact<T?>(authoritativePath, default);
        }

        return LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? loadFromRunFolder(runFolder)
            : default;
    }

    private static string TryResolveBuilderRouteArtifactPathFromCurrentStateIndex(string repoRoot, string artifactKind)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(artifactKind))
        {
            return string.Empty;
        }

        var entry = LoadBuilderRouteCurrentStateIndex(repoRoot).Entries
            .FirstOrDefault(candidate =>
                string.Equals(candidate.ArtifactKind, artifactKind, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(candidate.ArtifactPath) &&
                File.Exists(candidate.ArtifactPath));
        return entry?.ArtifactPath ?? string.Empty;
    }

    public BuilderToolchainCapabilityRegistry RefreshBuilderCapabilityArtifacts(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderToolchainCapabilityRegistry(repoRoot);
        }

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var policy = BuildBuilderRepoToolchainPolicySnapshot(repoRoot);
        var observations = _builderToolchainCapabilityScanner.Scan(repoRoot);
        var registry = BuildBuilderToolchainCapabilityRegistry(
            repoRoot,
            policy,
            observations,
            LoadBuilderToolchainCapabilityRegistry(repoRoot));
        File.WriteAllText(
            BuilderToolchainCapabilityRegistryPathForRepo(repoRoot),
            JsonSerializer.Serialize(registry, new JsonSerializerOptions { WriteIndented = true }));

        var history = BuildBuilderToolchainCapabilityHistory(
            LoadBuilderToolchainCapabilityHistory(repoRoot),
            registry);
        File.WriteAllText(
            BuilderToolchainCapabilityHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));

        var eligibility = BuildBuilderLanguageEligibility(repoRoot, policy, registry);
        File.WriteAllText(
            BuilderLanguageEligibilityPathForRepo(repoRoot),
            JsonSerializer.Serialize(eligibility, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderLanguageEligibilitySummaryPathForRepo(repoRoot),
            BuildBuilderLanguageEligibilitySummaryMarkdown(eligibility));
        return registry;
    }

    public BuilderModelRoutingPolicy RefreshBuilderModelRoutingArtifacts(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderModelRoutingPolicy(repoRoot);
        }

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var latestRun = LoadLatestBuilderProofRun(repoRoot);
        if (latestRun is not null && LoadBuilderDefaultPolicy(latestRun.RunFolder) is null)
        {
            RefreshBuilderDefaultPolicyArtifacts(repoRoot, latestRun.RunFolder);
        }

        var defaultPolicy = LoadLatestBuilderDefaultPolicy(repoRoot);
        if (defaultPolicy is null)
        {
            return LoadBuilderModelRoutingPolicy(repoRoot);
        }

        var priorPolicy = LoadBuilderModelRoutingPolicy(repoRoot);
        var stability = BuildBuilderModelRoutingStability(repoRoot, defaultPolicy);
        File.WriteAllText(
            BuilderModelRoutingStabilityPathForRepo(repoRoot),
            JsonSerializer.Serialize(stability, new JsonSerializerOptions { WriteIndented = true }));

        var matrix = BuildBuilderModelCapabilityMatrix(repoRoot, defaultPolicy, stability);
        File.WriteAllText(
            BuilderModelCapabilityMatrixPathForRepo(repoRoot),
            JsonSerializer.Serialize(matrix, new JsonSerializerOptions { WriteIndented = true }));

        var routingPolicy = BuildBuilderModelRoutingPolicyArtifact(repoRoot, defaultPolicy, matrix);
        File.WriteAllText(
            BuilderModelRoutingPolicyPathForRepo(repoRoot),
            JsonSerializer.Serialize(routingPolicy, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderModelRoutingPolicySummaryPathForRepo(repoRoot),
            BuildBuilderModelRoutingPolicySummaryMarkdown(routingPolicy));

        var history = BuildBuilderModelRoutingPolicyHistory(
            LoadBuilderModelRoutingPolicyHistory(repoRoot),
            priorPolicy,
            routingPolicy);
        File.WriteAllText(
            BuilderModelRoutingPolicyHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        return routingPolicy;
    }

    public BuilderRepoKnowledgeIndex RefreshBuilderRepoKnowledgeArtifacts(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderRepoKnowledgeIndex(repoRoot);
        }

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var capabilityRegistry = LoadBuilderToolchainCapabilityRegistry(repoRoot);
        if (capabilityRegistry.ObservedUtc <= DateTimeOffset.MinValue)
        {
            capabilityRegistry = RefreshBuilderCapabilityArtifacts(repoRoot);
        }

        var priorIndex = LoadBuilderRepoKnowledgeIndex(repoRoot);
        var index = BuildBuilderRepoKnowledgeIndex(repoRoot, capabilityRegistry, priorIndex);
        File.WriteAllText(
            BuilderRepoKnowledgeIndexPathForRepo(repoRoot),
            JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderRepoKnowledgeSummaryPathForRepo(repoRoot),
            BuildBuilderRepoKnowledgeSummaryMarkdown(index));

        var drift = BuildBuilderRepoKnowledgeDrift(priorIndex, index);
        var driftPath = BuilderRepoKnowledgeDriftPathForRepo(repoRoot);
        if (drift is null)
        {
            if (File.Exists(driftPath))
            {
                File.Delete(driftPath);
            }
        }
        else
        {
            File.WriteAllText(
                driftPath,
                JsonSerializer.Serialize(drift, new JsonSerializerOptions { WriteIndented = true }));
        }

        var history = BuildBuilderRepoKnowledgeHistory(
            LoadBuilderRepoKnowledgeHistory(repoRoot),
            index);
        File.WriteAllText(
            BuilderRepoKnowledgeHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        return index;
    }

    public BuilderConversationIntake PreviewBuilderConversationIntake(string repoRoot, string rawRequestText)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderConversationIntake(repoRoot);
        }

        rawRequestText ??= string.Empty;
        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var knowledgeIndex = RefreshBuilderRepoKnowledgeArtifacts(repoRoot);
        var capabilityRegistry = LoadBuilderToolchainCapabilityRegistry(repoRoot);
        if (capabilityRegistry.ObservedUtc <= DateTimeOffset.MinValue)
        {
            capabilityRegistry = RefreshBuilderCapabilityArtifacts(repoRoot);
        }

        var languageEligibility = LoadBuilderLanguageEligibility(repoRoot);
        if (languageEligibility.ObservedUtc <= DateTimeOffset.MinValue)
        {
            capabilityRegistry = RefreshBuilderCapabilityArtifacts(repoRoot);
            languageEligibility = LoadBuilderLanguageEligibility(repoRoot);
        }

        var normalizedTaskClass = NormalizeBuilderConversationTaskClass(rawRequestText);
        var impliedStackId = ResolveBuilderConversationImpliedStackId(rawRequestText, knowledgeIndex);
        var impliedStackLabel = ResolveBuilderStackLabel(impliedStackId);
        var retrieval = BuildBuilderRepoRetrievalContext(
            repoRoot,
            knowledgeIndex,
            rawRequestText,
            normalizedTaskClass,
            impliedStackId,
            impliedStackLabel);
        File.WriteAllText(
            BuilderRepoRetrievalContextPathForRepo(repoRoot),
            JsonSerializer.Serialize(retrieval, new JsonSerializerOptions { WriteIndented = true }));

        var modelRoutingPolicy = RefreshBuilderModelRoutingArtifacts(repoRoot);
        var modelCapabilityMatrix = LoadBuilderModelCapabilityMatrix(repoRoot);
        var modelRoutingStability = LoadBuilderModelRoutingStability(repoRoot);
        var matchedPolicyEntry = FindBuilderModelRoutingPolicyEntry(modelRoutingPolicy, normalizedTaskClass);
        var matchedMatrixEntry = FindBuilderModelCapabilityMatrixEntry(modelCapabilityMatrix, normalizedTaskClass);
        var latestPrep = LoadLatestBuilderExecutionPrep(repoRoot);
        var latestRequestDecision = LoadLatestBuilderRequestPolicyDecision(repoRoot);
        var defaultRouteDecision = LoadLatestBuilderDefaultRouteDecision(repoRoot);
        var launchDefaultDecision = LoadLatestBuilderLaunchDefaultDecision(repoRoot);
        var selectedRoute = FirstNonEmpty(
            matchedPolicyEntry?.RouteClass,
            launchDefaultDecision?.ConfirmedDefaultRoute,
            defaultRouteDecision?.ChosenDefaultRoute,
            latestPrep?.SelectedRoute);
        var routeSourceState = matchedPolicyEntry is null
            ? FirstNonEmpty(
                launchDefaultDecision?.RouteSourceState,
                defaultRouteDecision?.RouteSourceState,
                "not_recorded")
            : "model_routing_policy";
        var capabilityRoutingState = DetermineBuilderConversationCapabilityRoutingState(
            languageEligibility,
            impliedStackId);
        var capabilitySummary = BuildBuilderConversationCapabilitySummary(
            languageEligibility,
            impliedStackId,
            capabilityRoutingState);
        var observedUtc = DateTimeOffset.UtcNow;
        var provisionalIntake = new BuilderConversationIntake(
            rawRequestText,
            normalizedTaskClass,
            impliedStackId,
            impliedStackLabel,
            retrieval.RetrievalConfidenceState,
            retrieval.Summary,
            capabilityRoutingState,
            capabilitySummary,
            selectedRoute,
            routeSourceState,
            matchedMatrixEntry?.SplitFirstRequired ?? string.Equals(selectedRoute, "split_first_low_floor_route", StringComparison.Ordinal),
            FirstNonEmpty(matchedMatrixEntry?.StrongerTierRecommendationState, latestRequestDecision?.StrongerTierDisposition, "not_recorded"),
            "pending_operator_review",
            "not_ready",
            string.Empty,
            Array.Empty<string>(),
            string.Empty,
            BuilderConversationIntakePathForRepo(repoRoot),
            observedUtc);
        var modelDecision = BuildBuilderModelDecision(
            repoRoot,
            BuildBuilderConversationIntakeId(provisionalIntake),
            provisionalIntake,
            modelCapabilityMatrix,
            modelRoutingPolicy,
            modelRoutingStability);
        var escalationPolicyDecision = BuildBuilderModelEscalationPolicyDecision(repoRoot, modelDecision);
        File.WriteAllText(
            BuilderModelDecisionPathForRepo(repoRoot),
            JsonSerializer.Serialize(modelDecision, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderModelEscalationPolicyDecisionPathForRepo(repoRoot),
            JsonSerializer.Serialize(escalationPolicyDecision, new JsonSerializerOptions { WriteIndented = true }));
        var launchReadinessState = DetermineBuilderConversationLaunchReadinessState(
            retrieval.RetrievalConfidenceState,
            capabilityRoutingState,
            selectedRoute,
            escalationPolicyDecision);
        var blockReason = BuildBuilderConversationBlockReason(
            retrieval,
            languageEligibility,
            impliedStackId,
            capabilityRoutingState,
            launchReadinessState,
            selectedRoute,
            escalationPolicyDecision);
        var linkedArtifactPaths = BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot)
            .Concat(new[]
            {
                knowledgeIndex.ArtifactPath,
                capabilityRegistry.ArtifactPath,
                languageEligibility.ArtifactPath,
                retrieval.ArtifactPath,
                latestPrep?.ArtifactPath ?? string.Empty,
                modelCapabilityMatrix.ArtifactPath,
                modelRoutingPolicy.ArtifactPath,
                modelRoutingStability.ArtifactPath,
                modelDecision.ArtifactPath,
                escalationPolicyDecision.ArtifactPath
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = BuildBuilderConversationIntakeSummary(
            rawRequestText,
            normalizedTaskClass,
            impliedStackLabel,
            retrieval,
            capabilitySummary,
            modelDecision.Summary,
            selectedRoute,
            routeSourceState,
            launchReadinessState,
            blockReason);
        var intake = provisionalIntake with
        {
            LaunchReadinessState = launchReadinessState,
            BlockReason = blockReason,
            LinkedArtifactPaths = linkedArtifactPaths,
            Summary = summary
        };
        File.WriteAllText(
            BuilderConversationIntakePathForRepo(repoRoot),
            JsonSerializer.Serialize(intake, new JsonSerializerOptions { WriteIndented = true }));
        modelDecision = modelDecision with
        {
            LinkedArtifactPaths = modelDecision.LinkedArtifactPaths
                .Concat(new[] { intake.ArtifactPath, escalationPolicyDecision.ArtifactPath })
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        File.WriteAllText(
            BuilderModelDecisionPathForRepo(repoRoot),
            JsonSerializer.Serialize(modelDecision, new JsonSerializerOptions { WriteIndented = true }));
        escalationPolicyDecision = escalationPolicyDecision with
        {
            LinkedArtifactPaths = escalationPolicyDecision.LinkedArtifactPaths
                .Concat(new[] { intake.ArtifactPath, modelDecision.ArtifactPath })
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        File.WriteAllText(
            BuilderModelEscalationPolicyDecisionPathForRepo(repoRoot),
            JsonSerializer.Serialize(escalationPolicyDecision, new JsonSerializerOptions { WriteIndented = true }));
        RefreshBuilderDiagnosticArtifacts(repoRoot);
        return intake;
    }

    public BuilderConversationHandoff CreateBuilderConversationHandoff(
        string repoRoot,
        string operatorDecisionState,
        string routeOverride = "",
        string overrideReason = "")
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderConversationHandoff(repoRoot);
        }

        var intake = LoadBuilderConversationIntake(repoRoot);
        var modelEscalationPolicyDecision = LoadBuilderModelEscalationPolicyDecision(repoRoot);
        var selectedRoute = intake.SelectedRoute;
        var routeSourceState = intake.RouteSourceState;
        var launchReadinessState = intake.LaunchReadinessState;
        var blockReason = intake.BlockReason;

        if (string.Equals(operatorDecisionState, "override_route", StringComparison.Ordinal))
        {
            routeSourceState = "operator_override";
            if (string.IsNullOrWhiteSpace(routeOverride))
            {
                launchReadinessState = "launch_blocked_missing_override_route";
                blockReason = "Builder conversation override requires an explicit supported route.";
            }
            else if (string.Equals(intake.CapabilityRoutingState, "route_blocked_missing_toolchain", StringComparison.Ordinal) ||
                     string.Equals(intake.CapabilityRoutingState, "route_blocked_repo_policy", StringComparison.Ordinal))
            {
                launchReadinessState = "launch_blocked_capability";
                blockReason = intake.BlockReason;
            }
            else if (string.Equals(modelEscalationPolicyDecision.FinalDecisionState, "blocked_required_stronger_tier_unavailable", StringComparison.Ordinal) ||
                     string.Equals(modelEscalationPolicyDecision.FinalDecisionState, "not_yet_proven", StringComparison.Ordinal))
            {
                launchReadinessState = "launch_blocked_model_policy";
                blockReason = FirstNonEmpty(modelEscalationPolicyDecision.BlockReason, modelEscalationPolicyDecision.Summary, intake.BlockReason);
            }
            else if (!IsBuilderPreparedRouteSupported(routeOverride))
            {
                launchReadinessState = "launch_blocked_override_route";
                blockReason = $"Builder conversation override route {routeOverride} is not supported for prepared launch.";
            }
            else
            {
                selectedRoute = routeOverride;
                launchReadinessState = "ready_for_launch_with_override";
                blockReason = string.Empty;
            }
        }
        else if (string.Equals(operatorDecisionState, "cancel", StringComparison.Ordinal))
        {
            launchReadinessState = "launch_cancelled_by_operator";
            blockReason = "Operator cancelled the builder conversation handoff before launch.";
        }
        else
        {
            if (string.Equals(intake.LaunchReadinessState, "launch_blocked_weak_match", StringComparison.Ordinal))
            {
                launchReadinessState = "launch_blocked_weak_match";
                blockReason = intake.BlockReason;
            }
            else if (string.Equals(intake.CapabilityRoutingState, "route_blocked_missing_toolchain", StringComparison.Ordinal) ||
                     string.Equals(intake.CapabilityRoutingState, "route_blocked_repo_policy", StringComparison.Ordinal))
            {
                launchReadinessState = "launch_blocked_capability";
                blockReason = intake.BlockReason;
            }
            else if (string.Equals(intake.LaunchReadinessState, "launch_blocked_model_policy", StringComparison.Ordinal))
            {
                launchReadinessState = intake.LaunchReadinessState;
                blockReason = FirstNonEmpty(intake.BlockReason, modelEscalationPolicyDecision.BlockReason, modelEscalationPolicyDecision.Summary);
            }
            else if (string.IsNullOrWhiteSpace(intake.SelectedRoute) || !IsBuilderPreparedRouteSupported(intake.SelectedRoute))
            {
                launchReadinessState = "launch_blocked_route";
                blockReason = string.IsNullOrWhiteSpace(intake.SelectedRoute)
                    ? "Builder conversation handoff has no prepared route to launch."
                    : $"Prepared route {intake.SelectedRoute} still requires operator review.";
            }
            else
            {
                launchReadinessState = "ready_for_launch";
                blockReason = string.Empty;
            }
        }

        var linkedArtifactPaths = intake.LinkedArtifactPaths
            .Concat(BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot))
            .Concat(new[]
            {
                BuilderConversationIntakePathForRepo(repoRoot),
                LoadLatestBuilderExecutionPrep(repoRoot)?.ArtifactPath ?? string.Empty
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = BuildBuilderConversationHandoffSummary(
            intake,
            operatorDecisionState,
            selectedRoute,
            launchReadinessState,
            blockReason,
            overrideReason);
        var handoff = new BuilderConversationHandoff(
            intake.RawRequestText,
            intake.NormalizedTaskClass,
            intake.RetrievalConfidenceState,
            intake.CapabilityRoutingState,
            selectedRoute,
            routeSourceState,
            operatorDecisionState,
            launchReadinessState,
            blockReason,
            linkedArtifactPaths,
            summary,
            BuilderConversationHandoffPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            BuilderConversationHandoffPathForRepo(repoRoot),
            JsonSerializer.Serialize(handoff, new JsonSerializerOptions { WriteIndented = true }));
        RefreshBuilderDiagnosticArtifacts(repoRoot);
        return handoff;
    }

    public async Task<BuilderConversationExecutionSession> RunBuilderConversationExecutionSessionAsync(
        string repoRoot,
        string provider = "ollama",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderConversationExecutionSession(repoRoot);
        }

        var intake = LoadBuilderConversationIntake(repoRoot);
        var handoff = LoadBuilderConversationHandoff(repoRoot);
        if (!string.Equals(handoff.LaunchReadinessState, "ready_for_launch", StringComparison.Ordinal) &&
            !string.Equals(handoff.LaunchReadinessState, "ready_for_launch_with_override", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Builder conversation execution requires a ready conversation handoff.");
        }

        var latestPrep = LoadLatestBuilderExecutionPrep(repoRoot);
        var intakeId = BuildBuilderConversationIntakeId(intake);
        var handoffId = BuildBuilderConversationHandoffId(handoff);
        var sessionId = BuildBuilderConversationExecutionSessionId(handoff);
        var executingSession = BuildBuilderConversationExecutionSession(
            repoRoot,
            sessionId,
            intakeId,
            handoffId,
            intake,
            handoff,
            latestPrep,
            launch: null,
            result: null,
            patchReview: null,
            patchReviewOutcome: null,
            sessionState: "executing",
            currentStageId: "builder_launched",
            currentStageLabel: "Builder launched",
            reviewState: "pending_operator_review",
            validationSummary: "Prepared route execution is in progress.",
            reviewNote: string.Empty);
        WriteBuilderConversationExecutionSessionArtifacts(repoRoot, executingSession, null);

        var routeOverride = string.Equals(handoff.LaunchReadinessState, "ready_for_launch_with_override", StringComparison.Ordinal) ||
                            string.Equals(handoff.OperatorDecisionState, "override_route", StringComparison.Ordinal)
            ? handoff.SelectedRoute
            : null;
        var overrideReason = !string.IsNullOrWhiteSpace(routeOverride) ? handoff.Summary : null;
        var result = await LaunchPreparedBuilderRouteAsync(repoRoot, provider, routeOverride, overrideReason, ct).ConfigureAwait(false);
        var latestRun = LoadLatestBuilderProofRun(repoRoot)
            ?? throw new InvalidOperationException("Builder conversation execution requires a recorded proof run.");
        var launch = LoadBuilderExecutionLaunch(latestRun.RunFolder);
        var refreshedPrep = LoadBuilderExecutionPrep(latestRun.RunFolder) ?? latestPrep;
        var patchReview = BuildBuilderPatchReview(
            repoRoot,
            sessionId,
            intakeId,
            handoffId,
            intake,
            handoff,
            refreshedPrep,
            result);
        File.WriteAllText(
            BuilderPatchReviewPathForRepo(repoRoot),
            JsonSerializer.Serialize(patchReview, new JsonSerializerOptions { WriteIndented = true }));

        var finalSessionState = string.Equals(patchReview.ReviewReadinessState, "ready_for_operator_review", StringComparison.Ordinal)
            ? "awaiting_patch_review"
            : "failed_into_followup";
        var finalCurrentStageId = string.Equals(finalSessionState, "awaiting_patch_review", StringComparison.Ordinal)
            ? "awaiting_operator_review"
            : "validation_run";
        var finalCurrentStageLabel = string.Equals(finalSessionState, "awaiting_patch_review", StringComparison.Ordinal)
            ? "Awaiting operator review"
            : "Validation run";
        var finalSession = BuildBuilderConversationExecutionSession(
            repoRoot,
            sessionId,
            intakeId,
            handoffId,
            intake,
            handoff,
            refreshedPrep,
            launch,
            result,
            patchReview,
            patchReviewOutcome: null,
            sessionState: finalSessionState,
            currentStageId: finalCurrentStageId,
            currentStageLabel: finalCurrentStageLabel,
            reviewState: "pending_operator_review",
            validationSummary: BuildBuilderConversationValidationSummary(result),
            reviewNote: string.Empty);
        WriteBuilderConversationExecutionSessionArtifacts(repoRoot, finalSession, patchReview);
        var patchDiffReview = BuildBuilderPatchDiffReview(repoRoot, finalSession, patchReview, result);
        var initialFileReviewDecision = BuildBuilderFileReviewDecision(
            repoRoot,
            patchDiffReview,
            patchDiffReview.FileEntries
                .Select(entry => new BuilderFileReviewDecisionEntry(
                    entry.RelativePath,
                    entry.ApprovalState,
                    string.Empty,
                    entry.RejectionReason,
                    new[] { patchDiffReview.ArtifactPath },
                    entry.ObservedUtc))
                .ToArray(),
            patchDiffReview.ObservedUtc);
        var initialApplyDecision = BuildBuilderPatchApplyDecision(
            repoRoot,
            finalSession,
            patchDiffReview,
            initialFileReviewDecision,
            patchReviewOutcome: null);
        WriteBuilderPatchReviewGovernanceArtifacts(repoRoot, patchDiffReview, initialFileReviewDecision, initialApplyDecision);
        RefreshBuilderDiagnosticArtifacts(repoRoot);
        return finalSession;
    }

    public BuilderPatchReviewOutcome RecordBuilderPatchReviewOutcome(
        string repoRoot,
        string reviewDecisionState,
        string reviewNote = "",
        string rerouteRoute = "")
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderPatchReviewOutcome(repoRoot);
        }

        var currentSession = LoadBuilderConversationExecutionSession(repoRoot);
        var patchReview = LoadBuilderPatchReview(repoRoot);
        var intake = LoadBuilderConversationIntake(repoRoot);
        var handoff = LoadBuilderConversationHandoff(repoRoot);
        var result = LoadLatestBuilderExecutionResult(repoRoot);
        var latestPrep = LoadLatestBuilderExecutionPrep(repoRoot);

        BuilderConversationHandoff effectiveHandoff = handoff;
        if (string.Equals(reviewDecisionState, "reroute_requested", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(rerouteRoute))
        {
            effectiveHandoff = CreateBuilderConversationHandoff(
                repoRoot,
                "override_route",
                rerouteRoute,
                FirstNonEmpty(reviewNote, "Operator requested a bounded reroute during patch review."));
        }

        var nextSessionState = DetermineBuilderConversationReviewSessionState(reviewDecisionState, result);
        var linkedArtifactPaths = BuildBuilderConversationReviewLinkedArtifactPaths(
            repoRoot,
            currentSession,
            patchReview,
            result,
            effectiveHandoff);
        var summary = BuildBuilderPatchReviewOutcomeSummary(
            reviewDecisionState,
            nextSessionState,
            reviewNote,
            rerouteRoute,
            result);
        var outcome = new BuilderPatchReviewOutcome(
            currentSession.SessionId,
            reviewDecisionState,
            nextSessionState,
            reviewDecisionState,
            reviewNote,
            rerouteRoute,
            linkedArtifactPaths,
            summary,
            BuilderPatchReviewOutcomePathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            BuilderPatchReviewOutcomePathForRepo(repoRoot),
            JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));

        var updatedSession = BuildBuilderConversationExecutionSession(
            repoRoot,
            currentSession.SessionId,
            currentSession.SourceConversationIntakeId,
            currentSession.SourceConversationHandoffId,
            intake,
            effectiveHandoff,
            latestPrep,
            LoadLatestBuilderExecutionLaunch(repoRoot),
            result,
            patchReview,
            outcome,
            sessionState: nextSessionState,
            currentStageId: string.Equals(reviewDecisionState, "accepted", StringComparison.Ordinal) ? "completed" : currentSession.CurrentStageId,
            currentStageLabel: string.Equals(reviewDecisionState, "accepted", StringComparison.Ordinal) ? "Completed" : currentSession.CurrentStageLabel,
            reviewState: reviewDecisionState,
            validationSummary: FirstNonEmpty(currentSession.ValidationSummary, BuildBuilderConversationValidationSummary(result)),
            reviewNote: reviewNote);
        WriteBuilderConversationExecutionSessionArtifacts(repoRoot, updatedSession, patchReview, outcome);
        var patchDiffReview = LoadBuilderPatchDiffReview(repoRoot);
        if (patchDiffReview.FileEntries.Count > 0)
        {
            var fileReviewDecision = LoadBuilderFileReviewDecision(repoRoot);
            var applyDecision = BuildBuilderPatchApplyDecision(repoRoot, updatedSession, patchDiffReview, fileReviewDecision, outcome);
            WriteBuilderPatchReviewGovernanceArtifacts(repoRoot, patchDiffReview, fileReviewDecision, applyDecision);
        }

        RefreshBuilderDiagnosticArtifacts(repoRoot);
        return outcome;
    }

    public BuilderFileReviewDecision RecordBuilderPatchFileReviewDecision(
        string repoRoot,
        string relativePath,
        string approvalState,
        string operatorDecisionSource,
        string rejectionReason = "")
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderFileReviewDecision(repoRoot);
        }

        var patchDiffReview = LoadBuilderPatchDiffReview(repoRoot);
        if (patchDiffReview.FileEntries.Count == 0)
        {
            return LoadBuilderFileReviewDecision(repoRoot);
        }

        var priorDecision = LoadBuilderFileReviewDecision(repoRoot);
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var observedUtc = DateTimeOffset.UtcNow;
        var updatedFileEntries = patchDiffReview.FileEntries
            .Select(entry => string.Equals(entry.RelativePath, normalizedRelativePath, StringComparison.Ordinal)
                ? entry with
                {
                    ApprovalState = approvalState,
                    RejectionReason = approvalState is "rejected" or "needs_revision" ? rejectionReason : string.Empty,
                    ObservedUtc = observedUtc
                }
                : entry)
            .ToArray();
        var updatedPatchDiffReview = patchDiffReview with
        {
            FileEntries = updatedFileEntries,
            OverallFileReviewState = DetermineBuilderPatchOverallFileReviewState(updatedFileEntries),
            Summary = BuildBuilderPatchDiffReviewSummary(updatedFileEntries, patchDiffReview.ReviewReadinessState),
            ObservedUtc = observedUtc
        };

        var entryMap = priorDecision.Entries.ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        entryMap[normalizedRelativePath] = new BuilderFileReviewDecisionEntry(
            normalizedRelativePath,
            approvalState,
            operatorDecisionSource,
            approvalState is "rejected" or "needs_revision" ? rejectionReason : string.Empty,
            new[] { updatedPatchDiffReview.ArtifactPath },
            observedUtc);
        var decisionEntries = updatedPatchDiffReview.FileEntries
            .Select(entry => entryMap.TryGetValue(entry.RelativePath, out var existing)
                ? existing with
                {
                    ApprovalState = entry.ApprovalState,
                    RejectionReason = entry.RejectionReason,
                    LinkedArtifactPaths = new[] { updatedPatchDiffReview.ArtifactPath },
                    ObservedUtc = entry.ObservedUtc
                }
                : new BuilderFileReviewDecisionEntry(
                    entry.RelativePath,
                    entry.ApprovalState,
                    string.Empty,
                    entry.RejectionReason,
                    new[] { updatedPatchDiffReview.ArtifactPath },
                    entry.ObservedUtc))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var fileReviewDecision = BuildBuilderFileReviewDecision(repoRoot, updatedPatchDiffReview, decisionEntries, observedUtc);
        var applyDecision = BuildBuilderPatchApplyDecision(
            repoRoot,
            LoadBuilderConversationExecutionSession(repoRoot),
            updatedPatchDiffReview,
            fileReviewDecision,
            LoadBuilderPatchReviewOutcome(repoRoot));
        WriteBuilderPatchReviewGovernanceArtifacts(repoRoot, updatedPatchDiffReview, fileReviewDecision, applyDecision);
        RefreshBuilderDiagnosticArtifacts(repoRoot);
        return fileReviewDecision;
    }

    public BuilderPatchDiffReview ApproveAllBuilderPatchFiles(string repoRoot, string operatorDecisionSource = "approve_all")
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderPatchDiffReview(repoRoot);
        }

        var patchDiffReview = LoadBuilderPatchDiffReview(repoRoot);
        foreach (var file in patchDiffReview.FileEntries.Where(entry => !string.Equals(entry.ApprovalState, "approved", StringComparison.Ordinal)))
        {
            RecordBuilderPatchFileReviewDecision(repoRoot, file.RelativePath, "approved", operatorDecisionSource);
        }

        return LoadBuilderPatchDiffReview(repoRoot);
    }

    public BuilderPatchApplyDecision FinalizeBuilderApprovedPatch(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderPatchApplyDecision(repoRoot);
        }

        var session = LoadBuilderConversationExecutionSession(repoRoot);
        var patchDiffReview = LoadBuilderPatchDiffReview(repoRoot);
        var fileReviewDecision = LoadBuilderFileReviewDecision(repoRoot);
        var currentOutcome = LoadBuilderPatchReviewOutcome(repoRoot);
        var applyDecision = BuildBuilderPatchApplyDecision(repoRoot, session, patchDiffReview, fileReviewDecision, currentOutcome);
        if (!string.Equals(applyDecision.FinalizationState, "ready_to_apply", StringComparison.Ordinal))
        {
            WriteBuilderPatchReviewGovernanceArtifacts(repoRoot, patchDiffReview, fileReviewDecision, applyDecision);
            RefreshBuilderDiagnosticArtifacts(repoRoot);
            return applyDecision;
        }

        var outcome = RecordBuilderPatchReviewOutcome(
            repoRoot,
            "accepted",
            "Operator finalized the approved patch after file-level review.");
        var finalizedApplyDecision = applyDecision with
        {
            LinkedArtifactPaths = applyDecision.LinkedArtifactPaths
                .Concat(new[] { outcome.ArtifactPath })
                .Where(BuilderArtifactPathExists)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            FinalizationState = "applied_with_operator_approval",
            Summary = "Approved patch was applied with explicit operator approval.",
            ObservedUtc = DateTimeOffset.UtcNow
        };
        WriteBuilderPatchReviewGovernanceArtifacts(
            repoRoot,
            LoadBuilderPatchDiffReview(repoRoot),
            LoadBuilderFileReviewDecision(repoRoot),
            finalizedApplyDecision);
        WriteBuilderPatchSnapshotArtifacts(
            repoRoot,
            EnsureBuilderPatchSnapshot(
                repoRoot,
                LoadBuilderConversationExecutionSession(repoRoot),
                LoadBuilderPatchDiffReview(repoRoot),
                LoadBuilderFileReviewDecision(repoRoot),
                finalizedApplyDecision,
                LoadBuilderPatchReviewOutcome(repoRoot),
                LoadLatestBuilderExecutionResult(repoRoot)));
        RefreshBuilderDiagnosticArtifacts(repoRoot);
        return finalizedApplyDecision;
    }

    public BuilderCommitProposal PrepareBuilderCommitProposal(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderCommitProposal(repoRoot);
        }

        var snapshot = EnsureBuilderPatchSnapshot(
            repoRoot,
            LoadBuilderConversationExecutionSession(repoRoot),
            LoadBuilderPatchDiffReview(repoRoot),
            LoadBuilderFileReviewDecision(repoRoot),
            LoadBuilderPatchApplyDecision(repoRoot),
            LoadBuilderPatchReviewOutcome(repoRoot),
            LoadLatestBuilderExecutionResult(repoRoot));
        if (snapshot.ApprovedFiles.Count == 0 ||
            !string.Equals(snapshot.OperatorApprovalState, "applied_with_operator_approval", StringComparison.Ordinal))
        {
            return LoadBuilderCommitProposal(repoRoot);
        }

        var patchDiffReview = LoadBuilderPatchDiffReview(repoRoot);
        var changedFiles = snapshot.ApprovedFiles
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var diffSummary = string.Join(
            " ",
            patchDiffReview.FileEntries
                .Where(entry => changedFiles.Contains(entry.RelativePath, StringComparer.Ordinal))
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .Select(entry => entry.DiffSummary));
        var commitMessage = BuildBuilderCommitProposalMessage(snapshot, patchDiffReview);
        var observedUtc = DateTimeOffset.UtcNow;
        var proposal = new BuilderCommitProposal(
            snapshot.SnapshotId,
            snapshot.ExecutionSessionId,
            commitMessage,
            changedFiles,
            diffSummary,
            repoRoot,
            snapshot.LinkedArtifactPaths
                .Concat(new[] { snapshot.ArtifactPath })
                .Where(BuilderArtifactPathExists)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            $"Commit proposal prepared for {changedFiles.Length} approved file(s).",
            BuilderCommitProposalPathForRepo(repoRoot),
            observedUtc);
        File.WriteAllText(
            BuilderCommitProposalPathForRepo(repoRoot),
            JsonSerializer.Serialize(proposal, new JsonSerializerOptions { WriteIndented = true }));
        return proposal;
    }

    public BuilderPatchExport ExportBuilderApprovedPatchBundle(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderPatchExport(repoRoot);
        }

        var snapshot = EnsureBuilderPatchSnapshot(
            repoRoot,
            LoadBuilderConversationExecutionSession(repoRoot),
            LoadBuilderPatchDiffReview(repoRoot),
            LoadBuilderFileReviewDecision(repoRoot),
            LoadBuilderPatchApplyDecision(repoRoot),
            LoadBuilderPatchReviewOutcome(repoRoot),
            LoadLatestBuilderExecutionResult(repoRoot));
        if (snapshot.ApprovedFiles.Count == 0 ||
            !string.Equals(snapshot.OperatorApprovalState, "applied_with_operator_approval", StringComparison.Ordinal))
        {
            return LoadBuilderPatchExport(repoRoot);
        }

        var patchDiffReview = LoadBuilderPatchDiffReview(repoRoot);
        var result = LoadLatestBuilderExecutionResult(repoRoot);
        var bundleText = BuildBuilderPatchBundleText(repoRoot, snapshot, patchDiffReview, result);
        File.WriteAllText(BuilderPatchBundlePathForRepo(repoRoot), bundleText);

        var observedUtc = DateTimeOffset.UtcNow;
        var export = new BuilderPatchExport(
            snapshot.SnapshotId,
            BuilderPatchBundlePathForRepo(repoRoot),
            observedUtc,
            snapshot.ApprovedFiles.Count,
            snapshot.LinkedArtifactPaths
                .Concat(new[]
                {
                    snapshot.ArtifactPath,
                    patchDiffReview.ArtifactPath,
                    BuilderPatchBundlePathForRepo(repoRoot)
                })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            $"Patch export created for {snapshot.ApprovedFiles.Count} approved file(s).",
            BuilderPatchExportPathForRepo(repoRoot),
            observedUtc);
        File.WriteAllText(
            BuilderPatchExportPathForRepo(repoRoot),
            JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));

        var history = BuildBuilderPatchSnapshotHistory(repoRoot, LoadBuilderPatchSnapshotHistory(repoRoot), snapshot, export);
        File.WriteAllText(
            BuilderPatchSnapshotHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        return export;
    }

    public BuilderOutputHandoff PrepareBuilderOutputHandoff(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return LoadBuilderOutputHandoff(repoRoot);
        }

        var session = LoadBuilderConversationExecutionSession(repoRoot);
        var patchDiffReview = LoadBuilderPatchDiffReview(repoRoot);
        var fileReviewDecision = LoadBuilderFileReviewDecision(repoRoot);
        var patchApplyDecision = LoadBuilderPatchApplyDecision(repoRoot);
        var patchReviewOutcome = LoadBuilderPatchReviewOutcome(repoRoot);
        var result = LoadLatestBuilderExecutionResult(repoRoot);
        var snapshot = EnsureBuilderPatchSnapshot(
            repoRoot,
            session,
            patchDiffReview,
            fileReviewDecision,
            patchApplyDecision,
            patchReviewOutcome,
            result);
        if (snapshot.ApprovedFiles.Count == 0 ||
            !string.Equals(snapshot.OperatorApprovalState, "applied_with_operator_approval", StringComparison.Ordinal))
        {
            return LoadBuilderOutputHandoff(repoRoot);
        }

        var proposal = PrepareBuilderCommitProposal(repoRoot);
        var export = ExportBuilderApprovedPatchBundle(repoRoot);
        if (string.IsNullOrWhiteSpace(proposal.ArtifactPath) ||
            string.IsNullOrWhiteSpace(export.ArtifactPath) ||
            string.IsNullOrWhiteSpace(export.BundleFilePath))
        {
            return LoadBuilderOutputHandoff(repoRoot);
        }

        var approvedFiles = snapshot.ApprovedFiles
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var gitObservation = _builderGitReadinessProbe.Probe(repoRoot);
        var gitReadiness = BuildBuilderGitHandoffReadiness(repoRoot, gitObservation);
        File.WriteAllText(
            BuilderGitHandoffReadinessPathForRepo(repoRoot),
            JsonSerializer.Serialize(gitReadiness, new JsonSerializerOptions { WriteIndented = true }));

        var manualApplyGuidance = BuildBuilderManualApplyGuidance(repoRoot, snapshot, export, gitReadiness);
        File.WriteAllText(
            BuilderManualApplyGuidancePathForRepo(repoRoot),
            JsonSerializer.Serialize(manualApplyGuidance, new JsonSerializerOptions { WriteIndented = true }));

        var gitCommitHandoff = BuildBuilderGitCommitHandoff(repoRoot, snapshot, proposal, gitReadiness);
        File.WriteAllText(
            BuilderGitCommitHandoffPathForRepo(repoRoot),
            JsonSerializer.Serialize(gitCommitHandoff, new JsonSerializerOptions { WriteIndented = true }));

        var observedUtc = new[]
            {
                snapshot.ObservedUtc,
                proposal.ObservedUtc,
                export.ObservedUtc,
                gitReadiness.ObservedUtc,
                manualApplyGuidance.ObservedUtc,
                gitCommitHandoff.ObservedUtc
            }
            .Max();
        var handoffReadinessState = DetermineBuilderOutputHandoffReadinessState(export, gitReadiness);
        var linkedArtifactPaths = snapshot.LinkedArtifactPaths
            .Concat(new[]
            {
                snapshot.ArtifactPath,
                proposal.ArtifactPath,
                export.ArtifactPath,
                export.BundleFilePath,
                manualApplyGuidance.ArtifactPath,
                gitReadiness.ArtifactPath,
                gitCommitHandoff.ArtifactPath,
                BuilderPatchApplyDecisionPathForRepo(repoRoot),
                patchReviewOutcome.ArtifactPath
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var blockReasons = gitReadiness.BlockReasons
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToArray();
        var handoff = new BuilderOutputHandoff(
            snapshot.SnapshotId,
            snapshot.ExecutionSessionId,
            approvedFiles,
            export.BundleFilePath,
            proposal.ArtifactPath,
            export.ArtifactPath,
            manualApplyGuidance.ArtifactPath,
            gitReadiness.ArtifactPath,
            gitCommitHandoff.ArtifactPath,
            handoffReadinessState,
            gitReadiness.ReadinessClassification,
            blockReasons,
            linkedArtifactPaths,
            BuildBuilderOutputHandoffSummary(snapshot, export, gitReadiness),
            BuilderOutputHandoffPathForRepo(repoRoot),
            observedUtc);
        File.WriteAllText(
            BuilderOutputHandoffPathForRepo(repoRoot),
            JsonSerializer.Serialize(handoff, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderOutputHandoffSummaryPathForRepo(repoRoot),
            BuildBuilderOutputHandoffSummaryMarkdown(handoff, manualApplyGuidance));

        var history = BuildBuilderOutputHandoffHistory(repoRoot, LoadBuilderOutputHandoffHistory(repoRoot), handoff);
        File.WriteAllText(
            BuilderOutputHandoffHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        RefreshBuilderDiagnosticArtifacts(repoRoot);
        return handoff;
    }

    private void RefreshBuilderDiagnosticArtifacts(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return;
        }

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var intake = LoadBuilderConversationIntake(repoRoot);
        var handoff = LoadBuilderConversationHandoff(repoRoot);
        var modelDecision = LoadBuilderModelDecision(repoRoot);
        var escalationPolicyDecision = LoadBuilderModelEscalationPolicyDecision(repoRoot);
        var capabilityMatrix = LoadBuilderModelCapabilityMatrix(repoRoot);
        var routingPolicy = LoadBuilderModelRoutingPolicy(repoRoot);
        var defaultRouteDecision = LoadLatestBuilderDefaultRouteDecision(repoRoot);
        var launchDefaultDecision = LoadLatestBuilderLaunchDefaultDecision(repoRoot);
        var session = LoadBuilderConversationExecutionSession(repoRoot);
        var patchReviewOutcome = LoadBuilderPatchReviewOutcome(repoRoot);
        var patchApplyDecision = LoadBuilderPatchApplyDecision(repoRoot);
        var result = LoadLatestBuilderExecutionResult(repoRoot);

        var routeExplanation = BuildBuilderRouteExplanation(
            repoRoot,
            intake,
            handoff,
            capabilityMatrix,
            routingPolicy,
            defaultRouteDecision,
            launchDefaultDecision);
        File.WriteAllText(
            BuilderRouteExplanationPathForRepo(repoRoot),
            JsonSerializer.Serialize(routeExplanation, new JsonSerializerOptions { WriteIndented = true }));

        var modelDecisionExplanation = BuildBuilderModelDecisionExplanation(
            repoRoot,
            intake,
            modelDecision,
            escalationPolicyDecision,
            capabilityMatrix,
            routingPolicy);
        File.WriteAllText(
            BuilderModelDecisionExplanationPathForRepo(repoRoot),
            JsonSerializer.Serialize(modelDecisionExplanation, new JsonSerializerOptions { WriteIndented = true }));

        var failureAnalysis = BuildBuilderFailureAnalysis(
            repoRoot,
            intake,
            handoff,
            session,
            patchReviewOutcome,
            patchApplyDecision,
            result);
        File.WriteAllText(
            BuilderFailureAnalysisPathForRepo(repoRoot),
            JsonSerializer.Serialize(failureAnalysis, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(
            BuilderOperatorDiagnosticSummaryPathForRepo(repoRoot),
            BuildBuilderOperatorDiagnosticSummaryMarkdown(
                intake,
                handoff,
                session,
                result,
                routeExplanation,
                modelDecisionExplanation,
                failureAnalysis));
    }

    private static BuilderRouteExplanation BuildBuilderRouteExplanation(
        string repoRoot,
        BuilderConversationIntake intake,
        BuilderConversationHandoff handoff,
        BuilderModelCapabilityMatrix capabilityMatrix,
        BuilderModelRoutingPolicy routingPolicy,
        BuilderDefaultRouteDecision? defaultRouteDecision,
        BuilderLaunchDefaultDecision? launchDefaultDecision)
    {
        var requestId = intake.ObservedUtc <= DateTimeOffset.MinValue ? string.Empty : BuildBuilderConversationIntakeId(intake);
        var selectedRoute = FirstNonEmpty(handoff.SelectedRoute, intake.SelectedRoute);
        var policyEntry = FindBuilderModelRoutingPolicyEntry(routingPolicy, intake.NormalizedTaskClass);
        var matrixEntry = FindBuilderModelCapabilityMatrixEntry(capabilityMatrix, intake.NormalizedTaskClass);
        var alternateRoutes = BuildBuilderAlternateRoutesConsidered(
            selectedRoute,
            policyEntry,
            defaultRouteDecision,
            launchDefaultDecision);
        var linkedCapabilityEntries = matrixEntry is null
            ? Array.Empty<string>()
            : new[]
            {
                $"{matrixEntry.TaskClass}|{matrixEntry.RouteClass}|{matrixEntry.CapabilityState}|{matrixEntry.EvidenceSupportLevel}"
            };
        var linkedProofArtifacts = (matrixEntry?.LinkedProofArtifactPaths ?? Array.Empty<string>())
            .Concat(policyEntry?.LinkedEvidencePaths ?? Array.Empty<string>())
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var routeSelectionReason = BuildBuilderRouteExplanationReason(
            intake,
            handoff,
            policyEntry,
            matrixEntry,
            defaultRouteDecision,
            launchDefaultDecision);
        var summary = string.IsNullOrWhiteSpace(selectedRoute)
            ? $"No builder route explanation is available because the current request has not resolved a prepared route. {routeSelectionReason}"
            : $"Route {selectedRoute} was chosen for {FirstNonEmpty(intake.NormalizedTaskClass, "unclassified_request")}. {routeSelectionReason}";
        return new BuilderRouteExplanation(
            requestId,
            intake.NormalizedTaskClass,
            selectedRoute,
            alternateRoutes,
            routeSelectionReason,
            linkedCapabilityEntries,
            linkedProofArtifacts,
            summary,
            BuilderRouteExplanationPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelDecisionExplanation BuildBuilderModelDecisionExplanation(
        string repoRoot,
        BuilderConversationIntake intake,
        BuilderModelDecision modelDecision,
        BuilderModelEscalationPolicyDecision escalationPolicyDecision,
        BuilderModelCapabilityMatrix capabilityMatrix,
        BuilderModelRoutingPolicy routingPolicy)
    {
        var policyEntry = FindBuilderModelRoutingPolicyEntry(routingPolicy, intake.NormalizedTaskClass);
        var matrixEntry = FindBuilderModelCapabilityMatrixEntry(capabilityMatrix, intake.NormalizedTaskClass);
        var requestId = FirstNonEmpty(
            modelDecision.RequestId,
            intake.ObservedUtc <= DateTimeOffset.MinValue ? string.Empty : BuildBuilderConversationIntakeId(intake));
        var splitFirstReasoning = BuildBuilderSplitFirstReasoning(modelDecision, escalationPolicyDecision, policyEntry);
        var linkedArtifactPaths = modelDecision.LinkedArtifactPaths
            .Concat(policyEntry?.LinkedEvidencePaths ?? Array.Empty<string>())
            .Concat(matrixEntry?.LinkedProofArtifactPaths ?? Array.Empty<string>())
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var capabilitySummary = matrixEntry?.Summary ?? "No builder model capability entry recorded.";
        var routingRulesSummary = policyEntry?.Summary ?? "No builder routing rules entry recorded.";
        var summary = string.IsNullOrWhiteSpace(modelDecision.SelectedModelTier)
            ? "No builder model decision explanation is available because no current model decision was recorded."
            : $"{modelDecision.SelectedModelTier} ({FirstNonEmpty(modelDecision.SelectedModelId, "not recorded")}) was selected for {FirstNonEmpty(intake.NormalizedTaskClass, modelDecision.NormalizedTaskClass, "unclassified_request")}. {splitFirstReasoning} Escalation state={FirstNonEmpty(escalationPolicyDecision.FinalDecisionState, "not_recorded")}.";
        return new BuilderModelDecisionExplanation(
            requestId,
            modelDecision.SelectedModelTier,
            modelDecision.SelectedModelId,
            capabilitySummary,
            routingRulesSummary,
            escalationPolicyDecision.FinalDecisionState,
            splitFirstReasoning,
            modelDecision.StrongerTierRecommendationState,
            modelDecision.EvidenceSupportLevel,
            linkedArtifactPaths,
            summary,
            BuilderModelDecisionExplanationPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderFailureAnalysis BuildBuilderFailureAnalysis(
        string repoRoot,
        BuilderConversationIntake intake,
        BuilderConversationHandoff handoff,
        BuilderConversationExecutionSession session,
        BuilderPatchReviewOutcome patchReviewOutcome,
        BuilderPatchApplyDecision patchApplyDecision,
        PreparedBuilderExecutionResult? result)
    {
        var linkedArtifactPaths = new[]
            {
                intake.ArtifactPath,
                handoff.ArtifactPath,
                session.ArtifactPath,
                patchReviewOutcome.ArtifactPath,
                patchApplyDecision.ArtifactPath,
                result?.ArtifactPath ?? string.Empty
            }
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        string failureStageId;
        string failureStageLabel;
        string failureClassification;
        string failureReason;
        string possibleRemediationPath;

        if (string.Equals(patchApplyDecision.FinalizationState, "blocked_by_file_rejection", StringComparison.Ordinal))
        {
            failureStageId = FirstNonEmpty(session.CurrentStageId, "awaiting_operator_review");
            failureStageLabel = FirstNonEmpty(session.CurrentStageLabel, "Awaiting operator review");
            failureClassification = "blocked_by_file_rejection";
            failureReason = FirstNonEmpty(patchApplyDecision.Summary, "Finalization is blocked because at least one file was rejected.");
            possibleRemediationPath = "Revise or approve the rejected files before finalizing the patch.";
        }
        else if (string.Equals(patchApplyDecision.FinalizationState, "blocked_by_revision_request", StringComparison.Ordinal))
        {
            failureStageId = FirstNonEmpty(session.CurrentStageId, "awaiting_operator_review");
            failureStageLabel = FirstNonEmpty(session.CurrentStageLabel, "Awaiting operator review");
            failureClassification = "blocked_by_revision_request";
            failureReason = FirstNonEmpty(patchApplyDecision.Summary, "Finalization is blocked because a revision request is still active.");
            possibleRemediationPath = "Complete the requested revision, then rerun file review before finalizing.";
        }
        else if (string.Equals(patchReviewOutcome.ReviewDecisionState, "rejected", StringComparison.Ordinal))
        {
            failureStageId = FirstNonEmpty(session.CurrentStageId, "awaiting_operator_review");
            failureStageLabel = FirstNonEmpty(session.CurrentStageLabel, "Awaiting operator review");
            failureClassification = "patch_rejected";
            failureReason = FirstNonEmpty(patchReviewOutcome.Summary, "Operator rejected the candidate patch.");
            possibleRemediationPath = "Create a bounded revision or reroute using the current patch review evidence.";
        }
        else if (string.Equals(patchReviewOutcome.ReviewDecisionState, "revise_requested", StringComparison.Ordinal))
        {
            failureStageId = FirstNonEmpty(session.CurrentStageId, "awaiting_operator_review");
            failureStageLabel = FirstNonEmpty(session.CurrentStageLabel, "Awaiting operator review");
            failureClassification = "revision_requested";
            failureReason = FirstNonEmpty(patchReviewOutcome.Summary, "Operator requested a bounded revision.");
            possibleRemediationPath = "Revise the candidate changes and return to patch review with the recorded file evidence.";
        }
        else if (string.Equals(patchReviewOutcome.ReviewDecisionState, "reroute_requested", StringComparison.Ordinal))
        {
            failureStageId = FirstNonEmpty(session.CurrentStageId, "awaiting_operator_review");
            failureStageLabel = FirstNonEmpty(session.CurrentStageLabel, "Awaiting operator review");
            failureClassification = "reroute_requested";
            failureReason = FirstNonEmpty(patchReviewOutcome.Summary, "Operator requested a different route.");
            possibleRemediationPath = "Prepare the requested reroute and re-enter the execution session with the current evidence trail.";
        }
        else if (string.Equals(session.SessionState, "failed_into_followup", StringComparison.Ordinal))
        {
            failureStageId = FirstNonEmpty(session.CurrentStageId, "validation_run");
            failureStageLabel = FirstNonEmpty(session.CurrentStageLabel, "Validation run");
            failureClassification = result?.FinalRouteOutcomeClassification switch
            {
                "launched_and_failed_out_of_scope" => "execution_failed_out_of_scope",
                "launched_and_failed_followup_created" => "execution_failed_into_followup",
                _ => "execution_failed_into_followup"
            };
            failureReason = FirstNonEmpty(result?.Summary, session.Summary, "Builder execution failed and entered follow-up.");
            possibleRemediationPath = string.Equals(result?.FinalRouteOutcomeClassification, "launched_and_failed_out_of_scope", StringComparison.Ordinal)
                ? "Reduce scope or select a route/model tier with evidence for the requested task class."
                : "Review the linked follow-up or repair artifacts and rerun the bounded request.";
        }
        else if (handoff.LaunchReadinessState.StartsWith("launch_blocked_", StringComparison.Ordinal))
        {
            failureStageId = "conversation_handoff";
            failureStageLabel = "Conversation handoff";
            failureClassification = handoff.LaunchReadinessState;
            failureReason = FirstNonEmpty(handoff.BlockReason, handoff.Summary, "Builder conversation handoff is blocked.");
            possibleRemediationPath = DetermineBuilderLaunchBlockRemediation(handoff.LaunchReadinessState, failureReason);
        }
        else if (intake.LaunchReadinessState.StartsWith("launch_blocked_", StringComparison.Ordinal))
        {
            failureStageId = "conversation_preview";
            failureStageLabel = "Conversation preview";
            failureClassification = intake.LaunchReadinessState;
            failureReason = FirstNonEmpty(intake.BlockReason, intake.Summary, "Builder conversation preview is blocked.");
            possibleRemediationPath = DetermineBuilderLaunchBlockRemediation(intake.LaunchReadinessState, failureReason);
        }
        else
        {
            failureStageId = FirstNonEmpty(session.CurrentStageId, "not_started");
            failureStageLabel = FirstNonEmpty(session.CurrentStageLabel, "Not started");
            failureClassification = "no_failure_recorded";
            failureReason = "No current builder failure is recorded for the latest request state.";
            possibleRemediationPath = "No remediation is required for the current builder state.";
        }

        var summary = string.Equals(failureClassification, "no_failure_recorded", StringComparison.Ordinal)
            ? $"{failureReason} Stage={failureStageLabel}."
            : $"{failureClassification} at {failureStageLabel}: {failureReason} Remediation: {possibleRemediationPath}";
        return new BuilderFailureAnalysis(
            session.SessionId,
            failureStageId,
            failureStageLabel,
            failureClassification,
            failureReason,
            linkedArtifactPaths,
            possibleRemediationPath,
            summary,
            BuilderFailureAnalysisPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string> BuildBuilderAlternateRoutesConsidered(
        string selectedRoute,
        BuilderModelRoutingPolicyEntry? policyEntry,
        BuilderDefaultRouteDecision? defaultRouteDecision,
        BuilderLaunchDefaultDecision? launchDefaultDecision)
    {
        var routes = new[]
            {
                policyEntry?.FallbackPath ?? string.Empty,
                policyEntry?.RouteClass ?? string.Empty,
                defaultRouteDecision?.ChosenDefaultRoute ?? string.Empty,
                launchDefaultDecision?.ConfirmedDefaultRoute ?? string.Empty
            }
            .Concat(GetBuilderRouteFamilyAlternates(selectedRoute))
            .Where(route => !string.IsNullOrWhiteSpace(route) &&
                            !string.Equals(route, selectedRoute, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();
        return routes;
    }

    private static IReadOnlyList<string> GetBuilderRouteFamilyAlternates(string selectedRoute)
        => selectedRoute switch
        {
            "direct_low_floor_route" => new[] { "split_first_low_floor_route", "low_floor_with_repair_loop_route" },
            "split_first_low_floor_route" => new[] { "direct_low_floor_route", "low_floor_with_repair_loop_route" },
            "low_floor_with_repair_loop_route" => new[] { "direct_low_floor_route", "split_first_low_floor_route" },
            "stronger_tier_recommended_route" => new[] { "split_first_low_floor_route", "direct_low_floor_route" },
            "task_out_of_scope_route" => new[] { "split_first_low_floor_route", "stronger_tier_recommended_route" },
            _ => Array.Empty<string>()
        };

    private static string BuildBuilderRouteExplanationReason(
        BuilderConversationIntake intake,
        BuilderConversationHandoff handoff,
        BuilderModelRoutingPolicyEntry? policyEntry,
        BuilderModelCapabilityMatrixEntry? matrixEntry,
        BuilderDefaultRouteDecision? defaultRouteDecision,
        BuilderLaunchDefaultDecision? launchDefaultDecision)
    {
        var parts = new List<string>();
        if (string.Equals(handoff.OperatorDecisionState, "override_route", StringComparison.Ordinal))
        {
            parts.Add($"Operator override selected {handoff.SelectedRoute}.");
        }
        else if (!string.IsNullOrWhiteSpace(intake.RouteSourceState))
        {
            parts.Add($"Route source={intake.RouteSourceState}.");
        }

        if (!string.IsNullOrWhiteSpace(policyEntry?.Summary))
        {
            parts.Add(policyEntry.Summary);
        }

        if (!string.IsNullOrWhiteSpace(matrixEntry?.Summary))
        {
            parts.Add(matrixEntry.Summary);
        }

        if (!string.IsNullOrWhiteSpace(launchDefaultDecision?.Summary))
        {
            parts.Add(launchDefaultDecision.Summary);
        }
        else if (!string.IsNullOrWhiteSpace(defaultRouteDecision?.Summary))
        {
            parts.Add(defaultRouteDecision.Summary);
        }

        if (!string.IsNullOrWhiteSpace(handoff.BlockReason))
        {
            parts.Add($"Block reason: {handoff.BlockReason}");
        }
        else if (!string.IsNullOrWhiteSpace(intake.BlockReason))
        {
            parts.Add($"Block reason: {intake.BlockReason}");
        }

        return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
    }

    private static string BuildBuilderSplitFirstReasoning(
        BuilderModelDecision modelDecision,
        BuilderModelEscalationPolicyDecision escalationPolicyDecision,
        BuilderModelRoutingPolicyEntry? policyEntry)
    {
        if (modelDecision.SplitFirstKeepsLowFloorViable ||
            string.Equals(escalationPolicyDecision.FinalDecisionState, "low_floor_via_split_first", StringComparison.Ordinal))
        {
            return "Split-first keeps the low-floor route viable for this task class.";
        }

        if (policyEntry?.SplitFirstRequired == true)
        {
            return "Split-first is required before the selected model tier can be used safely.";
        }

        if (string.Equals(modelDecision.CapabilityState, "low_floor_direct_supported", StringComparison.Ordinal))
        {
            return "Split-first is not required because current proof keeps this task class inside the direct low-floor lane.";
        }

        if (string.Equals(modelDecision.CapabilityState, "low_floor_supported_with_repair_loop", StringComparison.Ordinal))
        {
            return "Split-first is not the deciding factor here; the low-floor lane remains bounded only with repair-loop recovery.";
        }

        return "Split-first does not currently keep the low-floor route viable for this task class.";
    }

    private static string DetermineBuilderLaunchBlockRemediation(string launchReadinessState, string failureReason)
        => launchReadinessState switch
        {
            "launch_blocked_model_policy" when failureReason.Contains("stronger", StringComparison.OrdinalIgnoreCase) =>
                "Use the stronger tier when it becomes available, or keep the request split-first only when proof explicitly supports that lane.",
            "launch_blocked_model_policy" =>
                "Refresh the recorded model routing evidence and keep the request inside a proven lane before launching again.",
            "launch_blocked_weak_match" =>
                "Clarify the request or use an explicit operator override only after reviewing the weak repo match evidence.",
            "launch_blocked_capability" =>
                "Use a repo-supported stack/toolchain or refresh capability evidence before preparing the route again.",
            "launch_blocked_route" =>
                "Refresh the current route decision artifacts and confirm a prepared route before launch.",
            _ => "Review the linked builder artifacts, correct the blocking condition, and retry the bounded request."
        };

    private static string BuildBuilderOperatorDiagnosticSummaryMarkdown(
        BuilderConversationIntake intake,
        BuilderConversationHandoff handoff,
        BuilderConversationExecutionSession session,
        PreparedBuilderExecutionResult? result,
        BuilderRouteExplanation routeExplanation,
        BuilderModelDecisionExplanation modelDecisionExplanation,
        BuilderFailureAnalysis failureAnalysis)
    {
        var finalOutcome = FirstNonEmpty(
            result?.FinalRouteOutcomeClassification,
            session.SessionState,
            handoff.LaunchReadinessState,
            intake.LaunchReadinessState,
            "not_recorded");
        var splitState = intake.SplitFirstRequired || modelDecisionExplanation.SplitFirstReasoning.Contains("keeps the low-floor route viable", StringComparison.OrdinalIgnoreCase)
            ? "required_or_used"
            : "not_required";
        var strongerTierState = FirstNonEmpty(
            modelDecisionExplanation.StrongerTierRecommendationState,
            intake.StrongerTierDisposition,
            "not_recorded");
        var blockSummary = string.Equals(failureAnalysis.FailureClassification, "no_failure_recorded", StringComparison.Ordinal)
            ? "No current block conditions are recorded."
            : failureAnalysis.FailureReason;

        return string.Join(
            System.Environment.NewLine,
            new[]
            {
                "# Builder Operator Diagnostic Summary",
                string.Empty,
                $"- Request: {FirstNonEmpty(intake.RawRequestText, handoff.RawRequestText, "Not recorded")}",
                $"- Task class: {FirstNonEmpty(intake.NormalizedTaskClass, handoff.NormalizedTaskClass, "not_recorded")}",
                $"- Route: {FirstNonEmpty(routeExplanation.SelectedRoute, intake.SelectedRoute, handoff.SelectedRoute, "not_recorded")}",
                $"- Model tier: {FirstNonEmpty(modelDecisionExplanation.ModelTierSelected, "not_recorded")}",
                $"- Split-first: {splitState}",
                $"- Stronger tier: {strongerTierState}",
                $"- Block conditions: {blockSummary}",
                $"- Final execution outcome: {finalOutcome}",
                string.Empty,
                "## Route Explanation",
                routeExplanation.Summary,
                string.Empty,
                "## Model Decision Explanation",
                modelDecisionExplanation.Summary,
                string.Empty,
                "## Failure Analysis",
                failureAnalysis.Summary
            });
    }

    private static BuilderToolchainCapabilityRegistry BuildBuilderToolchainCapabilityRegistry(
        string repoRoot,
        BuilderRepoToolchainPolicySnapshot policy,
        IReadOnlyList<BuilderToolchainCapabilityObservation> observations,
        BuilderToolchainCapabilityRegistry priorRegistry)
    {
        var observedUtc = observations.Count == 0
            ? DateTimeOffset.UtcNow
            : observations.Max(entry => entry.ObservedUtc);
        var observationMap = observations
            .GroupBy(entry => entry.ToolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.ObservedUtc).First(), StringComparer.Ordinal);
        foreach (var toolId in GetBuilderToolchainCandidateIds())
        {
            if (!observationMap.ContainsKey(toolId))
            {
                observationMap[toolId] = new BuilderToolchainCapabilityObservation(
                    toolId,
                    GetBuilderToolchainCategory(toolId),
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    "not_found",
                    $"{toolId} is not installed or not discoverable on PATH.",
                    observedUtc);
            }
        }

        observationMap["dotnet_wpf_desktop"] = BuildBuilderWpfDesktopObservation(
            policy,
            observationMap["dotnet"],
            observedUtc);

        var entries = observationMap.Values
            .Select(observation =>
            {
                var supportedByRepo = IsBuilderToolchainSupportedByRepo(observation.ToolId, policy);
                var preferredByRepo = IsBuilderToolchainPreferredByRepo(observation.ToolId, policy);
                var usabilityState = DetermineBuilderToolchainUsabilityState(observation, supportedByRepo, preferredByRepo);
                var blockedReason = BuildBuilderToolchainBlockedReason(observation, supportedByRepo);
                return new BuilderToolchainCapabilityRegistryEntry(
                    observation.ToolId,
                    observation.ToolCategory,
                    observation.DiscoveredPath,
                    observation.Version,
                    observation.Installed,
                    observation.Callable,
                    supportedByRepo,
                    preferredByRepo,
                    supportedByRepo
                        ? preferredByRepo ? "preferred_for_repo" : "supported_for_repo"
                        : "not_supported_for_repo",
                    usabilityState,
                    blockedReason,
                    BuildBuilderToolchainRegistryEntrySummary(observation, usabilityState, blockedReason),
                    observation.ObservedUtc);
            })
            .OrderBy(entry => entry.ToolId, StringComparer.Ordinal)
            .ToArray();

        var changedToolIds = new SortedSet<string>(StringComparer.Ordinal);
        var changeSummaries = new List<string>();
        foreach (var entry in entries)
        {
            var previous = priorRegistry.Entries.FirstOrDefault(candidate => string.Equals(candidate.ToolId, entry.ToolId, StringComparison.Ordinal));
            if (previous is null)
            {
                if (priorRegistry.ObservedUtc > DateTimeOffset.MinValue)
                {
                    changedToolIds.Add(entry.ToolId);
                    changeSummaries.Add($"{entry.ToolId} was newly observed during capability refresh.");
                }

                continue;
            }

            if (!string.Equals(previous.Version, entry.Version, StringComparison.Ordinal))
            {
                changedToolIds.Add(entry.ToolId);
                changeSummaries.Add($"{entry.ToolId} version changed from {FirstNonEmpty(previous.Version, "not_recorded")} to {FirstNonEmpty(entry.Version, "not_recorded")}.");
            }

            if (previous.Callable != entry.Callable)
            {
                changedToolIds.Add(entry.ToolId);
                changeSummaries.Add($"{entry.ToolId} callable state changed from {previous.Callable} to {entry.Callable}.");
            }

            if (previous.Installed != entry.Installed)
            {
                changedToolIds.Add(entry.ToolId);
                changeSummaries.Add($"{entry.ToolId} installed state changed from {previous.Installed} to {entry.Installed}.");
            }

            if (!string.Equals(previous.DiscoveredPath, entry.DiscoveredPath, StringComparison.Ordinal))
            {
                changedToolIds.Add(entry.ToolId);
                changeSummaries.Add($"{entry.ToolId} path changed from {FirstNonEmpty(previous.DiscoveredPath, "not_recorded")} to {FirstNonEmpty(entry.DiscoveredPath, "not_recorded")}.");
            }
        }

        var driftState = priorRegistry.ObservedUtc <= DateTimeOffset.MinValue
            ? "initial_scan"
            : changeSummaries.Count > 0
                ? "changed"
                : "unchanged";
        return new BuilderToolchainCapabilityRegistry(
            policy.PreferredStackId,
            policy.PreferredStackLabel,
            entries,
            "completed",
            driftState,
            changedToolIds.ToArray(),
            changeSummaries.ToArray(),
            BuildBuilderToolchainCapabilitySummary(policy, entries, driftState),
            BuilderToolchainCapabilityRegistryPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderToolchainCapabilityHistory BuildBuilderToolchainCapabilityHistory(
        BuilderToolchainCapabilityHistory priorHistory,
        BuilderToolchainCapabilityRegistry current)
    {
        var retentionCount = Math.Max(priorHistory.RetentionCount, 12);
        var currentEntry = new BuilderToolchainCapabilityHistoryEntry(
            current.PreferredStackId,
            current.PreferredStackLabel,
            current.RefreshState,
            current.DriftState,
            current.ChangedToolIds,
            current.ChangeSummaries,
            current.Summary,
            current.ArtifactPath,
            current.ObservedUtc);
        var entries = priorHistory.Entries
            .Where(entry => entry.ObservedUtc != currentEntry.ObservedUtc)
            .Concat(new[] { currentEntry })
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.PreferredStackId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();
        var summary = entries.Length == 0
            ? "No toolchain capability refresh history recorded."
            : current.ChangeSummaries.Count == 0
                ? "Latest capability refresh found no drift."
                : $"Latest capability refresh changed {current.ChangedToolIds.Count} toolchain observation(s).";

        return new BuilderToolchainCapabilityHistory(
            retentionCount,
            entries,
            summary,
            Path.Combine(Path.GetDirectoryName(current.ArtifactPath) ?? string.Empty, "builder_toolchain_capability_history.json"),
            current.ObservedUtc);
    }

    private static BuilderLanguageEligibility BuildBuilderLanguageEligibility(
        string repoRoot,
        BuilderRepoToolchainPolicySnapshot policy,
        BuilderToolchainCapabilityRegistry registry)
    {
        var entries = GetBuilderLanguageStackDefinitions()
            .Select(definition =>
            {
                var supportedByRepo = IsBuilderLanguageSupportedByRepo(definition.StackId, policy);
                var preferredByRepo = IsBuilderLanguagePreferredByRepo(definition.StackId, policy);
                var ready = IsBuilderLanguageReady(definition.StackId, registry);
                var anyInstalled = definition.RequiredToolIds.Any(toolId => IsBuilderToolchainInstalled(registry, toolId));
                var eligibilityState = supportedByRepo
                    ? ready
                        ? preferredByRepo ? "ready_and_preferred" : "ready_but_not_preferred"
                        : "unavailable"
                    : anyInstalled
                        ? "installed_but_disallowed"
                        : "unsupported_for_repo";
                var blockedReason = eligibilityState switch
                {
                    "unavailable" => $"Required toolchain is unavailable: {definition.ToolchainRequirementSummary}",
                    "installed_but_disallowed" => $"{definition.StackLabel} is installed on the machine but blocked by repo policy.",
                    "unsupported_for_repo" => $"{definition.StackLabel} is not supported by repo policy.",
                    _ => string.Empty
                };
                return new BuilderLanguageEligibilityEntry(
                    definition.StackId,
                    definition.StackLabel,
                    eligibilityState,
                    supportedByRepo,
                    preferredByRepo,
                    definition.RequiredToolIds,
                    definition.ToolchainRequirementSummary,
                    blockedReason,
                    BuildBuilderLanguageEligibilityEntrySummary(definition, eligibilityState, blockedReason));
            })
            .OrderBy(entry => entry.StackId, StringComparer.Ordinal)
            .ToArray();

        return new BuilderLanguageEligibility(
            policy.PreferredStackId,
            policy.PreferredStackLabel,
            entries,
            BuildBuilderLanguageEligibilitySummary(entries, policy),
            BuilderLanguageEligibilityPathForRepo(repoRoot),
            registry.ObservedUtc);
    }

    private static string BuildBuilderLanguageEligibilitySummaryMarkdown(BuilderLanguageEligibility eligibility)
    {
        var preferred = eligibility.Entries.Where(entry => string.Equals(entry.EligibilityState, "ready_and_preferred", StringComparison.Ordinal)).ToArray();
        var readyNotPreferred = eligibility.Entries.Where(entry => string.Equals(entry.EligibilityState, "ready_but_not_preferred", StringComparison.Ordinal)).ToArray();
        var blocked = eligibility.Entries.Where(entry =>
                string.Equals(entry.EligibilityState, "installed_but_disallowed", StringComparison.Ordinal) ||
                string.Equals(entry.EligibilityState, "unsupported_for_repo", StringComparison.Ordinal) ||
                string.Equals(entry.EligibilityState, "unavailable", StringComparison.Ordinal))
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Language Eligibility");
        builder.AppendLine();
        builder.AppendLine($"Default build stack: {FirstNonEmpty(eligibility.PreferredStackLabel, "not recorded")}");
        builder.AppendLine();
        builder.AppendLine("Available and preferred:");
        foreach (var entry in preferred)
        {
            builder.AppendLine($"- {entry.StackLabel}: {entry.Summary}");
        }

        builder.AppendLine();
        builder.AppendLine("Available but not preferred:");
        foreach (var entry in readyNotPreferred)
        {
            builder.AppendLine($"- {entry.StackLabel}: {entry.Summary}");
        }

        builder.AppendLine();
        builder.AppendLine("Blocked or unsupported:");
        foreach (var entry in blocked)
        {
            builder.AppendLine($"- {entry.StackLabel}: {entry.Summary}");
        }

        return builder.ToString().TrimEnd();
    }

    private static BuilderRepoKnowledgeIndex BuildBuilderRepoKnowledgeIndex(
        string repoRoot,
        BuilderToolchainCapabilityRegistry capabilityRegistry,
        BuilderRepoKnowledgeIndex priorIndex)
    {
        var policy = BuildBuilderRepoToolchainPolicySnapshot(repoRoot);
        var observedUtc = DateTimeOffset.UtcNow;
        var solutionPaths = Directory.Exists(repoRoot)
            ? Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Select(path => NormalizeBuilderRepoRelativePath(repoRoot, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var keyDirectories = Directory.Exists(repoRoot)
            ? Directory.GetDirectories(repoRoot)
                .Select(path => NormalizeBuilderRepoRelativePath(repoRoot, path))
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    !string.Equals(path, ".git", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(path, ".codex", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(path, "bin", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(path, "obj", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var parsedProjects = DiscoverBuilderRepoProjects(repoRoot);
        var projectsByRelativePath = parsedProjects.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var projectEntries = parsedProjects
            .Select(project =>
            {
                var relatedTests = BuildBuilderRepoRelatedTestLinks(project, parsedProjects);
                var relatedUiSurfaces = BuildBuilderRepoKnowledgeLinksForPattern(
                    repoRoot,
                    project.AbsolutePath,
                    "*.xaml",
                    path => "direct_observed",
                    path => $"{Path.GetFileName(path)} is directly observed in the project UI surface set.");
                var relatedServices = BuildBuilderRepoKnowledgeLinksForPattern(
                    repoRoot,
                    project.AbsolutePath,
                    "*Service*.cs",
                    path => "direct_observed",
                    path => $"{Path.GetFileName(path)} is directly observed in the project services.");
                var relatedViewModels = BuildBuilderRepoKnowledgeLinksForPattern(
                    repoRoot,
                    project.AbsolutePath,
                    "*ViewModel*.cs",
                    path => "direct_observed",
                    path => $"{Path.GetFileName(path)} is directly observed in the project view-model set.");
                var relatedBuilderFiles = Directory.Exists(Path.GetDirectoryName(project.AbsolutePath) ?? string.Empty)
                    ? Directory.GetFiles(Path.GetDirectoryName(project.AbsolutePath)!, "*.cs", SearchOption.AllDirectories)
                        .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Builder{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .Select(path => new BuilderRepoKnowledgeLinkedItem(
                            NormalizeBuilderRepoRelativePath(repoRoot, path),
                            "direct_observed",
                            $"{Path.GetFileName(path)} is directly observed in the builder area."))
                        .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                        .ToArray()
                    : Array.Empty<BuilderRepoKnowledgeLinkedItem>();
                var relatedProjectIds = project.ProjectReferencePaths
                    .Where(path => projectsByRelativePath.ContainsKey(path))
                    .Select(path => projectsByRelativePath[path].ProjectId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                var featureAreaLabels = BuildBuilderRepoProjectFeatureAreas(
                    project,
                    relatedUiSurfaces,
                    relatedServices,
                    relatedViewModels,
                    relatedBuilderFiles,
                    relatedTests);
                var featureSummary = $"{project.ProjectName} is a {project.ProjectType.Replace('_', ' ')} using {project.InferredStackLabel}. UI surfaces={relatedUiSurfaces.Length}, view-models={relatedViewModels.Length}, services={relatedServices.Length}, builder files={relatedBuilderFiles.Length}, tests={relatedTests.Length}.";
                return new BuilderRepoKnowledgeProjectEntry(
                    project.ProjectId,
                    project.ProjectName,
                    project.RelativePath,
                    project.ProjectType,
                    project.TargetFrameworks,
                    project.InferredStackLabel,
                    "C#",
                    featureAreaLabels,
                    relatedTests,
                    relatedUiSurfaces,
                    relatedServices,
                    relatedViewModels,
                    relatedBuilderFiles,
                    relatedProjectIds,
                    featureSummary,
                    observedUtc);
            })
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProjectId, StringComparer.Ordinal)
            .ToArray();
        var ownershipSummaries = BuildBuilderRepoOwnershipSummaries(projectEntries);
        var linkedArtifactPaths = BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot)
            .Concat(new[]
            {
                capabilityRegistry.ArtifactPath,
                BuilderLanguageEligibilityPathForRepo(repoRoot)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var provisionalIndex = new BuilderRepoKnowledgeIndex(
            policy.PreferredStackId,
            policy.PreferredStackLabel,
            solutionPaths,
            keyDirectories,
            projectEntries,
            ownershipSummaries,
            linkedArtifactPaths,
            "completed",
            "unchanged",
            Array.Empty<string>(),
            string.Empty,
            BuilderRepoKnowledgeIndexPathForRepo(repoRoot),
            observedUtc);
        var drift = BuildBuilderRepoKnowledgeDrift(priorIndex, provisionalIndex);
        var changedProjectIds = drift is null
            ? Array.Empty<string>()
            : drift.AddedProjectIds
                .Concat(drift.RemovedProjectIds)
                .Concat(drift.ChangedProjectIds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        var driftState = priorIndex.ObservedUtc <= DateTimeOffset.MinValue
            ? "initial_scan"
            : changedProjectIds.Length > 0
                ? "changed"
                : "unchanged";
        return provisionalIndex with
        {
            DriftState = driftState,
            ChangedProjectIds = changedProjectIds,
            Summary = BuildBuilderRepoKnowledgeSummary(projectEntries, keyDirectories, policy, driftState)
        };
    }

    private static BuilderRepoKnowledgeHistory BuildBuilderRepoKnowledgeHistory(
        BuilderRepoKnowledgeHistory priorHistory,
        BuilderRepoKnowledgeIndex current)
    {
        var retentionCount = Math.Max(priorHistory.RetentionCount, 12);
        var currentEntry = new BuilderRepoKnowledgeHistoryEntry(
            current.PreferredStackId,
            current.DriftState,
            current.ChangedProjectIds,
            current.ProjectEntries.Count,
            current.Summary,
            current.ArtifactPath,
            current.ObservedUtc);
        var entries = priorHistory.Entries
            .Where(entry => entry.ObservedUtc != currentEntry.ObservedUtc)
            .Concat(new[] { currentEntry })
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.ProjectCount)
            .Take(retentionCount)
            .ToArray();
        var summary = current.DriftState switch
        {
            "changed" => $"Latest repo knowledge refresh changed {current.ChangedProjectIds.Count} project definition(s).",
            "initial_scan" => "Initial repo knowledge refresh completed.",
            _ => "Latest repo knowledge refresh found no structural drift."
        };
        return new BuilderRepoKnowledgeHistory(
            retentionCount,
            entries,
            summary,
            Path.Combine(Path.GetDirectoryName(current.ArtifactPath) ?? string.Empty, "builder_repo_knowledge_history.json"),
            current.ObservedUtc);
    }

    private static BuilderRepoKnowledgeDrift? BuildBuilderRepoKnowledgeDrift(
        BuilderRepoKnowledgeIndex priorIndex,
        BuilderRepoKnowledgeIndex currentIndex)
    {
        if (priorIndex.ObservedUtc <= DateTimeOffset.MinValue)
        {
            return null;
        }

        var priorSignatures = BuildBuilderRepoKnowledgeSignatureMap(priorIndex.ProjectEntries);
        var currentSignatures = BuildBuilderRepoKnowledgeSignatureMap(currentIndex.ProjectEntries);
        var added = currentSignatures.Keys.Except(priorSignatures.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var removed = priorSignatures.Keys.Except(currentSignatures.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var changed = currentSignatures.Keys
            .Intersect(priorSignatures.Keys, StringComparer.Ordinal)
            .Where(id => !string.Equals(currentSignatures[id], priorSignatures[id], StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (added.Length == 0 && removed.Length == 0 && changed.Length == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (added.Length > 0) parts.Add($"added: {string.Join(", ", added)}");
        if (removed.Length > 0) parts.Add($"removed: {string.Join(", ", removed)}");
        if (changed.Length > 0) parts.Add($"changed: {string.Join(", ", changed)}");

        return new BuilderRepoKnowledgeDrift(
            added,
            removed,
            changed,
            $"Repo structure drift detected with {string.Join("; ", parts)}.",
            Path.Combine(Path.GetDirectoryName(currentIndex.ArtifactPath) ?? string.Empty, "builder_repo_knowledge_drift.json"),
            currentIndex.ObservedUtc);
    }

    private static IReadOnlyDictionary<string, string> BuildBuilderRepoKnowledgeSignatureMap(IReadOnlyList<BuilderRepoKnowledgeProjectEntry> entries)
        => entries
            .GroupBy(entry => entry.ProjectId, StringComparer.Ordinal)
            .SelectMany(group =>
                group.Count() == 1
                    ? group.Select(entry => new KeyValuePair<string, string>(entry.ProjectId, ComputeBuilderRepoKnowledgeProjectSignature(entry)))
                    : group.Select(entry => new KeyValuePair<string, string>($"{entry.ProjectId} ({entry.RelativePath})", ComputeBuilderRepoKnowledgeProjectSignature(entry))))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static string BuildBuilderRepoKnowledgeSummaryMarkdown(BuilderRepoKnowledgeIndex index)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Repo Knowledge");
        builder.AppendLine();
        builder.AppendLine($"Preferred working stack: {FirstNonEmpty(index.PreferredStackLabel, "not recorded")}");
        builder.AppendLine($"Refresh state: {index.RefreshState} / {index.DriftState}");
        builder.AppendLine();
        builder.AppendLine("Projects:");
        foreach (var entry in index.ProjectEntries)
        {
            builder.AppendLine($"- {entry.ProjectName}: {entry.FeatureSummary}");
        }

        builder.AppendLine();
        builder.AppendLine("Major areas:");
        foreach (var summary in index.FileOwnershipSummaries.Take(8))
        {
            builder.AppendLine($"- {summary.RelativePath}: {summary.Summary}");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<BuilderParsedRepoProject> DiscoverBuilderRepoProjects(string repoRoot)
        => Directory.Exists(repoRoot)
            ? Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(path => ParseBuilderRepoProject(repoRoot, path))
                .GroupBy(project => project.ProjectId, StringComparer.Ordinal)
                .SelectMany(group =>
                    group.Count() == 1
                        ? group
                        : group.Select(project => project with
                        {
                            ProjectId = $"{project.ProjectName}__{Path.ChangeExtension(project.RelativePath, null)?.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_')}"
                        }))
                .OrderBy(project => project.RelativePath, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<BuilderParsedRepoProject>();

    private static BuilderParsedRepoProject ParseBuilderRepoProject(string repoRoot, string projectPath)
    {
        var text = File.ReadAllText(projectPath);
        var targetFrameworks = System.Text.RegularExpressions.Regex.Matches(
                text,
                @"<TargetFrameworks?>(?<value>[^<]+)</TargetFrameworks?>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .SelectMany(match => match.Groups["value"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var isTestProject = text.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase) ||
                            projectPath.Contains(".Tests", StringComparison.OrdinalIgnoreCase);
        var useWpf = text.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase);
        var relativePath = NormalizeBuilderRepoRelativePath(repoRoot, projectPath);
        var projectReferencePaths = System.Text.RegularExpressions.Regex.Matches(
                text,
                "<ProjectReference\\s+Include=\"(?<value>[^\"]+)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Groups["value"].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value =>
            {
                var absolute = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath) ?? repoRoot, value));
                return NormalizeBuilderRepoRelativePath(repoRoot, absolute);
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        return new BuilderParsedRepoProject(
            projectName,
            projectName,
            projectPath,
            relativePath,
            DetermineBuilderRepoProjectType(relativePath, useWpf, isTestProject),
            DetermineBuilderRepoProjectStackLabel(useWpf, targetFrameworks),
            targetFrameworks,
            useWpf,
            isTestProject,
            projectReferencePaths);
    }

    private static string DetermineBuilderRepoProjectType(string relativePath, bool useWpf, bool isTestProject)
    {
        if (isTestProject)
        {
            return "test_project";
        }

        if (useWpf)
        {
            return "wpf_desktop_app";
        }

        if (relativePath.Contains($"{Path.DirectorySeparatorChar}Runtime{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return "runtime_library";
        }

        if (relativePath.Contains($"{Path.DirectorySeparatorChar}Builder{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return "builder_library";
        }

        if (relativePath.StartsWith($"ui{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return "ui_project";
        }

        return "library";
    }

    private static string DetermineBuilderRepoProjectStackLabel(bool useWpf, IReadOnlyList<string> targetFrameworks)
    {
        if (useWpf)
        {
            return "WPF/Desktop .NET";
        }

        if (targetFrameworks.Any(framework => framework.Contains("windows", StringComparison.OrdinalIgnoreCase)))
        {
            return ".NET Desktop";
        }

        return ".NET / C#";
    }

    private static IReadOnlyList<string> BuildBuilderRepoProjectFeatureAreas(
        BuilderParsedRepoProject project,
        IReadOnlyList<BuilderRepoKnowledgeLinkedItem> uiSurfaces,
        IReadOnlyList<BuilderRepoKnowledgeLinkedItem> services,
        IReadOnlyList<BuilderRepoKnowledgeLinkedItem> viewModels,
        IReadOnlyList<BuilderRepoKnowledgeLinkedItem> builderFiles,
        IReadOnlyList<BuilderRepoKnowledgeLinkedItem> tests)
    {
        var labels = new SortedSet<string>(StringComparer.Ordinal);
        if (builderFiles.Count > 0) labels.Add("Builder");
        if (uiSurfaces.Count > 0) labels.Add("XamlSurfaces");
        if (viewModels.Count > 0) labels.Add("ViewModels");
        if (services.Count > 0) labels.Add("Services");
        if (tests.Count > 0 || project.IsTestProject) labels.Add("Tests");
        if (project.RelativePath.Contains($"{Path.DirectorySeparatorChar}Runtime{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) labels.Add("Runtime");
        if (project.RelativePath.StartsWith($"ui{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) labels.Add("UI");
        return labels.ToArray();
    }

    private static BuilderRepoKnowledgeLinkedItem[] BuildBuilderRepoKnowledgeLinksForPattern(
        string repoRoot,
        string projectPath,
        string pattern,
        Func<string, string> linkageState,
        Func<string, string> summary)
    {
        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<BuilderRepoKnowledgeLinkedItem>();
        }

        return Directory.GetFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new BuilderRepoKnowledgeLinkedItem(
                NormalizeBuilderRepoRelativePath(repoRoot, path),
                linkageState(path),
                summary(path)))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static BuilderRepoKnowledgeLinkedItem[] BuildBuilderRepoRelatedTestLinks(
        BuilderParsedRepoProject project,
        IReadOnlyList<BuilderParsedRepoProject> allProjects)
        => allProjects
            .Where(candidate => candidate.IsTestProject && !string.Equals(candidate.ProjectId, project.ProjectId, StringComparison.Ordinal))
            .Select(candidate =>
            {
                var direct = candidate.ProjectReferencePaths.Contains(project.RelativePath, StringComparer.Ordinal);
                var inferred = !direct &&
                               (candidate.ProjectName.Contains(project.ProjectName, StringComparison.OrdinalIgnoreCase) ||
                                project.ProjectName.Contains(candidate.ProjectName.Replace(".Tests", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase));
                if (!direct && !inferred)
                {
                    return null;
                }

                return new BuilderRepoKnowledgeLinkedItem(
                    candidate.RelativePath,
                    direct ? "direct_observed" : "inferred",
                    direct
                        ? $"{candidate.ProjectName} references {project.ProjectName}."
                        : $"{candidate.ProjectName} is inferred to cover {project.ProjectName} by naming pattern.");
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();

    private static BuilderRepoKnowledgeOwnershipSummary[] BuildBuilderRepoOwnershipSummaries(
        IReadOnlyList<BuilderRepoKnowledgeProjectEntry> projectEntries)
    {
        var summaries = new List<BuilderRepoKnowledgeOwnershipSummary>();
        foreach (var entry in projectEntries)
        {
            var projectDirectory = Path.GetDirectoryName(entry.RelativePath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(projectDirectory))
            {
                summaries.Add(new BuilderRepoKnowledgeOwnershipSummary(
                    projectDirectory,
                    entry.ProjectId,
                    entry.ProjectName,
                    "direct_observed",
                    $"{projectDirectory} is directly owned by {entry.ProjectName}."));
            }

            if (entry.RelatedBuilderFiles.Count > 0)
            {
                summaries.Add(new BuilderRepoKnowledgeOwnershipSummary(
                    Path.Combine(projectDirectory, "Builder"),
                    entry.ProjectId,
                    entry.ProjectName,
                    "direct_observed",
                    $"{entry.ProjectName} directly owns the builder area."));
            }

            if (entry.RelatedViewModels.Count > 0)
            {
                summaries.Add(new BuilderRepoKnowledgeOwnershipSummary(
                    Path.Combine(projectDirectory, "ViewModels"),
                    entry.ProjectId,
                    entry.ProjectName,
                    "direct_observed",
                    $"{entry.ProjectName} directly owns the view-model area."));
            }

            if (entry.RelatedServices.Count > 0)
            {
                summaries.Add(new BuilderRepoKnowledgeOwnershipSummary(
                    Path.Combine(projectDirectory, "Services"),
                    entry.ProjectId,
                    entry.ProjectName,
                    "direct_observed",
                    $"{entry.ProjectName} directly owns the service area."));
            }
        }

        return summaries
            .Where(summary => !string.IsNullOrWhiteSpace(summary.RelativePath))
            .GroupBy(summary => summary.RelativePath, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(summary => summary.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeBuilderRepoRelativePath(string repoRoot, string path)
        => Path.GetRelativePath(repoRoot, path);

    private static string BuildBuilderRepoKnowledgeSummary(
        IReadOnlyList<BuilderRepoKnowledgeProjectEntry> projectEntries,
        IReadOnlyList<string> keyDirectories,
        BuilderRepoToolchainPolicySnapshot policy,
        string driftState)
    {
        var projectSummary = projectEntries.Count == 0 ? "no projects detected" : string.Join(", ", projectEntries.Take(4).Select(entry => entry.ProjectName));
        var directorySummary = keyDirectories.Count == 0 ? "none" : string.Join(", ", keyDirectories.Take(4));
        return $"Repo is primarily {FirstNonEmpty(policy.PreferredStackLabel, "not classified")}. Projects: {projectSummary}. Key directories: {directorySummary}. Refresh completed with {driftState}.";
    }

    private static string ComputeBuilderRepoKnowledgeProjectSignature(BuilderRepoKnowledgeProjectEntry entry)
        => string.Join(
            "|",
            entry.RelativePath,
            entry.ProjectType,
            string.Join(",", entry.TargetFrameworks),
            entry.InferredStackLabel,
            entry.RelatedTests.Count,
            entry.RelatedUiSurfaces.Count,
            entry.RelatedServices.Count,
            entry.RelatedViewModels.Count,
            entry.RelatedBuilderFiles.Count,
            entry.FeatureSummary);

    private static string NormalizeBuilderConversationTaskClass(string rawRequestText)
    {
        var request = rawRequestText?.ToLowerInvariant() ?? string.Empty;
        if (request.Contains("fix", StringComparison.Ordinal) ||
            request.Contains("error", StringComparison.Ordinal) ||
            request.Contains("compile", StringComparison.Ordinal))
        {
            return "compile_fix";
        }

        if (request.Contains("test", StringComparison.Ordinal) ||
            request.Contains("coverage", StringComparison.Ordinal) ||
            request.Contains("assert", StringComparison.Ordinal))
        {
            return "test_extension";
        }

        return "bounded_refactor";
    }

    private static string ResolveBuilderConversationImpliedStackId(string rawRequestText, BuilderRepoKnowledgeIndex knowledgeIndex)
    {
        var request = rawRequestText?.ToLowerInvariant() ?? string.Empty;
        if (request.Contains("javascript", StringComparison.Ordinal) ||
            request.Contains("typescript", StringComparison.Ordinal) ||
            request.Contains("node", StringComparison.Ordinal))
        {
            return "javascript_typescript";
        }

        if (request.Contains("python", StringComparison.Ordinal))
        {
            return "python";
        }

        if (request.Contains("java", StringComparison.Ordinal))
        {
            return "java";
        }

        if (request.Contains("c++", StringComparison.Ordinal) ||
            request.Contains("cpp", StringComparison.Ordinal) ||
            request.Contains("cmake", StringComparison.Ordinal))
        {
            return "cpp_native";
        }

        if (request.Contains("wpf", StringComparison.Ordinal) ||
            request.Contains("xaml", StringComparison.Ordinal) ||
            request.Contains("viewmodel", StringComparison.Ordinal) ||
            request.Contains("mainwindow", StringComparison.Ordinal) ||
            request.Contains("ui", StringComparison.Ordinal))
        {
            return "wpf_desktop_dotnet";
        }

        return FirstNonEmpty(knowledgeIndex.PreferredStackId, "csharp_dotnet");
    }

    private static BuilderRepoRetrievalContext BuildBuilderRepoRetrievalContext(
        string repoRoot,
        BuilderRepoKnowledgeIndex knowledgeIndex,
        string rawRequestText,
        string normalizedTaskClass,
        string impliedStackId,
        string impliedStackLabel)
    {
        var normalizedRawRequestText = rawRequestText ?? string.Empty;
        var tokens = System.Text.RegularExpressions.Regex.Split(normalizedRawRequestText, @"[^a-zA-Z0-9\.\+#]+")
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token =>
                !string.IsNullOrWhiteSpace(token) &&
                token.Length >= 3 &&
                token is not "the" and not "thing" and not "with" and not "that" and not "this" and not "into" and not "from" and not "then")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var requestLower = normalizedRawRequestText.ToLowerInvariant();
        var scoredProjects = knowledgeIndex.ProjectEntries
            .Select(entry => new
            {
                Entry = entry,
                Score = ScoreBuilderRepoKnowledgeProject(entry, tokens, requestLower, impliedStackLabel)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Entry.ProjectId, StringComparer.Ordinal)
            .ToArray();
        var topScore = scoredProjects.FirstOrDefault()?.Score ?? 0;
        var confidenceState = topScore switch
        {
            >= 8 => "strong_match",
            >= 4 => "plausible_match",
            >= 1 => "weak_match_needs_operator_review",
            _ => "no_clear_match"
        };
        var matchedProjects = scoredProjects.Take(3).Select(result => result.Entry).ToArray();
        var matchedProjectIds = matchedProjects.Select(entry => entry.ProjectId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var matchedTests = matchedProjects.SelectMany(entry => CollectBuilderRepoMatchedLinkedPaths(entry.RelatedTests, tokens, requestLower)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var matchedUiSurfaces = matchedProjects.SelectMany(entry => CollectBuilderRepoMatchedLinkedPaths(entry.RelatedUiSurfaces, tokens, requestLower)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var matchedServices = matchedProjects.SelectMany(entry => CollectBuilderRepoMatchedLinkedPaths(entry.RelatedServices, tokens, requestLower)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var matchedViewModels = matchedProjects.SelectMany(entry => CollectBuilderRepoMatchedLinkedPaths(entry.RelatedViewModels, tokens, requestLower)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var matchedFiles = matchedProjects
            .SelectMany(entry => CollectBuilderRepoMatchedLinkedPaths(entry.RelatedBuilderFiles, tokens, requestLower))
            .Concat(matchedUiSurfaces)
            .Concat(matchedServices)
            .Concat(matchedViewModels)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var consultedArtifacts = BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot)
            .Concat(new[] { knowledgeIndex.ArtifactPath })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = confidenceState switch
        {
            "strong_match" => $"Strong repo match for {impliedStackLabel}. Projects={FirstNonEmpty(string.Join(", ", matchedProjectIds), "none")}.",
            "plausible_match" => $"Plausible repo match for {impliedStackLabel}. Projects={FirstNonEmpty(string.Join(", ", matchedProjectIds), "none")}.",
            "weak_match_needs_operator_review" => $"Weak repo match for {impliedStackLabel}; operator review is required before launch.",
            _ => $"No clear repo match for {impliedStackLabel}; operator review is required before launch."
        };
        return new BuilderRepoRetrievalContext(
            normalizedRawRequestText,
            normalizedTaskClass,
            impliedStackId,
            impliedStackLabel,
            confidenceState,
            matchedProjectIds,
            matchedFiles,
            matchedTests,
            matchedUiSurfaces,
            matchedServices,
            matchedViewModels,
            string.Equals(impliedStackId, knowledgeIndex.PreferredStackId, StringComparison.Ordinal)
                ? "preferred_stack_aligned"
                : "non_preferred_stack_requested",
            consultedArtifacts,
            summary,
            BuilderRepoRetrievalContextPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static int ScoreBuilderRepoKnowledgeProject(
        BuilderRepoKnowledgeProjectEntry entry,
        IReadOnlyList<string> tokens,
        string requestLower,
        string impliedStackLabel)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(entry.ProjectName) &&
            requestLower.Contains(entry.ProjectName.ToLowerInvariant(), StringComparison.Ordinal))
        {
            score += 8;
        }

        foreach (var token in tokens)
        {
            if (entry.ProjectName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }

            if (entry.RelativePath.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (entry.FeatureAreaLabels.Any(label => label.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score += 3;
            }
        }

        if (entry.InferredStackLabel.Contains(impliedStackLabel, StringComparison.OrdinalIgnoreCase) ||
            impliedStackLabel.Contains(entry.InferredStackLabel, StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        score += entry.RelatedUiSurfaces.Count(item => requestLower.Contains(Path.GetFileName(item.RelativePath).ToLowerInvariant(), StringComparison.Ordinal)) * 6;
        score += entry.RelatedViewModels.Count(item => requestLower.Contains(Path.GetFileNameWithoutExtension(item.RelativePath).ToLowerInvariant(), StringComparison.Ordinal)) * 6;
        score += entry.RelatedServices.Count(item => requestLower.Contains(Path.GetFileNameWithoutExtension(item.RelativePath).ToLowerInvariant(), StringComparison.Ordinal)) * 5;
        score += entry.RelatedBuilderFiles.Count(item => requestLower.Contains(Path.GetFileNameWithoutExtension(item.RelativePath).ToLowerInvariant(), StringComparison.Ordinal) ||
                                                          requestLower.Contains("builder", StringComparison.Ordinal)) * 2;
        return score;
    }

    private static IReadOnlyList<string> CollectBuilderRepoMatchedLinkedPaths(
        IReadOnlyList<BuilderRepoKnowledgeLinkedItem> items,
        IReadOnlyList<string> tokens,
        string requestLower)
        => items
            .Where(item =>
                requestLower.Contains(Path.GetFileName(item.RelativePath).ToLowerInvariant(), StringComparison.Ordinal) ||
                requestLower.Contains(Path.GetFileNameWithoutExtension(item.RelativePath).ToLowerInvariant(), StringComparison.Ordinal) ||
                tokens.Any(token => item.RelativePath.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.RelativePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string DetermineBuilderConversationCapabilityRoutingState(
        BuilderLanguageEligibility languageEligibility,
        string impliedStackId)
    {
        var entry = languageEligibility.Entries.FirstOrDefault(candidate => string.Equals(candidate.StackId, impliedStackId, StringComparison.Ordinal));
        return DetermineBuilderCapabilityRoutingState(impliedStackId, impliedStackId, entry?.EligibilityState ?? "unsupported_for_repo");
    }

    private static string BuildBuilderConversationCapabilitySummary(
        BuilderLanguageEligibility languageEligibility,
        string impliedStackId,
        string capabilityRoutingState)
    {
        var entry = languageEligibility.Entries.FirstOrDefault(candidate => string.Equals(candidate.StackId, impliedStackId, StringComparison.Ordinal));
        return entry is null
            ? "No capability decision recorded for the implied stack."
            : capabilityRoutingState switch
            {
                "route_blocked_missing_toolchain" => entry.BlockedReason,
                "route_blocked_repo_policy" => entry.BlockedReason,
                _ => entry.Summary
            };
    }

    private static IReadOnlyList<string> GetBuilderModelTaskClassCandidates(string normalizedTaskClass)
        => normalizedTaskClass switch
        {
            "compile_fix" => new[] { "compile_fix", "compile_fix_edit" },
            "bounded_refactor" => new[] { "bounded_refactor", "ui_feature_addition", "service_feature_addition" },
            _ => new[] { normalizedTaskClass }
        };

    private static BuilderModelRoutingPolicyEntry? FindBuilderModelRoutingPolicyEntry(
        BuilderModelRoutingPolicy policy,
        string normalizedTaskClass)
    {
        var candidates = GetBuilderModelTaskClassCandidates(normalizedTaskClass);
        return policy.Entries
            .OrderBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .FirstOrDefault(entry => candidates.Contains(entry.TaskClass, StringComparer.Ordinal));
    }

    private static BuilderModelCapabilityMatrixEntry? FindBuilderModelCapabilityMatrixEntry(
        BuilderModelCapabilityMatrix matrix,
        string normalizedTaskClass)
    {
        var candidates = GetBuilderModelTaskClassCandidates(normalizedTaskClass);
        return matrix.Entries
            .OrderBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .FirstOrDefault(entry => candidates.Contains(entry.TaskClass, StringComparer.Ordinal));
    }

    private static BuilderModelRoutingStabilityEntry? FindBuilderModelRoutingStabilityEntry(
        BuilderModelRoutingStability stability,
        string normalizedTaskClass)
    {
        var candidates = GetBuilderModelTaskClassCandidates(normalizedTaskClass);
        return stability.Entries
            .OrderBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .FirstOrDefault(entry => candidates.Contains(entry.TaskClass, StringComparer.Ordinal));
    }

    private static BuilderModelDecision BuildBuilderModelDecision(
        string repoRoot,
        string requestId,
        BuilderConversationIntake intake,
        BuilderModelCapabilityMatrix matrix,
        BuilderModelRoutingPolicy routingPolicy,
        BuilderModelRoutingStability stability)
    {
        var policyEntry = FindBuilderModelRoutingPolicyEntry(routingPolicy, intake.NormalizedTaskClass);
        var matrixEntry = FindBuilderModelCapabilityMatrixEntry(matrix, intake.NormalizedTaskClass);
        var stabilityEntry = FindBuilderModelRoutingStabilityEntry(stability, intake.NormalizedTaskClass);
        var strongerTierAvailability = LoadLatestBuilderStrongerTierAvailability(repoRoot);
        var selectedModelTier = FirstNonEmpty(policyEntry?.PreferredModelTier, "low_floor_model_tier");
        var selectedModelId = string.Equals(selectedModelTier, "stronger_builder_tier", StringComparison.Ordinal)
            ? FirstNonEmpty(strongerTierAvailability?.ConfiguredStrongerTierId, routingPolicy.PreferredStrongerModelId, "unavailable")
            : BuilderProofFloorModelId;
        var capabilityState = matrixEntry?.CapabilityState ?? "not_yet_proven";
        var decisionReason = FirstNonEmpty(
            policyEntry?.Summary,
            matrixEntry?.Summary,
            stabilityEntry?.Summary,
            "No explicit model routing evidence was recorded.");
        var linkedArtifactPaths = new[]
        {
            routingPolicy.ArtifactPath,
            routingPolicy.SummaryArtifactPath,
            matrix.ArtifactPath,
            stability.ArtifactPath,
            strongerTierAvailability?.ArtifactPath ?? string.Empty,
            intake.ArtifactPath
        }
            .Concat(policyEntry?.LinkedEvidencePaths ?? Array.Empty<string>())
            .Concat(matrixEntry?.LinkedProofArtifactPaths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{intake.NormalizedTaskClass} selects {selectedModelTier} ({selectedModelId}) with capability {capabilityState}. {decisionReason}";
        return new BuilderModelDecision(
            requestId,
            intake.RawRequestText,
            intake.NormalizedTaskClass,
            selectedModelTier,
            selectedModelId,
            capabilityState,
            matrixEntry?.StrongerTierRecommendationState ?? "not_needed",
            matrixEntry?.StrongerTierRequirementState ?? "not_required",
            matrixEntry?.SplitFirstRequired ?? false,
            stabilityEntry?.StabilityState ?? "provisional",
            decisionReason,
            linkedArtifactPaths,
            summary,
            BuilderModelDecisionPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelEscalationPolicyDecision BuildBuilderModelEscalationPolicyDecision(
        string repoRoot,
        BuilderModelDecision modelDecision)
    {
        var strongerTierAvailability = LoadLatestBuilderStrongerTierAvailability(repoRoot);
        var splitFirstViabilityState = modelDecision.SplitFirstKeepsLowFloorViable ? "viable" : "not_viable";
        var strongerTierAvailabilityState = strongerTierAvailability?.AvailabilityState ?? "unknown";
        string finalDecisionState;
        string blockReason;
        if (string.Equals(modelDecision.CapabilityState, "not_yet_proven", StringComparison.Ordinal))
        {
            finalDecisionState = "not_yet_proven";
            blockReason = "No model capability proof currently supports this task class.";
        }
        else if (string.Equals(modelDecision.StrongerTierRequirementState, "required", StringComparison.Ordinal) &&
                 !string.Equals(strongerTierAvailabilityState, "available", StringComparison.Ordinal))
        {
            finalDecisionState = "blocked_required_stronger_tier_unavailable";
            blockReason = FirstNonEmpty(
                strongerTierAvailability?.Summary,
                "A stronger builder tier is required for this task class, but no supported stronger tier is currently available.");
        }
        else if (string.Equals(modelDecision.StrongerTierRequirementState, "required", StringComparison.Ordinal))
        {
            finalDecisionState = "stronger_tier_required";
            blockReason = string.Empty;
        }
        else if (string.Equals(modelDecision.StrongerTierRecommendationState, "recommended", StringComparison.Ordinal))
        {
            finalDecisionState = string.Equals(strongerTierAvailabilityState, "available", StringComparison.Ordinal)
                ? "stronger_tier_recommended"
                : "stronger_tier_recommended_but_unavailable";
            blockReason = string.Empty;
        }
        else
        {
            finalDecisionState = modelDecision.CapabilityState switch
            {
                "low_floor_split_first_supported" => "low_floor_via_split_first",
                "low_floor_supported_with_repair_loop" => "low_floor_with_repair_loop",
                _ => "low_floor_direct"
            };
            blockReason = string.Empty;
        }

        var linkedArtifactPaths = new[]
        {
            modelDecision.ArtifactPath,
            strongerTierAvailability?.ArtifactPath ?? string.Empty
        }
            .Concat(modelDecision.LinkedArtifactPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = string.IsNullOrWhiteSpace(blockReason)
            ? $"{modelDecision.NormalizedTaskClass} resolved to {finalDecisionState}. Stronger-tier state={strongerTierAvailabilityState}."
            : $"{modelDecision.NormalizedTaskClass} is blocked: {blockReason}";
        return new BuilderModelEscalationPolicyDecision(
            modelDecision.RequestId,
            modelDecision.NormalizedTaskClass,
            modelDecision.CapabilityState,
            modelDecision.StrongerTierRecommendationState,
            splitFirstViabilityState,
            strongerTierAvailabilityState,
            finalDecisionState,
            blockReason,
            linkedArtifactPaths,
            summary,
            BuilderModelEscalationPolicyDecisionPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static string DetermineBuilderConversationLaunchReadinessState(
        string retrievalConfidenceState,
        string capabilityRoutingState,
        string selectedRoute,
        BuilderModelEscalationPolicyDecision escalationPolicyDecision)
    {
        if (string.Equals(escalationPolicyDecision.FinalDecisionState, "blocked_required_stronger_tier_unavailable", StringComparison.Ordinal) ||
            string.Equals(escalationPolicyDecision.FinalDecisionState, "not_yet_proven", StringComparison.Ordinal))
        {
            return "launch_blocked_model_policy";
        }

        if (string.Equals(capabilityRoutingState, "route_blocked_missing_toolchain", StringComparison.Ordinal) ||
            string.Equals(capabilityRoutingState, "route_blocked_repo_policy", StringComparison.Ordinal))
        {
            return "launch_blocked_capability";
        }

        if (string.Equals(retrievalConfidenceState, "weak_match_needs_operator_review", StringComparison.Ordinal) ||
            string.Equals(retrievalConfidenceState, "no_clear_match", StringComparison.Ordinal))
        {
            return "launch_blocked_weak_match";
        }

        if (string.IsNullOrWhiteSpace(selectedRoute))
        {
            return "launch_blocked_route";
        }

        return IsBuilderPreparedRouteSupported(selectedRoute)
            ? "ready_for_operator_approval"
            : "launch_blocked_model_policy";
    }

    private static string BuildBuilderConversationBlockReason(
        BuilderRepoRetrievalContext retrieval,
        BuilderLanguageEligibility languageEligibility,
        string impliedStackId,
        string capabilityRoutingState,
        string launchReadinessState,
        string selectedRoute,
        BuilderModelEscalationPolicyDecision escalationPolicyDecision)
    {
        var entry = languageEligibility.Entries.FirstOrDefault(candidate => string.Equals(candidate.StackId, impliedStackId, StringComparison.Ordinal));
        if (string.Equals(launchReadinessState, "launch_blocked_model_policy", StringComparison.Ordinal))
        {
            return FirstNonEmpty(
                escalationPolicyDecision.BlockReason,
                escalationPolicyDecision.Summary,
                "Builder conversation is blocked by model routing policy.");
        }

        if (string.Equals(launchReadinessState, "launch_blocked_capability", StringComparison.Ordinal))
        {
            return entry?.BlockedReason ?? "Builder conversation is blocked by capability state.";
        }

        if (string.Equals(launchReadinessState, "launch_blocked_weak_match", StringComparison.Ordinal))
        {
            return $"Builder conversation retrieval is {retrieval.RetrievalConfidenceState}; explicit operator override is required before launch.";
        }

        if (string.Equals(launchReadinessState, "launch_blocked_route", StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(selectedRoute)
                ? "Builder conversation could not resolve a prepared route from current authoritative artifacts."
                : $"Builder conversation route {selectedRoute} still requires operator review before launch.";
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> BuildBuilderConversationAuthoritativeArtifactPaths(string repoRoot)
        => new[]
        {
            BuilderRouteCurrentStateIndexPathForRepo(repoRoot),
            BuilderRouteStateContinuityPathForRepo(repoRoot),
            BuilderModelCapabilityMatrixPathForRepo(repoRoot),
            BuilderModelRoutingPolicyPathForRepo(repoRoot),
            BuilderModelRoutingStabilityPathForRepo(repoRoot),
            BuilderModelDecisionPathForRepo(repoRoot),
            BuilderModelEscalationPolicyDecisionPathForRepo(repoRoot),
            BuilderToolchainCapabilityRegistryPathForRepo(repoRoot),
            BuilderLanguageEligibilityPathForRepo(repoRoot),
            TryResolveBuilderRouteArtifactPathFromCurrentStateIndex(repoRoot, "builder_default_route_decision"),
            TryResolveBuilderRouteArtifactPathFromCurrentStateIndex(repoRoot, "builder_launch_default_decision"),
            TryResolveBuilderRouteArtifactPathFromCurrentStateIndex(repoRoot, "builder_route_override_evidence"),
            TryResolveBuilderRouteArtifactPathFromCurrentStateIndex(repoRoot, "builder_route_reconfirmation")
        }
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string BuildBuilderConversationIntakeSummary(
        string rawRequestText,
        string normalizedTaskClass,
        string impliedStackLabel,
        BuilderRepoRetrievalContext retrieval,
        string capabilitySummary,
        string modelDecisionSummary,
        string selectedRoute,
        string routeSourceState,
        string launchReadinessState,
        string blockReason)
    {
        var builder = new StringBuilder();
        builder.Append($"Conversation request \"{FirstNonEmpty(rawRequestText, "not recorded")}\" normalized to {normalizedTaskClass}. ");
        builder.Append($"Implied stack: {impliedStackLabel}. ");
        builder.Append($"{retrieval.Summary} ");
        builder.Append($"{capabilitySummary} ");
        if (!string.IsNullOrWhiteSpace(modelDecisionSummary))
        {
            builder.Append($"{modelDecisionSummary} ");
        }

        if (!string.IsNullOrWhiteSpace(selectedRoute))
        {
            builder.Append($"Selected route: {selectedRoute} from {routeSourceState}. ");
        }

        builder.Append(string.IsNullOrWhiteSpace(blockReason) ? $"Launch state: {launchReadinessState}." : blockReason);
        return builder.ToString().Trim();
    }

    private static string BuildBuilderConversationHandoffSummary(
        BuilderConversationIntake intake,
        string operatorDecisionState,
        string selectedRoute,
        string launchReadinessState,
        string blockReason,
        string overrideReason)
    {
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            return $"{operatorDecisionState} left the builder conversation blocked: {blockReason}";
        }

        if (string.Equals(operatorDecisionState, "override_route", StringComparison.Ordinal))
        {
            return $"{operatorDecisionState} selected {selectedRoute}. {FirstNonEmpty(overrideReason, "No override reason recorded.")}";
        }

        return $"{operatorDecisionState} kept route {selectedRoute} ready with launch state {launchReadinessState}.";
    }

    private static string BuildBuilderConversationIntakeId(BuilderConversationIntake intake)
        => $"conversation-intake-{intake.ObservedUtc.UtcDateTime:yyyyMMddHHmmssfff}-{SanitizeBuilderProofToken(FirstNonEmpty(intake.NormalizedTaskClass, "unknown"))}";

    private static string BuildBuilderConversationHandoffId(BuilderConversationHandoff handoff)
        => $"conversation-handoff-{handoff.ObservedUtc.UtcDateTime:yyyyMMddHHmmssfff}-{SanitizeBuilderProofToken(FirstNonEmpty(handoff.SelectedRoute, handoff.OperatorDecisionState, "unknown"))}";

    private static string BuildBuilderConversationExecutionSessionId(BuilderConversationHandoff handoff)
        => $"conversation-session-{handoff.ObservedUtc.UtcDateTime:yyyyMMddHHmmssfff}-{SanitizeBuilderProofToken(FirstNonEmpty(handoff.SelectedRoute, "unknown"))}";

    private static string WriteBuilderStarterStateManifest(string targetFolder)
    {
        var manifestPath = Path.Combine(targetFolder, "starter_state_manifest.json");
        var snapshot = CaptureBuilderReviewableTargetSnapshot(targetFolder, Path.Combine(targetFolder, "starter_state"));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        return manifestPath;
    }

    private static IReadOnlyList<BuilderReviewableFileSnapshotEntry> CaptureBuilderReviewableTargetSnapshot(string rootFolder, string? snapshotRoot = null)
        => Directory.Exists(rootFolder)
            ? Directory.GetFiles(rootFolder, "*", SearchOption.AllDirectories)
                .Where(path => IsBuilderReviewableTargetFile(rootFolder, path))
                .Select(path =>
                {
                    var relativePath = NormalizeBuilderRepoRelativePath(rootFolder, path);
                    var snapshotPath = string.Empty;
                    if (!string.IsNullOrWhiteSpace(snapshotRoot))
                    {
                        snapshotPath = Path.Combine(snapshotRoot, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                        File.Copy(path, snapshotPath, overwrite: true);
                    }

                    return new BuilderReviewableFileSnapshotEntry(
                        relativePath,
                        ComputeBuilderFileHash(path),
                        new FileInfo(path).Length,
                        snapshotPath);
                })
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<BuilderReviewableFileSnapshotEntry>();

    private static IReadOnlyList<BuilderReviewableFileSnapshotEntry> LoadBuilderReviewableTargetSnapshot(string manifestPath)
        => TryLoadBuilderProofArtifact(
            manifestPath,
            Array.Empty<BuilderReviewableFileSnapshotEntry>());

    private static bool IsBuilderReviewableTargetFile(string rootFolder, string filePath)
    {
        var relativePath = NormalizeBuilderRepoRelativePath(rootFolder, filePath);
        if (relativePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        if (string.Equals(fileName, "starter_state_manifest.json", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains($"{Path.DirectorySeparatorChar}starter_state{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeBuilderFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static BuilderPatchReview BuildBuilderPatchReview(
        string repoRoot,
        string sessionId,
        string intakeId,
        string handoffId,
        BuilderConversationIntake intake,
        BuilderConversationHandoff handoff,
        BuilderExecutionPrep? prep,
        PreparedBuilderExecutionResult result)
    {
        var changedFiles = BuildBuilderPatchReviewChangedFiles(result);
        var validationSummary = BuildBuilderConversationValidationSummary(result);
        var reviewReadinessState = changedFiles.Count > 0
            ? "ready_for_operator_review"
            : result.LinkedArtifactPaths.Any(path => !string.IsNullOrWhiteSpace(path) && path.Contains("followup", StringComparison.OrdinalIgnoreCase))
                ? "followup_only"
                : "blocked_no_candidate_changes";
        var linkedArtifactPaths = BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot)
            .Concat(new[]
            {
                intake.ArtifactPath,
                handoff.ArtifactPath,
                prep?.ArtifactPath ?? string.Empty,
                result.ArtifactPath,
                result.FollowupIntakePath,
                result.FollowupPlanPath,
                result.RepairPrepBundlePath,
                result.RepairBundlePath,
                result.FollowupExecutionOutcomePath
            })
            .Concat(result.LinkedArtifactPaths)
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var summary = changedFiles.Count > 0
            ? $"Patch review found {changedFiles.Count} changed file candidate(s) on route {handoff.SelectedRoute}. {validationSummary}"
            : $"Patch review found no candidate file changes on route {handoff.SelectedRoute}. {validationSummary}";
        return new BuilderPatchReview(
            sessionId,
            intakeId,
            handoffId,
            handoff.SelectedRoute,
            intake.ImpliedStackId,
            intake.ImpliedStackLabel,
            validationSummary,
            reviewReadinessState,
            changedFiles,
            linkedArtifactPaths,
            summary,
            BuilderPatchReviewPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderPatchDiffReview BuildBuilderPatchDiffReview(
        string repoRoot,
        BuilderConversationExecutionSession session,
        BuilderPatchReview patchReview,
        PreparedBuilderExecutionResult? result,
        BuilderFileReviewDecision? priorFileReviewDecision = null)
    {
        var observedUtc = DateTimeOffset.UtcNow;
        var decisionMap = (priorFileReviewDecision?.Entries ?? Array.Empty<BuilderFileReviewDecisionEntry>())
            .ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        var fileEntries = BuildBuilderPatchDiffReviewFileEntries(result, patchReview, decisionMap, observedUtc);
        var overallFileReviewState = DetermineBuilderPatchOverallFileReviewState(fileEntries);
        var linkedArtifactPaths = session.LinkedArtifactPaths
            .Concat(patchReview.LinkedArtifactPaths)
            .Concat(new[] { session.ArtifactPath, patchReview.ArtifactPath, result?.ArtifactPath ?? string.Empty })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return new BuilderPatchDiffReview(
            session.SessionId,
            patchReview.SessionId,
            patchReview.ArtifactPath,
            overallFileReviewState,
            patchReview.ReviewReadinessState,
            fileEntries,
            linkedArtifactPaths,
            BuildBuilderPatchDiffReviewSummary(fileEntries, patchReview.ReviewReadinessState),
            BuilderPatchDiffReviewPathForRepo(repoRoot),
            observedUtc);
    }

    private static IReadOnlyList<BuilderPatchDiffReviewFileEntry> BuildBuilderPatchDiffReviewFileEntries(
        PreparedBuilderExecutionResult? result,
        BuilderPatchReview patchReview,
        IReadOnlyDictionary<string, BuilderFileReviewDecisionEntry> decisionMap,
        DateTimeOffset observedUtc)
    {
        var baselineMap = string.IsNullOrWhiteSpace(result?.StarterStateManifestPath)
            ? new Dictionary<string, BuilderReviewableFileSnapshotEntry>(StringComparer.Ordinal)
            : LoadBuilderReviewableTargetSnapshot(result.StarterStateManifestPath)
                .ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        return patchReview.ChangedFiles
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file =>
            {
                decisionMap.TryGetValue(file.Path, out var decision);
                baselineMap.TryGetValue(file.Path, out var baselineEntry);
                var currentPath = string.IsNullOrWhiteSpace(result?.SourceWorkingFolderPath)
                    ? string.Empty
                    : Path.Combine(result.SourceWorkingFolderPath, file.Path);
                var baselineText = baselineEntry is null || string.IsNullOrWhiteSpace(baselineEntry.SnapshotPath) || !File.Exists(baselineEntry.SnapshotPath)
                    ? string.Empty
                    : SafeReadBuilderTextFile(baselineEntry.SnapshotPath);
                var currentText = string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath)
                    ? string.Empty
                    : SafeReadBuilderTextFile(currentPath);
                var patchPreviewText = BuildBuilderPatchPreviewText(file.ChangeKind, baselineText, currentText, file.Path);
                var diffSummary = !string.IsNullOrWhiteSpace(patchPreviewText)
                    ? $"{file.ChangeSummary} Diff preview recorded."
                    : $"{file.ChangeSummary} No bounded diff preview was available.";
                return new BuilderPatchDiffReviewFileEntry(
                    file.Path,
                    file.FileCategory,
                    file.ChangeKind,
                    diffSummary,
                    patchPreviewText,
                    decision?.ApprovalState ?? "pending_review",
                    decision?.RejectionReason ?? string.Empty,
                    observedUtc);
            })
            .ToArray();
    }

    private static string SafeReadBuilderTextFile(string filePath)
    {
        try
        {
            return File.ReadAllText(filePath);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildBuilderPatchPreviewText(string changeKind, string baselineText, string currentText, string relativePath)
    {
        var builder = new StringBuilder();
        builder.Append("@@ ").Append(relativePath).AppendLine();
        var baselineLines = SplitBuilderPatchLines(baselineText);
        var currentLines = SplitBuilderPatchLines(currentText);
        if (string.Equals(changeKind, "created", StringComparison.Ordinal))
        {
            foreach (var line in currentLines.Take(8))
            {
                builder.Append("+").AppendLine(line);
            }

            return builder.ToString().TrimEnd();
        }

        if (string.Equals(changeKind, "removed", StringComparison.Ordinal))
        {
            foreach (var line in baselineLines.Take(8))
            {
                builder.Append("-").AppendLine(line);
            }

            return builder.ToString().TrimEnd();
        }

        var maxLines = Math.Max(baselineLines.Length, currentLines.Length);
        var emitted = 0;
        for (var index = 0; index < maxLines && emitted < 12; index++)
        {
            var before = index < baselineLines.Length ? baselineLines[index] : null;
            var after = index < currentLines.Length ? currentLines[index] : null;
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                continue;
            }

            if (before is not null)
            {
                builder.Append("-").AppendLine(before);
                emitted++;
            }

            if (after is not null && emitted < 12)
            {
                builder.Append("+").AppendLine(after);
                emitted++;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string[] SplitBuilderPatchLines(string text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

    private static string DetermineBuilderPatchOverallFileReviewState(IReadOnlyList<BuilderPatchDiffReviewFileEntry> fileEntries)
    {
        if (fileEntries.Count == 0)
        {
            return "all_files_pending";
        }

        if (fileEntries.Any(entry => string.Equals(entry.ApprovalState, "rejected", StringComparison.Ordinal)))
        {
            return "rejected_file_present";
        }

        if (fileEntries.Any(entry => string.Equals(entry.ApprovalState, "needs_revision", StringComparison.Ordinal)))
        {
            return "needs_revision_before_apply";
        }

        if (fileEntries.All(entry => string.Equals(entry.ApprovalState, "approved", StringComparison.Ordinal)))
        {
            return "ready_to_apply";
        }

        if (fileEntries.Any(entry => string.Equals(entry.ApprovalState, "approved", StringComparison.Ordinal)))
        {
            return "partially_approved";
        }

        return "all_files_pending";
    }

    private static string BuildBuilderPatchDiffReviewSummary(
        IReadOnlyList<BuilderPatchDiffReviewFileEntry> fileEntries,
        string reviewReadinessState)
        => fileEntries.Count == 0
            ? $"Patch diff review has no file entries. Readiness={reviewReadinessState}."
            : $"Patch diff review tracks {fileEntries.Count} file(s). Overall state={DetermineBuilderPatchOverallFileReviewState(fileEntries)}. Readiness={reviewReadinessState}.";

    private static BuilderFileReviewDecision BuildBuilderFileReviewDecision(
        string repoRoot,
        BuilderPatchDiffReview patchDiffReview,
        IReadOnlyList<BuilderFileReviewDecisionEntry> entries,
        DateTimeOffset observedUtc)
    {
        var summary = entries.Count == 0
            ? "No builder file review decisions recorded."
            : $"File review decisions recorded for {entries.Count} file(s). Overall state={patchDiffReview.OverallFileReviewState}.";
        return new BuilderFileReviewDecision(
            patchDiffReview.SessionId,
            patchDiffReview.SourcePatchReviewId,
            patchDiffReview.OverallFileReviewState,
            entries,
            patchDiffReview.LinkedArtifactPaths,
            summary,
            BuilderFileReviewDecisionPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderPatchApplyDecision BuildBuilderPatchApplyDecision(
        string repoRoot,
        BuilderConversationExecutionSession session,
        BuilderPatchDiffReview patchDiffReview,
        BuilderFileReviewDecision fileReviewDecision,
        BuilderPatchReviewOutcome? patchReviewOutcome)
    {
        var blockReasons = new List<string>();
        var finalizationState = "not_ready_to_apply";
        var applyEligibilityState = "not_ready";
        if (!string.Equals(session.SessionState, "awaiting_patch_review", StringComparison.Ordinal) &&
            !string.Equals(session.SessionState, "accepted_for_completion", StringComparison.Ordinal))
        {
            blockReasons.Add($"Execution session state {session.SessionState} is not eligible for patch finalization.");
        }

        switch (patchDiffReview.OverallFileReviewState)
        {
            case "ready_to_apply":
                if (blockReasons.Count == 0)
                {
                    applyEligibilityState = "ready";
                    finalizationState = string.Equals(patchReviewOutcome?.ReviewDecisionState, "accepted", StringComparison.Ordinal)
                        ? "applied_with_operator_approval"
                        : "ready_to_apply";
                }

                break;
            case "rejected_file_present":
                applyEligibilityState = "blocked";
                finalizationState = "blocked_by_file_rejection";
                blockReasons.AddRange(patchDiffReview.FileEntries
                    .Where(entry => string.Equals(entry.ApprovalState, "rejected", StringComparison.Ordinal))
                    .Select(entry => $"{entry.RelativePath}: {FirstNonEmpty(entry.RejectionReason, "file was rejected.")}"));
                break;
            case "needs_revision_before_apply":
                applyEligibilityState = "blocked";
                finalizationState = "blocked_by_revision_request";
                blockReasons.AddRange(patchDiffReview.FileEntries
                    .Where(entry => string.Equals(entry.ApprovalState, "needs_revision", StringComparison.Ordinal))
                    .Select(entry => $"{entry.RelativePath}: {FirstNonEmpty(entry.RejectionReason, "file needs revision before apply.")}"));
                break;
            default:
                applyEligibilityState = "not_ready";
                finalizationState = "not_ready_to_apply";
                blockReasons.Add("Not all changed files are approved.");
                break;
        }

        var linkedArtifactPaths = patchDiffReview.LinkedArtifactPaths
            .Concat(fileReviewDecision.LinkedArtifactPaths)
            .Concat(new[]
            {
                session.ArtifactPath,
                patchDiffReview.ArtifactPath,
                fileReviewDecision.ArtifactPath,
                patchReviewOutcome?.ArtifactPath ?? string.Empty
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var summary = finalizationState switch
        {
            "applied_with_operator_approval" => "Approved patch was applied with explicit operator approval.",
            "ready_to_apply" => "Patch is ready to apply after file-level approval.",
            "blocked_by_file_rejection" => $"Patch apply is blocked by rejected files. {string.Join(" ", blockReasons)}".Trim(),
            "blocked_by_revision_request" => $"Patch apply is blocked by file revision requests. {string.Join(" ", blockReasons)}".Trim(),
            _ => $"Patch apply is not ready. {string.Join(" ", blockReasons)}".Trim()
        };
        return new BuilderPatchApplyDecision(
            session.SessionId,
            patchDiffReview.OverallFileReviewState,
            applyEligibilityState,
            blockReasons,
            finalizationState,
            linkedArtifactPaths,
            summary,
            BuilderPatchApplyDecisionPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static void WriteBuilderPatchReviewGovernanceArtifacts(
        string repoRoot,
        BuilderPatchDiffReview patchDiffReview,
        BuilderFileReviewDecision fileReviewDecision,
        BuilderPatchApplyDecision patchApplyDecision)
    {
        File.WriteAllText(
            BuilderPatchDiffReviewPathForRepo(repoRoot),
            JsonSerializer.Serialize(patchDiffReview, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderFileReviewDecisionPathForRepo(repoRoot),
            JsonSerializer.Serialize(fileReviewDecision, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderPatchApplyDecisionPathForRepo(repoRoot),
            JsonSerializer.Serialize(patchApplyDecision, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static BuilderPatchSnapshot EnsureBuilderPatchSnapshot(
        string repoRoot,
        BuilderConversationExecutionSession session,
        BuilderPatchDiffReview patchDiffReview,
        BuilderFileReviewDecision fileReviewDecision,
        BuilderPatchApplyDecision patchApplyDecision,
        BuilderPatchReviewOutcome patchReviewOutcome,
        PreparedBuilderExecutionResult? result)
    {
        var existingSnapshot = LoadBuilderPatchSnapshot(repoRoot);
        if (existingSnapshot.ApprovedFiles.Count > 0 &&
            string.Equals(existingSnapshot.ExecutionSessionId, session.SessionId, StringComparison.Ordinal) &&
            string.Equals(existingSnapshot.OperatorApprovalState, "applied_with_operator_approval", StringComparison.Ordinal))
        {
            return existingSnapshot;
        }

        if (!string.Equals(patchApplyDecision.FinalizationState, "applied_with_operator_approval", StringComparison.Ordinal) ||
            patchDiffReview.FileEntries.Count == 0)
        {
            return existingSnapshot;
        }

        var snapshot = BuildBuilderPatchSnapshot(
            repoRoot,
            session,
            patchDiffReview,
            fileReviewDecision,
            patchApplyDecision,
            patchReviewOutcome,
            result);
        WriteBuilderPatchSnapshotArtifacts(repoRoot, snapshot);
        return snapshot;
    }

    private static BuilderPatchSnapshot BuildBuilderPatchSnapshot(
        string repoRoot,
        BuilderConversationExecutionSession session,
        BuilderPatchDiffReview patchDiffReview,
        BuilderFileReviewDecision fileReviewDecision,
        BuilderPatchApplyDecision patchApplyDecision,
        BuilderPatchReviewOutcome patchReviewOutcome,
        PreparedBuilderExecutionResult? result)
    {
        var baselineMap = string.IsNullOrWhiteSpace(result?.StarterStateManifestPath)
            ? new Dictionary<string, BuilderReviewableFileSnapshotEntry>(StringComparer.Ordinal)
            : LoadBuilderReviewableTargetSnapshot(result.StarterStateManifestPath)
                .ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        var decisionMap = fileReviewDecision.Entries.ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        var approvedFiles = patchDiffReview.FileEntries
            .Where(entry => string.Equals(entry.ApprovalState, "approved", StringComparison.Ordinal))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry =>
            {
                decisionMap.TryGetValue(entry.RelativePath, out var decision);
                return new BuilderPatchSnapshotFileEntry(
                    entry.RelativePath,
                    NormalizeBuilderPatchChangeType(entry.ChangeKind),
                    ResolveBuilderPatchSnapshotFileChecksum(repoRoot, result, entry, baselineMap),
                    entry.ApprovalState,
                    decision?.ObservedUtc ?? patchApplyDecision.ObservedUtc);
            })
            .ToArray();
        var approvedUtc = approvedFiles.Length == 0
            ? patchApplyDecision.ObservedUtc
            : approvedFiles.Max(file => file.ApprovedUtc);
        var snapshotId = BuildBuilderPatchSnapshotId(session);
        var linkedArtifactPaths = BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot)
            .Concat(new[]
            {
                session.ArtifactPath,
                patchDiffReview.ArtifactPath,
                fileReviewDecision.ArtifactPath,
                patchApplyDecision.ArtifactPath,
                patchReviewOutcome.ArtifactPath,
                result?.ArtifactPath ?? string.Empty
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return new BuilderPatchSnapshot(
            snapshotId,
            session.SessionId,
            BuildBuilderPatchDiffReviewId(patchDiffReview),
            session.SelectedRoute,
            session.StackId,
            patchApplyDecision.FinalizationState,
            approvedFiles,
            linkedArtifactPaths,
            $"Approved patch snapshot {snapshotId} recorded {approvedFiles.Length} file(s) for route {session.SelectedRoute}.",
            BuilderPatchSnapshotPathForRepo(repoRoot),
            approvedUtc,
            DateTimeOffset.UtcNow);
    }

    private static string ResolveBuilderPatchSnapshotFileChecksum(
        string repoRoot,
        PreparedBuilderExecutionResult? result,
        BuilderPatchDiffReviewFileEntry entry,
        IReadOnlyDictionary<string, BuilderReviewableFileSnapshotEntry> baselineMap)
    {
        var normalizedChangeType = NormalizeBuilderPatchChangeType(entry.ChangeKind);
        if (string.Equals(normalizedChangeType, "deleted", StringComparison.Ordinal))
        {
            if (baselineMap.TryGetValue(entry.RelativePath, out var baseline) &&
                !string.IsNullOrWhiteSpace(baseline.ContentHash))
            {
                return baseline.ContentHash;
            }

            return string.Empty;
        }

        var currentPath = ResolveBuilderCurrentFilePath(repoRoot, result, entry.RelativePath);
        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
        {
            return ComputeBuilderFileHash(currentPath);
        }

        if (baselineMap.TryGetValue(entry.RelativePath, out var fallback) &&
            !string.IsNullOrWhiteSpace(fallback.SnapshotPath) &&
            File.Exists(fallback.SnapshotPath))
        {
            return ComputeBuilderFileHash(fallback.SnapshotPath);
        }

        return string.Empty;
    }

    private static string ResolveBuilderCurrentFilePath(
        string repoRoot,
        PreparedBuilderExecutionResult? result,
        string relativePath)
    {
        if (!string.IsNullOrWhiteSpace(result?.SourceWorkingFolderPath))
        {
            var workingPath = Path.Combine(result.SourceWorkingFolderPath, relativePath);
            if (File.Exists(workingPath))
            {
                return workingPath;
            }
        }

        var repoPath = Path.Combine(repoRoot, relativePath);
        return File.Exists(repoPath) ? repoPath : string.Empty;
    }

    private static void WriteBuilderPatchSnapshotArtifacts(
        string repoRoot,
        BuilderPatchSnapshot snapshot,
        BuilderPatchExport? export = null)
    {
        if (snapshot.ApprovedFiles.Count == 0)
        {
            return;
        }

        File.WriteAllText(
            BuilderPatchSnapshotPathForRepo(repoRoot),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        var history = BuildBuilderPatchSnapshotHistory(repoRoot, LoadBuilderPatchSnapshotHistory(repoRoot), snapshot, export);
        File.WriteAllText(
            BuilderPatchSnapshotHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static BuilderPatchSnapshotHistory BuildBuilderPatchSnapshotHistory(
        string repoRoot,
        BuilderPatchSnapshotHistory priorHistory,
        BuilderPatchSnapshot snapshot,
        BuilderPatchExport? export)
    {
        const int retentionCount = 12;
        var priorEntry = priorHistory.Entries.FirstOrDefault(entry => string.Equals(entry.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal));
        var observedUtc = export?.ExportedUtc ?? snapshot.ObservedUtc;
        var currentEntry = new BuilderPatchSnapshotHistoryEntry(
            snapshot.SnapshotId,
            snapshot.ExecutionSessionId,
            snapshot.OperatorApprovalState,
            FirstNonEmpty(export?.BundleFilePath, priorEntry?.ExportBundlePath, string.Empty),
            snapshot.ArtifactPath,
            snapshot.Summary,
            observedUtc);
        var entries = new[] { currentEntry }
            .Concat(priorHistory.Entries.Where(entry => !string.Equals(entry.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal)))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SnapshotId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();
        var summary = string.IsNullOrWhiteSpace(currentEntry.ExportBundlePath)
            ? $"Latest approved patch snapshot is {snapshot.SnapshotId}."
            : $"Latest approved patch snapshot is {snapshot.SnapshotId} with export bundle {Path.GetFileName(currentEntry.ExportBundlePath)}.";
        return new BuilderPatchSnapshotHistory(
            retentionCount,
            entries,
            summary,
            BuilderPatchSnapshotHistoryPathForRepo(repoRoot),
            observedUtc);
    }

    private static BuilderGitHandoffReadiness BuildBuilderGitHandoffReadiness(
        string repoRoot,
        BuilderGitReadinessObservation observation)
    {
        var summary = observation.ReadinessClassification switch
        {
            "ready_for_optional_git_handoff" => $"Git handoff is ready on branch {FirstNonEmpty(observation.BranchName, "unknown")} with a clean working tree.",
            "blocked_git_missing_repo" => "Git handoff is unavailable because no Git repository was detected.",
            "blocked_git_dirty_tree" => $"Git handoff is blocked on branch {FirstNonEmpty(observation.BranchName, "unknown")} because the working tree is dirty.",
            _ => FirstNonEmpty(
                observation.BlockReasons.FirstOrDefault(),
                "Git handoff readiness could not be verified safely.")
        };
        return new BuilderGitHandoffReadiness(
            observation.RepoDetected,
            observation.BranchName,
            observation.WorkingTreeStateKnown,
            observation.WorkingTreeClean,
            observation.AheadBehindState,
            observation.ReadinessClassification,
            observation.BlockReasons
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray(),
            summary,
            BuilderGitHandoffReadinessPathForRepo(repoRoot),
            observation.ObservedUtc);
    }

    private static BuilderManualApplyGuidance BuildBuilderManualApplyGuidance(
        string repoRoot,
        BuilderPatchSnapshot snapshot,
        BuilderPatchExport export,
        BuilderGitHandoffReadiness gitReadiness)
    {
        var approvedFiles = snapshot.ApprovedFiles
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var warnings = gitReadiness.BlockReasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToArray();
        return new BuilderManualApplyGuidance(
            snapshot.SnapshotId,
            export.BundleFilePath,
            approvedFiles,
            BuildBuilderManualApplySteps(repoRoot, export, snapshot),
            warnings,
            $"Manual apply guidance prepared for {approvedFiles.Length} approved file(s) using {Path.GetFileName(export.BundleFilePath)}.",
            BuilderManualApplyGuidancePathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string> BuildBuilderManualApplySteps(
        string repoRoot,
        BuilderPatchExport export,
        BuilderPatchSnapshot snapshot)
        => new[]
        {
            $"Inspect the approved patch bundle at {export.BundleFilePath}.",
            $"Review the approved snapshot artifact at {snapshot.ArtifactPath}.",
            "Apply the unified diff manually with your preferred patch tool or copy the approved changes file-by-file.",
            $"If you later record the change in source control, use the proposal in {BuilderCommitProposalPathForRepo(repoRoot)} as guidance only."
        };

    private static BuilderGitCommitHandoff BuildBuilderGitCommitHandoff(
        string repoRoot,
        BuilderPatchSnapshot snapshot,
        BuilderCommitProposal proposal,
        BuilderGitHandoffReadiness gitReadiness)
    {
        var approvedFiles = snapshot.ApprovedFiles
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return new BuilderGitCommitHandoff(
            snapshot.SnapshotId,
            proposal.ProposedCommitMessage,
            approvedFiles,
            gitReadiness.BranchName,
            gitReadiness.ReadinessClassification,
            gitReadiness.BlockReasons,
            BuildBuilderGitCommitNextStepGuidance(gitReadiness),
            gitReadiness.ReadinessClassification switch
            {
                "ready_for_optional_git_handoff" => $"Git commit handoff is prepared for branch {FirstNonEmpty(gitReadiness.BranchName, "unknown")}.",
                _ => $"Git commit handoff is blocked: {FirstNonEmpty(gitReadiness.BlockReasons.FirstOrDefault(), "Git readiness is unavailable.")}"
            },
            BuilderGitCommitHandoffPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string> BuildBuilderGitCommitNextStepGuidance(BuilderGitHandoffReadiness gitReadiness)
        => string.Equals(gitReadiness.ReadinessClassification, "ready_for_optional_git_handoff", StringComparison.Ordinal)
            ? new[]
            {
                "Review the working tree and confirm only the approved files are part of the intended handoff.",
                "Stage the approved files manually if you want a Git commit.",
                "Use the prepared commit proposal text without modification unless you intentionally need a different commit summary."
            }
            : new[]
            {
                "Use the patch bundle and manual apply guidance instead of a Git handoff.",
                FirstNonEmpty(gitReadiness.BlockReasons.FirstOrDefault(), "Git handoff is blocked until readiness is verified.")
            };

    private static string DetermineBuilderOutputHandoffReadinessState(
        BuilderPatchExport export,
        BuilderGitHandoffReadiness gitReadiness)
    {
        if (string.IsNullOrWhiteSpace(export.BundleFilePath) || !File.Exists(export.BundleFilePath))
        {
            return "ready_for_export_only";
        }

        return gitReadiness.ReadinessClassification switch
        {
            "ready_for_optional_git_handoff" => "ready_for_optional_git_handoff",
            "blocked_git_missing_repo" => "ready_for_manual_apply",
            "blocked_git_dirty_tree" => "ready_for_manual_apply",
            "blocked_git_unknown_state" => "ready_for_manual_apply",
            _ => "ready_for_manual_apply"
        };
    }

    private static string BuildBuilderOutputHandoffSummary(
        BuilderPatchSnapshot snapshot,
        BuilderPatchExport export,
        BuilderGitHandoffReadiness gitReadiness)
    {
        return string.Equals(gitReadiness.ReadinessClassification, "ready_for_optional_git_handoff", StringComparison.Ordinal)
            ? $"Approved output handoff for snapshot {snapshot.SnapshotId} covers {snapshot.ApprovedFiles.Count} file(s). Bundle={Path.GetFileName(export.BundleFilePath)}. Git handoff is available on branch {FirstNonEmpty(gitReadiness.BranchName, "unknown")}."
            : $"Approved output handoff for snapshot {snapshot.SnapshotId} covers {snapshot.ApprovedFiles.Count} file(s). Bundle={Path.GetFileName(export.BundleFilePath)}. Ready for manual apply. Git handoff is blocked: {FirstNonEmpty(gitReadiness.BlockReasons.FirstOrDefault(), gitReadiness.Summary)}";
    }

    private static string BuildBuilderOutputHandoffSummaryMarkdown(
        BuilderOutputHandoff handoff,
        BuilderManualApplyGuidance manualApplyGuidance)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Output Handoff");
        builder.AppendLine();
        builder.AppendLine($"Snapshot: {FirstNonEmpty(handoff.SnapshotId, "not recorded")}");
        builder.AppendLine($"Approved files: {handoff.ApprovedFiles.Count}");
        builder.AppendLine($"Patch bundle: {FirstNonEmpty(handoff.PatchBundlePath, "not recorded")}");
        builder.AppendLine($"Git readiness: {FirstNonEmpty(handoff.OptionalGitReadinessState, "not recorded")}");
        if (handoff.BlockReasons.Count > 0)
        {
            builder.AppendLine($"Git block reason: {handoff.BlockReasons[0]}");
        }

        builder.AppendLine("Next actions:");
        foreach (var step in manualApplyGuidance.ApplySteps)
        {
            builder.AppendLine($"- {step}");
        }

        return builder.ToString().TrimEnd() + System.Environment.NewLine;
    }

    private static BuilderOutputHandoffHistory BuildBuilderOutputHandoffHistory(
        string repoRoot,
        BuilderOutputHandoffHistory priorHistory,
        BuilderOutputHandoff handoff)
    {
        const int retentionCount = 12;
        var currentEntry = new BuilderOutputHandoffHistoryEntry(
            handoff.SnapshotId,
            handoff.ExecutionSessionId,
            handoff.HandoffReadinessState,
            handoff.ManualApplyGuidancePath,
            handoff.OptionalGitReadinessState,
            handoff.GitCommitHandoffPath,
            handoff.ArtifactPath,
            handoff.Summary,
            handoff.ObservedUtc);
        var entries = new[] { currentEntry }
            .Concat(priorHistory.Entries.Where(entry => !string.Equals(entry.SnapshotId, handoff.SnapshotId, StringComparison.Ordinal)))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SnapshotId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();
        return new BuilderOutputHandoffHistory(
            retentionCount,
            entries,
            $"Latest builder output handoff is {FirstNonEmpty(handoff.SnapshotId, "not recorded")} with state {handoff.HandoffReadinessState}.",
            BuilderOutputHandoffHistoryPathForRepo(repoRoot),
            handoff.ObservedUtc);
    }

    private static string BuildBuilderCommitProposalMessage(
        BuilderPatchSnapshot snapshot,
        BuilderPatchDiffReview patchDiffReview)
    {
        var summary = string.Join(
            "; ",
            patchDiffReview.FileEntries
                .Where(entry => snapshot.ApprovedFiles.Any(file => string.Equals(file.RelativePath, entry.RelativePath, StringComparison.Ordinal)))
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .Select(entry => $"{entry.RelativePath} ({NormalizeBuilderPatchChangeType(entry.ChangeKind)})"));
        return string.Join(
            System.Environment.NewLine,
            new[]
            {
                "Shoots Builder Accepted Patch",
                $"Route: {snapshot.RouteId}",
                $"Stack: {snapshot.StackId}",
                $"Session: {snapshot.ExecutionSessionId}",
                $"Files: {snapshot.ApprovedFiles.Count}",
                $"Summary: {FirstNonEmpty(summary, "No approved file summary recorded.")}"
            });
    }

    private static string BuildBuilderPatchBundleText(
        string repoRoot,
        BuilderPatchSnapshot snapshot,
        BuilderPatchDiffReview patchDiffReview,
        PreparedBuilderExecutionResult? result)
    {
        var baselineMap = string.IsNullOrWhiteSpace(result?.StarterStateManifestPath)
            ? new Dictionary<string, BuilderReviewableFileSnapshotEntry>(StringComparer.Ordinal)
            : LoadBuilderReviewableTargetSnapshot(result.StarterStateManifestPath)
                .ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        var patchEntryMap = patchDiffReview.FileEntries.ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        var sections = snapshot.ApprovedFiles
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file =>
            {
                patchEntryMap.TryGetValue(file.RelativePath, out var patchEntry);
                baselineMap.TryGetValue(file.RelativePath, out var baselineEntry);
                var baselineText = baselineEntry is null || string.IsNullOrWhiteSpace(baselineEntry.SnapshotPath) || !File.Exists(baselineEntry.SnapshotPath)
                    ? string.Empty
                    : SafeReadBuilderTextFile(baselineEntry.SnapshotPath);
                var currentPath = ResolveBuilderCurrentFilePath(repoRoot, result, file.RelativePath);
                var currentText = string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath)
                    ? string.Empty
                    : SafeReadBuilderTextFile(currentPath);
                return BuildBuilderPatchBundleSection(
                    file.RelativePath,
                    patchEntry?.ChangeKind ?? file.ChangeType,
                    baselineText,
                    currentText,
                    patchEntry?.PatchPreviewText ?? string.Empty);
            })
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToArray();
        return sections.Length == 0
            ? string.Empty
            : string.Join(System.Environment.NewLine + System.Environment.NewLine, sections) + System.Environment.NewLine;
    }

    private static string BuildBuilderPatchBundleSection(
        string relativePath,
        string changeKind,
        string baselineText,
        string currentText,
        string fallbackPreviewText)
    {
        var normalizedPath = NormalizeBuilderPatchBundlePath(relativePath);
        var normalizedChangeType = NormalizeBuilderPatchChangeType(changeKind);
        if (string.Equals(normalizedChangeType, "created", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(currentText))
        {
            return BuildBuilderUnifiedDiffSection("/dev/null", $"b/{normalizedPath}", Array.Empty<string>(), SplitBuilderPatchLines(currentText));
        }

        if (string.Equals(normalizedChangeType, "deleted", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(baselineText))
        {
            return BuildBuilderUnifiedDiffSection($"a/{normalizedPath}", "/dev/null", SplitBuilderPatchLines(baselineText), Array.Empty<string>());
        }

        if (!string.IsNullOrWhiteSpace(baselineText) || !string.IsNullOrWhiteSpace(currentText))
        {
            return BuildBuilderUnifiedDiffSection($"a/{normalizedPath}", $"b/{normalizedPath}", SplitBuilderPatchLines(baselineText), SplitBuilderPatchLines(currentText));
        }

        if (string.IsNullOrWhiteSpace(fallbackPreviewText))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"--- a/{normalizedPath}");
        builder.AppendLine($"+++ b/{normalizedPath}");
        var preview = fallbackPreviewText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        if (!preview.StartsWith("@@", StringComparison.Ordinal))
        {
            builder.Append("@@ ").Append(normalizedPath).AppendLine();
        }

        builder.Append(preview);
        return builder.ToString().TrimEnd();
    }

    private static string BuildBuilderUnifiedDiffSection(
        string beforePath,
        string afterPath,
        IReadOnlyList<string> beforeLines,
        IReadOnlyList<string> afterLines)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"--- {beforePath}");
        builder.AppendLine($"+++ {afterPath}");
        builder.AppendLine($"@@ -1,{beforeLines.Count} +1,{afterLines.Count} @@");
        foreach (var line in beforeLines)
        {
            builder.Append('-').AppendLine(line);
        }

        foreach (var line in afterLines)
        {
            builder.Append('+').AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static string NormalizeBuilderPatchBundlePath(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/');

    private static string NormalizeBuilderPatchChangeType(string changeKind)
        => string.Equals(changeKind, "removed", StringComparison.Ordinal)
            ? "deleted"
            : FirstNonEmpty(changeKind, "modified");

    private static string BuildBuilderPatchSnapshotId(BuilderConversationExecutionSession session)
        => $"patch-snapshot-{SanitizeBuilderProofToken(FirstNonEmpty(session.SessionId, "unknown"))}";

    private static string BuildBuilderPatchDiffReviewId(BuilderPatchDiffReview patchDiffReview)
        => $"patch-diff-review-{SanitizeBuilderProofToken(FirstNonEmpty(patchDiffReview.SessionId, patchDiffReview.SourcePatchReviewId, "unknown"))}";

    private static IReadOnlyList<BuilderPatchReviewChangedFile> BuildBuilderPatchReviewChangedFiles(PreparedBuilderExecutionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SourceWorkingFolderPath) ||
            !Directory.Exists(result.SourceWorkingFolderPath))
        {
            return Array.Empty<BuilderPatchReviewChangedFile>();
        }

        var baseline = string.IsNullOrWhiteSpace(result.StarterStateManifestPath)
            ? Array.Empty<BuilderReviewableFileSnapshotEntry>()
            : LoadBuilderReviewableTargetSnapshot(result.StarterStateManifestPath);
        var current = CaptureBuilderReviewableTargetSnapshot(result.SourceWorkingFolderPath);
        var baselineMap = baseline.ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        var currentMap = current.ToDictionary(entry => entry.RelativePath, entry => entry, StringComparer.Ordinal);
        var allPaths = baselineMap.Keys
            .Union(currentMap.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var changedFiles = new List<BuilderPatchReviewChangedFile>(allPaths.Length);
        foreach (var path in allPaths)
        {
            baselineMap.TryGetValue(path, out var baselineEntry);
            currentMap.TryGetValue(path, out var currentEntry);
            var changeKind = baselineEntry is null
                ? "created"
                : currentEntry is null
                    ? "removed"
                    : string.Equals(baselineEntry.ContentHash, currentEntry.ContentHash, StringComparison.Ordinal)
                        ? "unchanged"
                        : "modified";
            if (string.Equals(changeKind, "unchanged", StringComparison.Ordinal))
            {
                continue;
            }

            var fileCategory = ClassifyBuilderPatchReviewFileCategory(path);
            changedFiles.Add(new BuilderPatchReviewChangedFile(
                path,
                fileCategory,
                changeKind,
                BuildBuilderPatchReviewChangeSummary(path, fileCategory, changeKind),
                true));
        }

        return changedFiles;
    }

    private static string ClassifyBuilderPatchReviewFileCategory(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (string.Equals(Path.GetExtension(relativePath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return "project_file";
        }

        if (string.Equals(Path.GetExtension(relativePath), ".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return "ui_markup";
        }

        if (fileName.Contains("ViewModel", StringComparison.OrdinalIgnoreCase))
        {
            return "view_model";
        }

        if (relativePath.Contains($"{Path.DirectorySeparatorChar}Services{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return "service";
        }

        if (relativePath.Contains($"{Path.DirectorySeparatorChar}Builder{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return "builder_logic";
        }

        if (relativePath.Contains("Tests", StringComparison.OrdinalIgnoreCase))
        {
            return "test_file";
        }

        return "source_file";
    }

    private static string BuildBuilderPatchReviewChangeSummary(string relativePath, string fileCategory, string changeKind)
        => changeKind switch
        {
            "created" => $"{relativePath} is newly created as bounded {fileCategory.Replace('_', ' ')} evidence.",
            "removed" => $"{relativePath} was removed as part of the bounded {fileCategory.Replace('_', ' ')} route.",
            _ => $"{relativePath} was modified to satisfy the bounded {fileCategory.Replace('_', ' ')} route."
        };

    private static string BuildBuilderConversationValidationSummary(PreparedBuilderExecutionResult? result)
    {
        if (result is null)
        {
            return "No validation summary recorded.";
        }

        var builder = new StringBuilder();
        builder.Append($"Build={FirstNonEmpty(result.BuildResult, "not_recorded")}. ");
        builder.Append($"Test={FirstNonEmpty(result.TestResult, "not_recorded")}. ");
        builder.Append($"Outcome={FirstNonEmpty(result.FinalRouteOutcomeClassification, "not_recorded")}.");
        if (!string.IsNullOrWhiteSpace(result.FollowupState) &&
            !string.Equals(result.FollowupState, "not_needed", StringComparison.Ordinal))
        {
            builder.Append($" Follow-up={result.FollowupState}.");
        }

        return builder.ToString().Trim();
    }

    private static BuilderConversationExecutionSession BuildBuilderConversationExecutionSession(
        string repoRoot,
        string sessionId,
        string intakeId,
        string handoffId,
        BuilderConversationIntake intake,
        BuilderConversationHandoff handoff,
        BuilderExecutionPrep? prep,
        PreparedBuilderExecutionLaunch? launch,
        PreparedBuilderExecutionResult? result,
        BuilderPatchReview? patchReview,
        BuilderPatchReviewOutcome? patchReviewOutcome,
        string sessionState,
        string currentStageId,
        string currentStageLabel,
        string reviewState,
        string validationSummary,
        string reviewNote)
    {
        var changedFiles = patchReview?.ChangedFiles ?? Array.Empty<BuilderPatchReviewChangedFile>();
        var linkedArtifactPaths = BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot)
            .Concat(new[]
            {
                intake.ArtifactPath,
                handoff.ArtifactPath,
                prep?.ArtifactPath ?? string.Empty,
                launch?.ArtifactPath ?? string.Empty,
                result?.ArtifactPath ?? string.Empty,
                patchReview?.ArtifactPath ?? string.Empty,
                patchReviewOutcome?.ArtifactPath ?? string.Empty
            })
            .Concat(result?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var stages = BuildBuilderConversationExecutionStages(
            intake,
            handoff,
            sessionState,
            currentStageId,
            validationSummary,
            changedFiles,
            patchReviewOutcome,
            launch,
            result);
        var summary = BuildBuilderConversationExecutionSessionSummary(
            handoff.SelectedRoute,
            sessionState,
            validationSummary,
            changedFiles.Count,
            patchReviewOutcome,
            reviewNote);
        return new BuilderConversationExecutionSession(
            sessionId,
            FirstNonEmpty(intakeId, BuildBuilderConversationIntakeId(intake)),
            FirstNonEmpty(handoffId, BuildBuilderConversationHandoffId(handoff)),
            intake.RawRequestText,
            intake.NormalizedTaskClass,
            handoff.SelectedRoute,
            intake.ImpliedStackId,
            intake.ImpliedStackLabel,
            FirstNonEmpty(prep?.CapabilitySummary, intake.CapabilitySummary),
            sessionState,
            currentStageId,
            currentStageLabel,
            reviewState,
            validationSummary,
            prep?.ArtifactPath ?? string.Empty,
            launch?.ArtifactPath ?? string.Empty,
            result?.ArtifactPath ?? string.Empty,
            patchReview?.ArtifactPath ?? string.Empty,
            patchReviewOutcome?.ArtifactPath ?? string.Empty,
            changedFiles,
            stages,
            linkedArtifactPaths,
            summary,
            BuilderConversationExecutionSessionPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<BuilderConversationExecutionStage> BuildBuilderConversationExecutionStages(
        BuilderConversationIntake intake,
        BuilderConversationHandoff handoff,
        string sessionState,
        string currentStageId,
        string validationSummary,
        IReadOnlyList<BuilderPatchReviewChangedFile> changedFiles,
        BuilderPatchReviewOutcome? patchReviewOutcome,
        PreparedBuilderExecutionLaunch? launch,
        PreparedBuilderExecutionResult? result)
    {
        var awaitingState = patchReviewOutcome is null
            ? string.Equals(sessionState, "awaiting_patch_review", StringComparison.Ordinal)
                ? "active"
                : string.Equals(sessionState, "executing", StringComparison.Ordinal)
                    ? "pending"
                    : "blocked"
            : "completed";
        var completedState = patchReviewOutcome is not null && string.Equals(patchReviewOutcome.ReviewDecisionState, "accepted", StringComparison.Ordinal)
            ? "completed"
            : "pending";
        return new[]
        {
            new BuilderConversationExecutionStage("repo_retrieval_confirmed", "Repo retrieval confirmed", "completed", intake.RetrievalSummary, Array.Empty<string>()),
            new BuilderConversationExecutionStage("route_prepared", "Route prepared", "completed", handoff.Summary, Array.Empty<string>()),
            new BuilderConversationExecutionStage("builder_launched", "Builder launched", string.Equals(currentStageId, "builder_launched", StringComparison.Ordinal) && string.Equals(sessionState, "executing", StringComparison.Ordinal) ? "active" : launch is null && result is null ? "pending" : "completed", launch?.Summary ?? "Prepared builder launch is pending.", Array.Empty<string>()),
            new BuilderConversationExecutionStage("files_changed", "Files changed", changedFiles.Count > 0 ? "completed" : result is null ? "pending" : "blocked", changedFiles.Count > 0 ? $"{changedFiles.Count} reviewable candidate file(s) were detected." : "No reviewable candidate file changes were detected.", changedFiles.Select(file => file.Path).ToArray()),
            new BuilderConversationExecutionStage("validation_run", "Validation run", result is null ? "pending" : "completed", validationSummary, result?.LinkedArtifactPaths ?? Array.Empty<string>()),
            new BuilderConversationExecutionStage("awaiting_operator_review", "Awaiting operator review", awaitingState, patchReviewOutcome?.Summary ?? "Operator review is required before completion.", Array.Empty<string>()),
            new BuilderConversationExecutionStage("completed", "Completed", completedState, patchReviewOutcome?.Summary ?? "Builder conversation completion is pending operator review.", Array.Empty<string>())
        };
    }

    private static string BuildBuilderConversationExecutionSessionSummary(
        string selectedRoute,
        string sessionState,
        string validationSummary,
        int changedFileCount,
        BuilderPatchReviewOutcome? patchReviewOutcome,
        string reviewNote)
        => sessionState switch
        {
            "executing" => $"Builder conversation execution is running route {selectedRoute}.",
            "awaiting_patch_review" => $"Route {selectedRoute} finished with {changedFileCount} candidate change(s) and is awaiting patch review. {validationSummary}",
            "accepted_for_completion" => $"Route {selectedRoute} was accepted for completion. {FirstNonEmpty(patchReviewOutcome?.Summary, validationSummary)}",
            "rejected_for_revision" => $"Route {selectedRoute} was rejected for revision. {FirstNonEmpty(reviewNote, patchReviewOutcome?.Summary, validationSummary)}",
            "rerouted" => $"Route {selectedRoute} was rerouted during patch review. {FirstNonEmpty(patchReviewOutcome?.Summary, reviewNote, validationSummary)}",
            "failed_into_followup" => $"Route {selectedRoute} moved into follow-up handling. {FirstNonEmpty(patchReviewOutcome?.Summary, validationSummary)}",
            _ => $"No builder conversation execution session recorded for route {selectedRoute}."
        };

    private static string DetermineBuilderConversationReviewSessionState(
        string reviewDecisionState,
        PreparedBuilderExecutionResult? result)
        => reviewDecisionState switch
        {
            "accepted" => "accepted_for_completion",
            "reroute_requested" => "rerouted",
            "rejected" when HasBuilderConversationFollowupArtifacts(result) => "failed_into_followup",
            "rejected" => "rejected_for_revision",
            "revise_requested" => "rejected_for_revision",
            _ => "awaiting_patch_review"
        };

    private static bool HasBuilderConversationFollowupArtifacts(PreparedBuilderExecutionResult? result)
        => result is not null &&
           (!string.IsNullOrWhiteSpace(result.FollowupIntakePath) ||
            !string.IsNullOrWhiteSpace(result.FollowupPlanPath) ||
            !string.IsNullOrWhiteSpace(result.RepairPrepBundlePath) ||
            !string.IsNullOrWhiteSpace(result.RepairBundlePath) ||
            !string.IsNullOrWhiteSpace(result.FollowupExecutionOutcomePath));

    private static IReadOnlyList<string> BuildBuilderConversationReviewLinkedArtifactPaths(
        string repoRoot,
        BuilderConversationExecutionSession session,
        BuilderPatchReview patchReview,
        PreparedBuilderExecutionResult? result,
        BuilderConversationHandoff handoff)
        => BuildBuilderConversationAuthoritativeArtifactPaths(repoRoot)
            .Concat(session.LinkedArtifactPaths)
            .Concat(patchReview.LinkedArtifactPaths)
            .Concat(new[]
            {
                handoff.ArtifactPath,
                BuilderPatchReviewPathForRepo(repoRoot),
                BuilderConversationExecutionSessionPathForRepo(repoRoot),
                result?.ArtifactPath ?? string.Empty,
                result?.FollowupIntakePath ?? string.Empty,
                result?.FollowupPlanPath ?? string.Empty,
                result?.RepairPrepBundlePath ?? string.Empty,
                result?.RepairBundlePath ?? string.Empty,
                result?.FollowupExecutionOutcomePath ?? string.Empty
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string BuildBuilderPatchReviewOutcomeSummary(
        string reviewDecisionState,
        string sessionState,
        string reviewNote,
        string rerouteRoute,
        PreparedBuilderExecutionResult? result)
    {
        var validationSummary = BuildBuilderConversationValidationSummary(result);
        return reviewDecisionState switch
        {
            "accepted" => $"Operator accepted the candidate changes for completion. {validationSummary}",
            "rejected" => $"Operator rejected the candidate changes. {FirstNonEmpty(reviewNote, validationSummary)}",
            "revise_requested" => $"Operator requested revision before completion. {FirstNonEmpty(reviewNote, validationSummary)}",
            "reroute_requested" => $"Operator requested reroute to {FirstNonEmpty(rerouteRoute, "an override route")}. {FirstNonEmpty(reviewNote, validationSummary)}",
            _ => $"Builder patch review is still pending. Session state={sessionState}."
        };
    }

    private static void WriteBuilderConversationExecutionSessionArtifacts(
        string repoRoot,
        BuilderConversationExecutionSession session,
        BuilderPatchReview? patchReview,
        BuilderPatchReviewOutcome? patchReviewOutcome = null)
    {
        File.WriteAllText(
            BuilderConversationExecutionSessionPathForRepo(repoRoot),
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));

        var history = BuildBuilderConversationExecutionHistory(
            LoadBuilderConversationExecutionHistory(repoRoot),
            session);
        File.WriteAllText(
            BuilderConversationExecutionHistoryPathForRepo(repoRoot),
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            BuilderConversationReviewSummaryPathForRepo(repoRoot),
            BuildBuilderConversationReviewSummaryMarkdown(session, patchReview, patchReviewOutcome));
    }

    private static BuilderConversationExecutionHistory BuildBuilderConversationExecutionHistory(
        BuilderConversationExecutionHistory priorHistory,
        BuilderConversationExecutionSession session)
    {
        const int retentionCount = 12;
        var newestEntry = new BuilderConversationExecutionHistoryEntry(
            session.SessionId,
            session.RawRequestText,
            session.SelectedRoute,
            session.SessionState,
            session.ReviewState,
            session.ArtifactPath,
            session.Summary,
            session.ObservedUtc);
        var entries = new[] { newestEntry }
            .Concat(priorHistory.Entries.Where(entry => !string.Equals(entry.SessionId, session.SessionId, StringComparison.Ordinal)))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SessionId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();
        var summary = $"Latest builder conversation session state is {session.SessionState} on route {session.SelectedRoute}.";
        return new BuilderConversationExecutionHistory(
            retentionCount,
            entries,
            summary,
            Path.Combine(Path.GetDirectoryName(session.ArtifactPath) ?? string.Empty, "builder_conversation_execution_history.json"),
            session.ObservedUtc);
    }

    private static string BuildBuilderConversationReviewSummaryMarkdown(
        BuilderConversationExecutionSession session,
        BuilderPatchReview? patchReview,
        BuilderPatchReviewOutcome? patchReviewOutcome)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Conversation Review Summary");
        builder.AppendLine();
        builder.AppendLine($"Session state: {session.SessionState}");
        builder.AppendLine($"Route: {FirstNonEmpty(session.SelectedRoute, "not recorded")}");
        builder.AppendLine($"Review state: {FirstNonEmpty(patchReviewOutcome?.ReviewState, session.ReviewState, "not recorded")}");
        builder.AppendLine($"Validation: {session.ValidationSummary}");
        if (patchReview is not null)
        {
            builder.AppendLine($"Changed files: {patchReview.ChangedFiles.Count}");
        }

        builder.AppendLine();
        builder.AppendLine(session.Summary);
        if (patchReviewOutcome is not null)
        {
            builder.AppendLine();
            builder.AppendLine(patchReviewOutcome.Summary);
        }

        return builder.ToString().TrimEnd();
    }

    private static BuilderRepoToolchainPolicySnapshot BuildBuilderRepoToolchainPolicySnapshot(string repoRoot)
    {
        var csprojPaths = Directory.Exists(repoRoot)
            ? Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();
        var hasDotNetProjects = csprojPaths.Length > 0;
        var hasWpfProjects = csprojPaths.Any(path =>
        {
            var text = File.ReadAllText(path);
            return text.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase);
        });

        return new BuilderRepoToolchainPolicySnapshot(
            hasDotNetProjects,
            hasWpfProjects,
            hasWpfProjects ? "wpf_desktop_dotnet" : hasDotNetProjects ? "csharp_dotnet" : string.Empty,
            hasWpfProjects ? "WPF/Desktop .NET" : hasDotNetProjects ? "C# / .NET" : string.Empty,
            hasWpfProjects
                ? "Repo policy prefers WPF/Desktop .NET while allowing bounded C#/.NET work."
                : hasDotNetProjects
                    ? "Repo policy allows C#/.NET for this repository."
                    : "No repo-level .NET policy was detected.");
    }

    private static BuilderToolchainCapabilityObservation BuildBuilderWpfDesktopObservation(
        BuilderRepoToolchainPolicySnapshot policy,
        BuilderToolchainCapabilityObservation dotnetObservation,
        DateTimeOffset observedUtc)
    {
        var installed = dotnetObservation.Installed && policy.HasWpfProjects;
        var callable = dotnetObservation.Callable && policy.HasWpfProjects && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var probeState = callable
            ? "probe_succeeded"
            : installed
                ? "probe_failed"
                : "not_found";
        var message = callable
            ? string.Empty
            : !policy.HasWpfProjects
                ? "Repo policy does not currently require WPF/Desktop .NET."
                : !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "WPF/Desktop .NET builds require Windows."
                    : FirstNonEmpty(dotnetObservation.ProbeMessage, "dotnet is not callable for WPF/Desktop .NET.");
        return new BuilderToolchainCapabilityObservation(
            "dotnet_wpf_desktop",
            "desktop_capability",
            dotnetObservation.DiscoveredPath,
            dotnetObservation.Version,
            installed,
            callable,
            probeState,
            message,
            observedUtc);
    }

    private static IReadOnlyList<string> GetBuilderToolchainCandidateIds()
        => new[]
        {
            "cl",
            "cmake",
            "dotnet",
            "dotnet_wpf_desktop",
            "g++",
            "gcc",
            "java",
            "msbuild",
            "ninja",
            "node",
            "npm",
            "pnpm",
            "python",
            "yarn",
            "clang"
        };

    private static string GetBuilderToolchainCategory(string toolId)
        => toolId switch
        {
            "cl" or "gcc" or "g++" or "clang" => "compiler",
            "cmake" or "ninja" or "msbuild" => "build_tool",
            "npm" or "pnpm" or "yarn" => "packaging_tool",
            "dotnet" => "sdk",
            "dotnet_wpf_desktop" => "desktop_capability",
            _ => "runtime"
        };

    private static bool IsBuilderToolchainSupportedByRepo(string toolId, BuilderRepoToolchainPolicySnapshot policy)
        => toolId switch
        {
            "dotnet" => policy.HasDotNetProjects,
            "msbuild" => policy.HasDotNetProjects,
            "dotnet_wpf_desktop" => policy.HasWpfProjects,
            _ => false
        };

    private static bool IsBuilderToolchainPreferredByRepo(string toolId, BuilderRepoToolchainPolicySnapshot policy)
        => toolId switch
        {
            "dotnet" => policy.HasDotNetProjects,
            "dotnet_wpf_desktop" => policy.HasWpfProjects,
            _ => false
        };

    private static string DetermineBuilderToolchainUsabilityState(
        BuilderToolchainCapabilityObservation observation,
        bool supportedByRepo,
        bool preferredByRepo)
    {
        if (!observation.Installed)
        {
            return "not_installed";
        }

        if (!observation.Callable)
        {
            return "installed_but_not_callable";
        }

        if (!supportedByRepo)
        {
            return "callable_but_repo_blocked";
        }

        return preferredByRepo
            ? "preferred_and_ready"
            : "approved_but_not_preferred";
    }

    private static string BuildBuilderToolchainBlockedReason(
        BuilderToolchainCapabilityObservation observation,
        bool supportedByRepo)
    {
        if (!observation.Installed)
        {
            return FirstNonEmpty(observation.ProbeMessage, $"{observation.ToolId} is not installed.");
        }

        if (!observation.Callable)
        {
            return FirstNonEmpty(observation.ProbeMessage, $"{observation.ToolId} is installed but not callable.");
        }

        return supportedByRepo
            ? string.Empty
            : $"{observation.ToolId} is callable on this machine but not approved for this repo.";
    }

    private static string BuildBuilderToolchainRegistryEntrySummary(
        BuilderToolchainCapabilityObservation observation,
        string usabilityState,
        string blockedReason)
        => string.IsNullOrWhiteSpace(blockedReason)
            ? $"{observation.ToolId} is {usabilityState.Replace('_', ' ')}."
            : $"{observation.ToolId} is {usabilityState.Replace('_', ' ')}. {blockedReason}";

    private static string BuildBuilderToolchainCapabilitySummary(
        BuilderRepoToolchainPolicySnapshot policy,
        IReadOnlyList<BuilderToolchainCapabilityRegistryEntry> entries,
        string driftState)
    {
        var ready = entries
            .Where(entry => string.Equals(entry.UsabilityState, "preferred_and_ready", StringComparison.Ordinal) ||
                            string.Equals(entry.UsabilityState, "approved_but_not_preferred", StringComparison.Ordinal))
            .Select(entry => entry.ToolId)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();
        var blocked = entries
            .Where(entry => !string.Equals(entry.UsabilityState, "preferred_and_ready", StringComparison.Ordinal) &&
                            !string.Equals(entry.UsabilityState, "approved_but_not_preferred", StringComparison.Ordinal))
            .Select(entry => $"{entry.ToolId} ({FirstNonEmpty(entry.BlockedReason, entry.UsabilityState.Replace('_', ' '))})")
            .Take(6)
            .ToArray();
        var readySummary = ready.Length == 0 ? "none" : string.Join(", ", ready);
        var blockedSummary = blocked.Length == 0 ? "none" : string.Join("; ", blocked);
        return $"Preferred stack: {FirstNonEmpty(policy.PreferredStackLabel, "not recorded")}. Ready toolchains: {readySummary}. Blocked toolchains: {blockedSummary}. Refresh completed with {driftState}.";
    }

    private static IReadOnlyList<BuilderLanguageStackDefinition> GetBuilderLanguageStackDefinitions()
        => new[]
        {
            new BuilderLanguageStackDefinition("wpf_desktop_dotnet", "WPF/Desktop .NET", new[] { "dotnet", "dotnet_wpf_desktop" }, "dotnet SDK plus Windows WPF desktop capability"),
            new BuilderLanguageStackDefinition("csharp_dotnet", "C# / .NET", new[] { "dotnet" }, "callable dotnet SDK"),
            new BuilderLanguageStackDefinition("cpp_native", "C/C++", new[] { "cl", "gcc", "g++", "clang" }, "at least one callable native compiler"),
            new BuilderLanguageStackDefinition("javascript_typescript", "JavaScript/TypeScript", new[] { "node", "npm" }, "callable node runtime with an approved package tool"),
            new BuilderLanguageStackDefinition("python", "Python", new[] { "python" }, "callable python runtime"),
            new BuilderLanguageStackDefinition("java", "Java", new[] { "java" }, "callable java runtime")
        };

    private static bool IsBuilderLanguageSupportedByRepo(string stackId, BuilderRepoToolchainPolicySnapshot policy)
        => stackId switch
        {
            "wpf_desktop_dotnet" => policy.HasWpfProjects,
            "csharp_dotnet" => policy.HasDotNetProjects,
            _ => false
        };

    private static bool IsBuilderLanguagePreferredByRepo(string stackId, BuilderRepoToolchainPolicySnapshot policy)
        => stackId switch
        {
            "wpf_desktop_dotnet" => policy.HasWpfProjects,
            "csharp_dotnet" => policy.HasDotNetProjects && !policy.HasWpfProjects,
            _ => false
        };

    private static bool IsBuilderLanguageReady(string stackId, BuilderToolchainCapabilityRegistry registry)
        => stackId switch
        {
            "cpp_native" => new[] { "cl", "gcc", "g++", "clang" }.Any(toolId => IsBuilderToolchainCallable(registry, toolId)),
            "javascript_typescript" => IsBuilderToolchainCallable(registry, "node") &&
                                      new[] { "npm", "pnpm", "yarn" }.Any(toolId => IsBuilderToolchainCallable(registry, toolId)),
            _ => GetBuilderLanguageStackDefinitions()
                .First(definition => string.Equals(definition.StackId, stackId, StringComparison.Ordinal))
                .RequiredToolIds.All(toolId => IsBuilderToolchainCallable(registry, toolId))
        };

    private static string BuildBuilderLanguageEligibilityEntrySummary(
        BuilderLanguageStackDefinition definition,
        string eligibilityState,
        string blockedReason)
        => string.IsNullOrWhiteSpace(blockedReason)
            ? $"{definition.StackLabel} is {eligibilityState.Replace('_', ' ')}."
            : $"{definition.StackLabel} is {eligibilityState.Replace('_', ' ')}. {blockedReason}";

    private static string BuildBuilderLanguageEligibilitySummary(
        IReadOnlyList<BuilderLanguageEligibilityEntry> entries,
        BuilderRepoToolchainPolicySnapshot policy)
    {
        var defaultEntry = entries.FirstOrDefault(entry => string.Equals(entry.EligibilityState, "ready_and_preferred", StringComparison.Ordinal));
        var readyButNotPreferred = entries
            .Where(entry => string.Equals(entry.EligibilityState, "ready_but_not_preferred", StringComparison.Ordinal))
            .Select(entry => entry.StackLabel)
            .ToArray();
        var blocked = entries
            .Where(entry =>
                string.Equals(entry.EligibilityState, "installed_but_disallowed", StringComparison.Ordinal) ||
                string.Equals(entry.EligibilityState, "unsupported_for_repo", StringComparison.Ordinal) ||
                string.Equals(entry.EligibilityState, "unavailable", StringComparison.Ordinal))
            .Select(entry => $"{entry.StackLabel} ({entry.EligibilityState.Replace('_', ' ')})")
            .ToArray();
        return $"{FirstNonEmpty(defaultEntry?.StackLabel, policy.PreferredStackLabel)} is the default builder stack. Available but not preferred: {FirstNonEmpty(string.Join(", ", readyButNotPreferred), "none")}. Blocked or unsupported: {FirstNonEmpty(string.Join(", ", blocked), "none")}.";
    }

    private static bool IsBuilderToolchainInstalled(BuilderToolchainCapabilityRegistry registry, string toolId)
        => registry.Entries.Any(entry => string.Equals(entry.ToolId, toolId, StringComparison.Ordinal) && entry.Installed);

    private static bool IsBuilderToolchainCallable(BuilderToolchainCapabilityRegistry registry, string toolId)
        => registry.Entries.Any(entry => string.Equals(entry.ToolId, toolId, StringComparison.Ordinal) && entry.Callable);

    private void RefreshBuilderRouteContinuityArtifacts(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return;
        }

        Directory.CreateDirectory(BuilderProofRootForRepo(repoRoot));
        var continuity = BuildBuilderRouteStateContinuity(repoRoot, LoadBuilderRouteStateContinuity(repoRoot));
        File.WriteAllText(
            BuilderRouteStateContinuityPathForRepo(repoRoot),
            JsonSerializer.Serialize(continuity, new JsonSerializerOptions { WriteIndented = true }));

        var currentStateIndex = BuildBuilderRouteCurrentStateIndex(repoRoot, continuity);
        File.WriteAllText(
            BuilderRouteCurrentStateIndexPathForRepo(repoRoot),
            JsonSerializer.Serialize(currentStateIndex, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static BuilderRouteStateContinuity BuildBuilderRouteStateContinuity(
        string repoRoot,
        BuilderRouteStateContinuity priorContinuity)
    {
        var mergedEntries = priorContinuity.Entries
            .ToDictionary(entry => entry.SourceProofRunId, StringComparer.Ordinal);
        foreach (var group in LoadBuilderProofHistory(repoRoot).Entries
                     .GroupBy(entry => new { entry.RunId, entry.RunFolder, entry.CompletedUtc }))
        {
            var snapshot = LoadBuilderRouteContinuitySnapshot(group.Key.RunId, group.Key.RunFolder, group.Key.CompletedUtc);
            if (snapshot is null)
            {
                continue;
            }

            mergedEntries[snapshot.SourceProofRunId] = new BuilderRouteStateContinuityEntry(
                snapshot.SourceProofRunId,
                snapshot.SourceRunFolder,
                snapshot.TaskClass,
                snapshot.DefaultRoute,
                snapshot.PreparedRoute,
                snapshot.RouteSourceState,
                snapshot.CurrentReadinessState,
                snapshot.ContradictionAttributionState,
                snapshot.ReconfirmationState,
                snapshot.DefaultRouteSuspended,
                snapshot.ContinuityState,
                0,
                0,
                snapshot.AvailableArtifacts,
                snapshot.Summary,
                snapshot.ObservedUtc);
        }

        var entries = new List<BuilderRouteStateContinuityEntry>();
        var overrideCycleCount = 0;
        var reconfirmationCycleCount = 0;
        var previousReconfirmed = false;
        foreach (var entry in mergedEntries.Values
                     .OrderBy(candidate => candidate.ObservedUtc)
                     .ThenBy(candidate => candidate.SourceProofRunId, StringComparer.Ordinal))
        {
            if (IsBuilderRouteOverrideContradictionCycle(entry))
            {
                overrideCycleCount++;
            }

            var isReconfirmed = string.Equals(entry.ReconfirmationState, "reconfirmed_default_route", StringComparison.Ordinal);
            if (isReconfirmed && !previousReconfirmed)
            {
                reconfirmationCycleCount++;
            }

            entries.Add(entry with
            {
                OverrideContradictionCycleCount = overrideCycleCount,
                ReconfirmationCycleCount = reconfirmationCycleCount
            });
            previousReconfirmed = isReconfirmed;
        }

        var latestEntry = entries.LastOrDefault();
        return new BuilderRouteStateContinuity(
            latestEntry?.SourceProofRunId ?? string.Empty,
            latestEntry?.SourceRunFolder ?? string.Empty,
            latestEntry?.TaskClass ?? string.Empty,
            latestEntry?.DefaultRoute ?? string.Empty,
            latestEntry?.PreparedRoute ?? string.Empty,
            latestEntry?.ContinuityState ?? string.Empty,
            latestEntry?.DefaultRouteSuspended ?? false,
            overrideCycleCount,
            reconfirmationCycleCount,
            entries.ToArray(),
            BuildBuilderRouteStateContinuitySummary(latestEntry, overrideCycleCount, reconfirmationCycleCount),
            BuilderRouteStateContinuityPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderRouteCurrentStateIndex BuildBuilderRouteCurrentStateIndex(
        string repoRoot,
        BuilderRouteStateContinuity continuity)
    {
        var latestEntry = continuity.Entries.LastOrDefault();
        if (latestEntry is null)
        {
            return new BuilderRouteCurrentStateIndex(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                0,
                0,
                Array.Empty<BuilderRouteCurrentStateArtifactIndexEntry>(),
                "No builder route current-state index recorded.",
                BuilderRouteCurrentStateIndexPathForRepo(repoRoot),
                DateTimeOffset.UtcNow);
        }

        var orderedEntries = continuity.Entries
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SourceProofRunId, StringComparer.Ordinal)
            .ToArray();
        var artifactEntries = new List<BuilderRouteCurrentStateArtifactIndexEntry>();
        foreach (var artifactKind in GetBuilderRouteCurrentStateArtifactKinds())
        {
            var reference = orderedEntries
                .Select(entry => new
                {
                    Entry = entry,
                    Artifact = entry.AvailableArtifacts.FirstOrDefault(candidate =>
                        string.Equals(candidate.ArtifactKind, artifactKind, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(candidate.ArtifactPath) &&
                        File.Exists(candidate.ArtifactPath))
                })
                .FirstOrDefault(candidate => candidate.Artifact is not null);
            if (reference?.Artifact is null)
            {
                continue;
            }

            artifactEntries.Add(new BuilderRouteCurrentStateArtifactIndexEntry(
                artifactKind,
                reference.Artifact.SourceProofRunId,
                reference.Artifact.SourceRunFolder,
                reference.Artifact.ArtifactPath,
                string.Equals(reference.Artifact.SourceProofRunId, latestEntry.SourceProofRunId, StringComparison.Ordinal)
                    ? "latest_run"
                    : "continuity_carried_forward"));
        }

        return new BuilderRouteCurrentStateIndex(
            latestEntry.SourceProofRunId,
            latestEntry.SourceRunFolder,
            latestEntry.TaskClass,
            latestEntry.DefaultRoute,
            latestEntry.PreparedRoute,
            latestEntry.CurrentReadinessState,
            latestEntry.ReconfirmationState,
            latestEntry.DefaultRouteSuspended,
            continuity.OverrideContradictionCycleCount,
            continuity.ReconfirmationCycleCount,
            artifactEntries,
            BuildBuilderRouteCurrentStateIndexSummary(latestEntry, artifactEntries),
            BuilderRouteCurrentStateIndexPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderRouteContinuitySnapshot? LoadBuilderRouteContinuitySnapshot(
        string runId,
        string runFolder,
        DateTimeOffset observedUtc)
    {
        var proofRun = LoadBuilderProofRun(runFolder);
        var intake = LoadBuilderRequestIntake(runFolder);
        var prep = LoadBuilderExecutionPrep(runFolder);
        var readinessGate = LoadBuilderReadinessGate(runFolder);
        var defaultRouteDecision = LoadBuilderDefaultRouteDecision(runFolder);
        var launchDecision = LoadBuilderLaunchDefaultDecision(runFolder);
        var overrideEvidence = LoadBuilderRouteOverrideEvidence(runFolder);
        var reconfirmation = LoadBuilderRouteReconfirmation(runFolder);
        var recovery = LoadBuilderDefaultRouteRecovery(runFolder);

        var taskClass = FirstNonEmpty(
            intake?.NormalizedTaskClass,
            defaultRouteDecision?.TaskClass,
            reconfirmation?.TaskClass,
            recovery?.TaskClass);
        var defaultRoute = FirstNonEmpty(
            defaultRouteDecision?.ChosenDefaultRoute,
            reconfirmation?.PriorConfirmedDefaultRoute,
            recovery?.SuspendedRoute,
            prep?.SelectedRoute);
        var preparedRoute = FirstNonEmpty(
            prep?.SelectedRoute,
            launchDecision?.ActualLaunchRoute,
            defaultRoute);
        if (proofRun is null &&
            string.IsNullOrWhiteSpace(taskClass) &&
            string.IsNullOrWhiteSpace(defaultRoute) &&
            string.IsNullOrWhiteSpace(preparedRoute))
        {
            return null;
        }

        var contradictionAttributionState = FirstNonEmpty(
            reconfirmation?.ContradictionAttributionState,
            recovery?.ContradictionAttributionState,
            readinessGate?.ContradictionAttributionState);
        var reconfirmationState = FirstNonEmpty(
            reconfirmation?.CurrentReconfirmationState,
            recovery?.DefaultRouteRestored == true ? "reconfirmed_default_route" : string.Empty,
            defaultRouteDecision?.DefaultRouteSuspended == true ? "default_still_suspended" : "not_required");
        var currentReadinessState = readinessGate?.CurrentReadinessGateState ?? "not_recorded";
        var routeSourceState = FirstNonEmpty(
            defaultRouteDecision?.RouteSourceState,
            intake?.RouteSourceState,
            launchDecision?.RouteSourceState);
        var recoveryIndicatesSuspended =
            string.Equals(reconfirmationState, "default_still_suspended", StringComparison.Ordinal) ||
            string.Equals(recovery?.RecoveryState, "default_still_suspended", StringComparison.Ordinal);
        var defaultRouteSuspended = defaultRouteDecision?.DefaultRouteSuspended
                                    ?? readinessGate?.DefaultRouteSuspended
                                    ?? recoveryIndicatesSuspended;
        var continuityState = DetermineBuilderRouteContinuityState(
            routeSourceState,
            defaultRouteSuspended,
            reconfirmationState,
            contradictionAttributionState,
            launchDecision?.OperatorDecisionState,
            overrideEvidence?.OverrideOutcomeComparisonState);
        var availableArtifacts = CollectBuilderRouteContinuityArtifacts(runId, runFolder);
        return new BuilderRouteContinuitySnapshot(
            runId,
            runFolder,
            taskClass,
            defaultRoute,
            preparedRoute,
            routeSourceState,
            currentReadinessState,
            contradictionAttributionState,
            reconfirmationState,
            defaultRouteSuspended,
            continuityState,
            availableArtifacts,
            BuildBuilderRouteContinuityEntrySummary(
                taskClass,
                defaultRoute,
                preparedRoute,
                continuityState,
                currentReadinessState,
                availableArtifacts.Count),
            observedUtc);
    }

    private static string DetermineBuilderRouteContinuityState(
        string routeSourceState,
        bool defaultRouteSuspended,
        string reconfirmationState,
        string contradictionAttributionState,
        string? operatorDecisionState,
        string? overrideOutcomeComparisonState)
    {
        if (string.Equals(reconfirmationState, "reconfirmed_default_route", StringComparison.Ordinal))
        {
            return "reconfirmed_default_route";
        }

        if (defaultRouteSuspended)
        {
            return contradictionAttributionState switch
            {
                "override_route_failure" => "override_contradiction_suspended",
                "default_route_failure" => "default_contradiction_suspended",
                _ => "route_suspended_pending_reconfirmation"
            };
        }

        if (string.Equals(operatorDecisionState, "operator_override_selected", StringComparison.Ordinal))
        {
            return string.Equals(overrideOutcomeComparisonState, "regressed_outcome", StringComparison.Ordinal)
                ? "override_contradiction_recorded"
                : "override_route_active";
        }

        return routeSourceState switch
        {
            "defaulted_by_confirmed_policy" => "confirmed_default_active",
            "suggested" => "suggested_default_active",
            _ => "route_state_observed"
        };
    }

    private static IReadOnlyList<BuilderRouteArtifactReference> CollectBuilderRouteContinuityArtifacts(string runId, string runFolder)
    {
        var artifacts = new List<BuilderRouteArtifactReference>();
        TryAddBuilderRouteArtifactReference(artifacts, "builder_readiness_gate", runId, runFolder, BuilderReadinessGatePath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_confirmed_task_classes", runId, runFolder, BuilderConfirmedTaskClassesPath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_default_route_decision", runId, runFolder, BuilderDefaultRouteDecisionPath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_launch_default_decision", runId, runFolder, BuilderLaunchDefaultDecisionPath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_route_override_evidence", runId, runFolder, BuilderRouteOverrideEvidencePath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_policy_review_candidates", runId, runFolder, BuilderPolicyReviewCandidatesPath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_route_reconfirmation", runId, runFolder, BuilderRouteReconfirmationPath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_default_route_recovery", runId, runFolder, BuilderDefaultRouteRecoveryPath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_readiness_contradictions", runId, runFolder, BuilderReadinessContradictionsPath(runFolder));
        TryAddBuilderRouteArtifactReference(artifacts, "builder_route_stability_summary", runId, runFolder, BuilderRouteStabilitySummaryPath(runFolder));
        return artifacts;
    }

    private static void TryAddBuilderRouteArtifactReference(
        ICollection<BuilderRouteArtifactReference> artifacts,
        string artifactKind,
        string runId,
        string runFolder,
        string artifactPath)
    {
        if (!string.IsNullOrWhiteSpace(artifactPath) && File.Exists(artifactPath))
        {
            artifacts.Add(new BuilderRouteArtifactReference(artifactKind, runId, runFolder, artifactPath));
        }
    }

    private static IReadOnlyList<string> GetBuilderRouteCurrentStateArtifactKinds()
        => new[]
        {
            "builder_readiness_gate",
            "builder_confirmed_task_classes",
            "builder_default_route_decision",
            "builder_launch_default_decision",
            "builder_route_override_evidence",
            "builder_policy_review_candidates",
            "builder_route_reconfirmation",
            "builder_default_route_recovery",
            "builder_readiness_contradictions",
            "builder_route_stability_summary"
        };

    private static bool IsBuilderRouteOverrideContradictionCycle(BuilderRouteStateContinuityEntry entry)
        => string.Equals(entry.ContinuityState, "override_contradiction_suspended", StringComparison.Ordinal) &&
           HasBuilderRouteArtifact(entry, "builder_launch_default_decision") &&
           HasBuilderRouteArtifact(entry, "builder_route_override_evidence");

    private static bool HasBuilderRouteArtifact(BuilderRouteStateContinuityEntry entry, string artifactKind)
        => entry.AvailableArtifacts.Any(candidate => string.Equals(candidate.ArtifactKind, artifactKind, StringComparison.Ordinal));

    private static string BuildBuilderRouteContinuityEntrySummary(
        string taskClass,
        string defaultRoute,
        string preparedRoute,
        string continuityState,
        string currentReadinessState,
        int artifactCount)
        => $"{FirstNonEmpty(taskClass, "builder route")} stayed in {continuityState} with default={FirstNonEmpty(defaultRoute, "not_recorded")} and prep={FirstNonEmpty(preparedRoute, "not_recorded")}. Readiness={FirstNonEmpty(currentReadinessState, "not_recorded")}. Artifacts={artifactCount}.";

    private static string BuildBuilderRouteStateContinuitySummary(
        BuilderRouteStateContinuityEntry? latestEntry,
        int overrideCycleCount,
        int reconfirmationCycleCount)
        => latestEntry is null
            ? "No builder route continuity recorded."
            : $"Builder route continuity is {latestEntry.ContinuityState} for {FirstNonEmpty(latestEntry.TaskClass, "the current task class")}. Default={FirstNonEmpty(latestEntry.DefaultRoute, "not_recorded")}. Override cycles={overrideCycleCount}. Reconfirmation cycles={reconfirmationCycleCount}.";

    private static string BuildBuilderRouteCurrentStateIndexSummary(
        BuilderRouteStateContinuityEntry latestEntry,
        IReadOnlyList<BuilderRouteCurrentStateArtifactIndexEntry> entries)
    {
        var carriedForwardCount = entries.Count(entry => string.Equals(entry.ResolutionState, "continuity_carried_forward", StringComparison.Ordinal));
        var latestRunCount = entries.Count(entry => string.Equals(entry.ResolutionState, "latest_run", StringComparison.Ordinal));
        return $"Authoritative builder route artifacts index {FirstNonEmpty(latestEntry.TaskClass, "the current task class")} on {FirstNonEmpty(latestEntry.DefaultRoute, "not_recorded")}. Carried forward {carriedForwardCount} artifact(s); latest run supplies {latestRunCount}. Reconfirmation={FirstNonEmpty(latestEntry.ReconfirmationState, "not_required")}.";
    }

    private static BuilderReadinessGate BuildBuilderReadinessGate(
        string repoRoot,
        string runFolder,
        BuilderProofRun proofRun,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep,
        PreparedBuilderExecutionLaunch? launch,
        PreparedBuilderExecutionResult? result)
    {
        var evidence = LoadBuilderReadinessEvidenceSnapshots(repoRoot, intake, prep);
        var supportingProofRunCount = evidence.Length;
        var supportingPreparedLaunches = evidence
            .Where(snapshot => snapshot.Launch is not null &&
                               string.Equals(snapshot.Launch.LaunchEligibilityState, "eligible", StringComparison.Ordinal) &&
                               snapshot.Result is not null)
            .ToArray();
        var confirmingLaunches = supportingPreparedLaunches
            .Where(snapshot => IsBuilderReadinessConfirmation(snapshot.Result))
            .ToArray();
        var contradictionEvidence = BuildBuilderReadinessContradictionEvidence(evidence);
        var contradictionAttributionState = DetermineBuilderContradictionAttributionState(contradictionEvidence);
        var reconfirmationThreshold = DetermineBuilderReconfirmationThreshold(
            contradictionAttributionState,
            BuilderReadinessRequiredPreparedLaunches);
        var contradictionNotes = contradictionEvidence
            .Select(item => item.Note)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var contradictionCount = contradictionEvidence.Length;
        var latestContradictionUtc = contradictionEvidence
            .OrderByDescending(item => item.ObservedUtc)
            .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
            .Select(item => (DateTimeOffset?)item.ObservedUtc)
            .FirstOrDefault();
        var freshSupportingProofRunCount = latestContradictionUtc.HasValue
            ? evidence.Count(snapshot => snapshot.ObservedUtc > latestContradictionUtc.Value)
            : supportingProofRunCount;
        var freshPreparedLaunchConfirmations = latestContradictionUtc.HasValue
            ? confirmingLaunches.Count(snapshot => snapshot.ObservedUtc > latestContradictionUtc.Value)
            : confirmingLaunches.Length;
        var repairLoopConfirmationCount = confirmingLaunches.Count(snapshot =>
            string.Equals(snapshot.Result?.FinalRouteOutcomeClassification, "launched_and_passed_with_repair", StringComparison.Ordinal));
        var readinessGateState = DetermineBuilderReadinessGateState(
            intake,
            supportingProofRunCount,
            confirmingLaunches.Length,
            contradictionCount,
            contradictionAttributionState,
            repairLoopConfirmationCount,
            freshSupportingProofRunCount,
            freshPreparedLaunchConfirmations,
            reconfirmationThreshold.RequiredFreshProofRunCount,
            reconfirmationThreshold.RequiredFreshPreparedLaunchConfirmations);
        var currentRecommendation = BuildBuilderReadinessRecommendation(readinessGateState, prep.SelectedRoute, intake.NormalizedTaskClass);
        var reconfirmationRequired = contradictionCount > 0;
        var defaultRouteSuspended = reconfirmationRequired &&
                                    !string.Equals(readinessGateState, "confirmed_for_bounded_use", StringComparison.Ordinal) &&
                                    !string.Equals(readinessGateState, "confirmed_with_repair_loop", StringComparison.Ordinal);
        var reconfirmationStatus = BuildBuilderReconfirmationStatus(
            reconfirmationRequired,
            readinessGateState,
            freshSupportingProofRunCount,
            freshPreparedLaunchConfirmations,
            reconfirmationThreshold.RequiredFreshProofRunCount,
            reconfirmationThreshold.RequiredFreshPreparedLaunchConfirmations);
        var latestSupportingArtifactPaths = confirmingLaunches
            .SelectMany(snapshot => new string[]
            {
                snapshot.Result?.ArtifactPath ?? string.Empty,
                snapshot.Launch?.ArtifactPath ?? string.Empty,
                snapshot.Prep.ArtifactPath,
                snapshot.Intake.ArtifactPath
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        var linkedArtifactPaths = latestSupportingArtifactPaths
            .Concat(contradictionNotes.Select(_ => BuilderExecutionResultPath(runFolder)).Where(BuilderArtifactPathExists))
            .Concat(new[]
            {
                proofRun.RunArtifactPath,
                BuilderDefaultPolicyPath(runFolder),
                BuilderRequestIntakePath(runFolder),
                BuilderExecutionPrepPath(runFolder),
                BuilderExecutionLaunchPath(runFolder),
                BuilderExecutionResultPath(runFolder),
                BuilderConfirmedTaskClassesPath(runFolder),
                BuilderDefaultRouteDecisionPath(runFolder),
                BuilderReadinessContradictionsPath(runFolder),
                BuilderReadinessGateHistoryPathForRepo(repoRoot)
            })
            .Concat(contradictionEvidence.Select(item => item.ArtifactPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var latestComparison = result?.PreparedRouteComparisonState
            ?? evidence.Select(snapshot => snapshot.Result?.PreparedRouteComparisonState)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "not_recorded";
        var builderReadyForBoundedUse =
            string.Equals(readinessGateState, "confirmed_for_bounded_use", StringComparison.Ordinal) ||
             string.Equals(readinessGateState, "confirmed_with_repair_loop", StringComparison.Ordinal);
        var summary = $"{prep.SelectedRoute} for {intake.NormalizedTaskClass} is {readinessGateState} with {supportingProofRunCount} supporting proof run(s), {supportingPreparedLaunches.Length} prepared launch(es), {confirmingLaunches.Length} confirmation(s), and {contradictionCount} contradiction(s). " +
                      $"{BuildBuilderContradictionAttributionNarration(contradictionAttributionState, reconfirmationThreshold)} " +
                      $"{BuildBuilderReconfirmationNarration(reconfirmationStatus, freshSupportingProofRunCount, freshPreparedLaunchConfirmations)} {currentRecommendation}".Trim();

        return new BuilderReadinessGate(
            proofRun.ProofRunId,
            intake.RequestId,
            prep.SourceIntakeId,
            intake.CurrentModelId,
            intake.ProofScope,
            intake.TargetId,
            intake.TargetLabel,
            intake.NormalizedTaskClass,
            prep.SelectedRoute,
            BuilderReadinessRequiredProofRuns,
            BuilderReadinessRequiredPreparedLaunches,
            supportingProofRunCount,
            supportingPreparedLaunches.Length,
            confirmingLaunches.Length,
            contradictionCount,
            latestComparison,
            readinessGateState,
            builderReadyForBoundedUse,
            currentRecommendation,
            contradictionNotes,
            latestSupportingArtifactPaths,
            linkedArtifactPaths,
            summary,
            BuilderReadinessGatePath(runFolder),
            DateTimeOffset.UtcNow,
            freshSupportingProofRunCount,
            freshPreparedLaunchConfirmations,
            reconfirmationRequired,
            defaultRouteSuspended,
            reconfirmationStatus,
            contradictionAttributionState,
            reconfirmationThreshold.RequiredFreshProofRunCount,
            reconfirmationThreshold.RequiredFreshPreparedLaunchConfirmations);
    }

    private static BuilderReadinessEvidenceSnapshot[] LoadBuilderReadinessEvidenceSnapshots(
        string repoRoot,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep)
        => LoadBuilderProofHistory(repoRoot).Entries
            .GroupBy(entry => new { entry.RunId, entry.RunFolder, entry.CompletedUtc })
            .OrderByDescending(group => group.Key.CompletedUtc)
            .ThenByDescending(group => group.Key.RunId, StringComparer.Ordinal)
            .Select(group =>
            {
                var snapshotIntake = LoadBuilderRequestIntake(group.Key.RunFolder);
                var snapshotPrep = LoadBuilderExecutionPrep(group.Key.RunFolder);
                if (snapshotIntake is null || snapshotPrep is null)
                {
                    return null;
                }

                return new BuilderReadinessEvidenceSnapshot(
                    group.Key.RunId,
                    group.Key.RunFolder,
                    group.Key.CompletedUtc,
                    snapshotIntake,
                    snapshotPrep,
                    LoadBuilderExecutionLaunch(group.Key.RunFolder),
                    LoadBuilderExecutionResult(group.Key.RunFolder));
            })
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .Where(snapshot =>
                string.Equals(snapshot.Intake.CurrentModelId, intake.CurrentModelId, StringComparison.Ordinal) &&
                string.Equals(snapshot.Intake.ProofScope, intake.ProofScope, StringComparison.Ordinal) &&
                string.Equals(snapshot.Intake.NormalizedTaskClass, intake.NormalizedTaskClass, StringComparison.Ordinal) &&
                string.Equals(snapshot.Prep.SelectedRoute, prep.SelectedRoute, StringComparison.Ordinal))
            .ToArray();

    private static bool IsBuilderReadinessConfirmation(PreparedBuilderExecutionResult? result)
        => result is not null &&
           (string.Equals(result.FinalRouteOutcomeClassification, "launched_and_passed", StringComparison.Ordinal) ||
            string.Equals(result.FinalRouteOutcomeClassification, "launched_and_passed_with_repair", StringComparison.Ordinal)) &&
           !string.Equals(result.PreparedRouteComparisonState, "insufficient_for_scope", StringComparison.Ordinal);

    private static BuilderReadinessContradictionEvidence[] BuildBuilderReadinessContradictionEvidence(IReadOnlyList<BuilderReadinessEvidenceSnapshot> evidence)
        => evidence
            .Select(BuildBuilderReadinessContradictionEvidence)
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Note, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.ObservedUtc)
                .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
                .First())
            .ToArray();

    private static BuilderReadinessContradictionEvidence? BuildBuilderReadinessContradictionEvidence(BuilderReadinessEvidenceSnapshot snapshot)
    {
        if (snapshot.Result is null)
        {
            return null;
        }

        if (!IsBuilderRouteContradiction(snapshot.Result))
        {
            return null;
        }

        var expectedRoute = snapshot.Prep.SelectedRoute;
        var contradictoryRoute = ResolveBuilderActualRoute(snapshot.Prep.SelectedRoute, snapshot.Launch, snapshot.Result);
        var attributionState = DetermineBuilderContradictionAttributionState(expectedRoute, contradictoryRoute);
        return new BuilderReadinessContradictionEvidence(
            snapshot.RunId,
            snapshot.ObservedUtc,
            BuildBuilderRouteContradictionNote(
                snapshot.RunId,
                expectedRoute,
                contradictoryRoute,
                attributionState,
                snapshot.Result.FinalRouteOutcomeClassification,
                snapshot.Result.PreparedRouteComparisonState),
            snapshot.Result.ArtifactPath,
            attributionState,
            contradictoryRoute,
            expectedRoute);
    }

    private static bool IsBuilderRouteContradiction(PreparedBuilderExecutionResult result)
        => string.Equals(result.FinalRouteOutcomeClassification, "launched_and_failed_out_of_scope", StringComparison.Ordinal) ||
           string.Equals(result.FinalRouteOutcomeClassification, "launched_and_failed_followup_created", StringComparison.Ordinal) ||
           string.Equals(result.PreparedRouteComparisonState, "insufficient_for_scope", StringComparison.Ordinal);

    private static string ResolveBuilderActualRoute(
        string expectedRoute,
        PreparedBuilderExecutionLaunch? launch,
        PreparedBuilderExecutionResult? result)
        => FirstNonEmpty(
            result?.ActualRouteUsed,
            launch?.SelectedRoute,
            expectedRoute);

    private static string DetermineBuilderContradictionAttributionState(
        string expectedRoute,
        string contradictoryRoute)
    {
        if (string.IsNullOrWhiteSpace(expectedRoute) || string.IsNullOrWhiteSpace(contradictoryRoute))
        {
            return "mixed_or_ambiguous";
        }

        return string.Equals(expectedRoute, contradictoryRoute, StringComparison.Ordinal)
            ? "default_route_failure"
            : "override_route_failure";
    }

    private static string DetermineBuilderContradictionAttributionState(IReadOnlyList<BuilderReadinessContradictionEvidence> evidence)
    {
        if (evidence.Count == 0)
        {
            return "none";
        }

        var distinctStates = evidence
            .Select(item => item.ContradictionAttributionState)
            .Where(state => !string.IsNullOrWhiteSpace(state))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctStates.Length == 1)
        {
            return distinctStates[0];
        }

        return "mixed_or_ambiguous";
    }

    private static BuilderReconfirmationThreshold DetermineBuilderReconfirmationThreshold(
        string contradictionAttributionState,
        int defaultPreparedLaunchThreshold)
        => string.Equals(contradictionAttributionState, "override_route_failure", StringComparison.Ordinal)
            ? new BuilderReconfirmationThreshold(
                contradictionAttributionState,
                BuilderOverrideRecoveryRequiredProofRuns,
                BuilderOverrideRecoveryRequiredPreparedLaunches)
            : new BuilderReconfirmationThreshold(
                contradictionAttributionState,
                BuilderReadinessRequiredProofRuns,
                defaultPreparedLaunchThreshold);

    private static string BuildBuilderRouteContradictionNote(
        string runId,
        string expectedRoute,
        string contradictoryRoute,
        string attributionState,
        string outcomeClassification,
        string comparisonState)
    {
        var scopeFailure = string.Equals(outcomeClassification, "launched_and_failed_out_of_scope", StringComparison.Ordinal) ||
                           string.Equals(comparisonState, "insufficient_for_scope", StringComparison.Ordinal);
        if (string.Equals(attributionState, "override_route_failure", StringComparison.Ordinal))
        {
            return scopeFailure
                ? $"{runId}: override route {contradictoryRoute} was insufficient for scope while default route {expectedRoute} remained the confirmed candidate."
                : $"{runId}: override route {contradictoryRoute} failed and returned to follow-up while default route {expectedRoute} remained the confirmed candidate.";
        }

        if (string.Equals(attributionState, "default_route_failure", StringComparison.Ordinal))
        {
            return scopeFailure
                ? $"{runId}: default route {expectedRoute} was insufficient for scope."
                : $"{runId}: default route {expectedRoute} failed and returned to follow-up work.";
        }

        return scopeFailure
            ? $"{runId}: route evidence was insufficient for scope and could not be attributed cleanly."
            : $"{runId}: route evidence failed and returned to follow-up without clean attribution.";
    }

    private static string BuildBuilderContradictionAttributionNarration(
        string contradictionAttributionState,
        BuilderReconfirmationThreshold threshold)
        => contradictionAttributionState switch
        {
            "override_route_failure" => $"Latest contradiction came from an override route, so recovery needs {threshold.RequiredFreshPreparedLaunchConfirmations} fresh default launch confirmation(s).",
            "default_route_failure" => $"Latest contradiction came from the default route itself, so recovery needs {threshold.RequiredFreshPreparedLaunchConfirmations} fresh default launch confirmation(s).",
            "mixed_or_ambiguous" => $"Latest contradiction evidence is mixed, so recovery keeps the full corroboration threshold of {threshold.RequiredFreshPreparedLaunchConfirmations} launch confirmation(s).",
            _ => string.Empty
        };

    private static string DetermineBuilderReadinessGateState(
        BuilderRequestIntake intake,
        int supportingProofRunCount,
        int confirmationCount,
        int contradictionCount,
        string contradictionAttributionState,
        int repairLoopConfirmationCount,
        int freshSupportingProofRunCount,
        int freshConfirmationCount,
        int requiredFreshProofRunCount,
        int requiredFreshConfirmationCount)
    {
        var repairLoopExpected =
            repairLoopConfirmationCount > 0 ||
            string.Equals(intake.IntakeClassificationState, "ready_for_low_floor_with_repair_loop", StringComparison.Ordinal);
        if (contradictionCount > 0)
        {
            if (freshSupportingProofRunCount >= requiredFreshProofRunCount &&
                freshConfirmationCount >= requiredFreshConfirmationCount)
            {
                return repairLoopExpected
                    ? "confirmed_with_repair_loop"
                    : "confirmed_for_bounded_use";
            }

            if (!string.Equals(contradictionAttributionState, "override_route_failure", StringComparison.Ordinal) &&
                (contradictionCount >= 2 || confirmationCount == 0))
            {
                return "contradicted";
            }

            return "unstable_needs_more_evidence";
        }

        if (confirmationCount >= BuilderReadinessRequiredPreparedLaunches &&
            supportingProofRunCount >= BuilderReadinessRequiredProofRuns)
        {
            if (repairLoopExpected)
            {
                return "confirmed_with_repair_loop";
            }

            return "confirmed_for_bounded_use";
        }

        return "provisional";
    }

    private static string BuildBuilderReadinessRecommendation(string readinessGateState, string route, string taskClass)
        => readinessGateState switch
        {
            "confirmed_for_bounded_use" => $"{route} is builder-ready for bounded {taskClass} work.",
            "confirmed_with_repair_loop" => $"{route} is builder-ready for bounded {taskClass} work, but the repair loop should stay enabled.",
            "unstable_needs_more_evidence" => $"{route} should stay in bounded use only while more confirmation evidence is gathered.",
            "contradicted" => $"{route} should not be treated as builder-ready until new proof and prepared-launch evidence replaces the contradiction.",
            _ => $"{route} still needs repeated prepared-launch confirmation before it can be treated as builder-ready."
        };

    private static BuilderReadinessGateHistory BuildBuilderReadinessGateHistory(
        BuilderReadinessGateHistory priorHistory,
        BuilderReadinessGate current)
    {
        var previous = priorHistory.Entries
            .Where(entry =>
                string.Equals(entry.TaskClass, current.TaskClass, StringComparison.Ordinal) &&
                string.Equals(entry.CurrentRoute, current.CurrentRoute, StringComparison.Ordinal) &&
                !string.Equals(entry.SourceProofRunId, current.SourceProofRunId, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SourceProofRunId, StringComparer.Ordinal)
            .FirstOrDefault();
        var changeReason = DetermineBuilderReadinessHistoryReason(previous, current);
        var currentEntry = new BuilderReadinessGateHistoryEntry(
            current.SourceProofRunId,
            current.SourceIntakeId,
            current.TaskClass,
            current.CurrentRoute,
            current.CurrentReadinessGateState,
            current.ConfirmationCount,
            current.ContradictionCount,
            changeReason,
            current.ArtifactPath,
            current.Summary,
            current.ObservedUtc);
        var retentionCount = Math.Max(priorHistory.RetentionCount, BuilderReadinessGateHistoryRetentionCount);
        var entries = priorHistory.Entries
            .Where(entry =>
                !string.Equals(entry.SourceProofRunId, current.SourceProofRunId, StringComparison.Ordinal) ||
                !string.Equals(entry.CurrentRoute, current.CurrentRoute, StringComparison.Ordinal))
            .Concat(new[] { currentEntry })
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SourceProofRunId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();

        return new BuilderReadinessGateHistory(retentionCount, entries);
    }

    private static string DetermineBuilderReadinessHistoryReason(
        BuilderReadinessGateHistoryEntry? previous,
        BuilderReadinessGate current)
    {
        if (previous is null)
        {
            return $"Initial readiness capture from {current.SupportingProofRunCount} proof run(s) and {current.ConfirmationCount} confirming launch(es).";
        }

        if (string.Equals(previous.ReadinessGateState, current.CurrentReadinessGateState, StringComparison.Ordinal))
        {
            if (current.ReconfirmationRequired &&
                string.Equals(current.ReconfirmationStatus, "reconfirmed_after_contradiction", StringComparison.Ordinal))
            {
                return $"Readiness returned to {current.CurrentReadinessGateState} after contradiction handling gathered {current.FreshPreparedLaunchConfirmationCountAfterLatestContradiction} fresh confirming launch(es).";
            }

            return $"Readiness remained {current.CurrentReadinessGateState} with {current.ConfirmationCount} confirmation(s) and {current.ContradictionCount} contradiction(s).";
        }

        var currentRank = ScoreBuilderReadinessGateState(current.CurrentReadinessGateState);
        var previousRank = ScoreBuilderReadinessGateState(previous.ReadinessGateState);
        return currentRank > previousRank
            ? $"Upgraded from {previous.ReadinessGateState} to {current.CurrentReadinessGateState} after reaching {current.ConfirmationCount} confirmation(s)."
            : $"Downgraded from {previous.ReadinessGateState} to {current.CurrentReadinessGateState} because contradiction evidence reached {current.ContradictionCount}.";
    }

    private static int ScoreBuilderReadinessGateState(string readinessGateState)
        => readinessGateState switch
        {
            "confirmed_for_bounded_use" => 4,
            "confirmed_with_repair_loop" => 3,
            "provisional" => 2,
            "unstable_needs_more_evidence" => 1,
            "contradicted" => 0,
            _ => -1
        };

    private static string BuildBuilderRouteStabilitySummaryMarkdown(BuilderReadinessGate gate)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Route Stability Summary");
        builder.AppendLine();
        builder.AppendLine("## Route");
        builder.AppendLine($"- Task class: {gate.TaskClass}");
        builder.AppendLine($"- Route: {gate.CurrentRoute}");
        builder.AppendLine($"- Readiness state: {gate.CurrentReadinessGateState}");
        builder.AppendLine();
        builder.AppendLine("## Evidence");
        builder.AppendLine($"- Supporting proof runs: {gate.SupportingProofRunCount}");
        builder.AppendLine($"- Supporting prepared launches: {gate.SupportingPreparedLaunchCount}");
        builder.AppendLine($"- Confirmation count: {gate.ConfirmationCount}");
        builder.AppendLine($"- Contradiction count: {gate.ContradictionCount}");
        builder.AppendLine($"- Latest route comparison: {gate.LatestPolicyResultComparison}");
        builder.AppendLine($"- Reconfirmation status: {gate.ReconfirmationStatus}");
        builder.AppendLine($"- Contradiction attribution: {gate.ContradictionAttributionState}");
        builder.AppendLine($"- Fresh proof runs after latest contradiction: {gate.FreshProofRunCountAfterLatestContradiction}");
        builder.AppendLine($"- Fresh launch confirmations after latest contradiction: {gate.FreshPreparedLaunchConfirmationCountAfterLatestContradiction}");
        builder.AppendLine($"- Reconfirmation proof threshold: {gate.RequiredFreshProofRunCountForReconfirmation}");
        builder.AppendLine($"- Reconfirmation launch threshold: {gate.RequiredFreshPreparedLaunchConfirmationsForReconfirmation}");
        builder.AppendLine($"- Default route suspended: {gate.DefaultRouteSuspended}");
        builder.AppendLine();
        builder.AppendLine("## Recommendation");
        builder.AppendLine($"- {gate.CurrentRecommendation}");
        if (gate.ContradictionNotes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Contradictions");
            foreach (var note in gate.ContradictionNotes)
            {
                builder.AppendLine($"- {note}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static BuilderConfirmedTaskClasses BuildBuilderConfirmedTaskClasses(
        string repoRoot,
        string runFolder,
        BuilderDefaultPolicy policy)
    {
        var entries = policy.TaskClassEntries
            .Select(entry => BuildBuilderConfirmedTaskClassEntry(repoRoot, runFolder, policy, entry))
            .OrderBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .ToArray();
        var confirmed = entries.Count(entry => string.Equals(entry.SummaryClassification, "confirmed_for_bounded_use", StringComparison.Ordinal));
        var repairLoop = entries.Count(entry => string.Equals(entry.SummaryClassification, "confirmed_with_repair_loop", StringComparison.Ordinal));
        var provisional = entries.Count(entry => string.Equals(entry.SummaryClassification, "provisional", StringComparison.Ordinal));
        var contradicted = entries.Count(entry => string.Equals(entry.SummaryClassification, "contradicted", StringComparison.Ordinal));
        var notYetProven = entries.Count(entry => string.Equals(entry.SummaryClassification, "not_yet_proven", StringComparison.Ordinal));
        var linkedArtifactPaths = entries
            .SelectMany(entry => entry.LinkedEvidencePaths)
            .Concat(new[]
            {
                policy.ArtifactPath,
                BuilderDefaultPolicyHistoryPathForRepo(repoRoot),
                BuilderReadinessGateHistoryPathForRepo(repoRoot)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{confirmed} task class(es) are confirmed for bounded use, {repairLoop} require the repair loop, {provisional} remain provisional, {contradicted} are contradicted, and {notYetProven} are not yet proven.";

        return new BuilderConfirmedTaskClasses(
            policy.SourceProofRunId,
            policy.CurrentModelId,
            entries,
            linkedArtifactPaths,
            summary,
            BuilderConfirmedTaskClassesPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderConfirmedTaskClassEntry BuildBuilderConfirmedTaskClassEntry(
        string repoRoot,
        string runFolder,
        BuilderDefaultPolicy policy,
        BuilderDefaultPolicyTaskClassEntry entry)
    {
        var currentRoute = MapBuilderPolicyStateToRoute(entry.PolicyState);
        var evidence = LoadBuilderTaskClassEvidenceSnapshots(repoRoot, entry);
        var routeEvidence = evidence
            .Where(snapshot => string.Equals(snapshot.CurrentRoute, currentRoute, StringComparison.Ordinal))
            .ToArray();
        var supportingProofRunCount = routeEvidence.Length;
        var usesPreparedLaunchConfirmations =
            string.Equals(currentRoute, "split_first_low_floor_route", StringComparison.Ordinal) ||
            routeEvidence.Any(snapshot => snapshot.Result is not null && string.Equals(snapshot.Prep?.SelectedRoute, currentRoute, StringComparison.Ordinal));
        var confirmingProofRuns = routeEvidence
            .Where(snapshot => snapshot.CaseResult is not null && IsBuilderProofConfirmation(snapshot.CaseResult))
            .ToArray();
        var supportingPreparedLaunches = routeEvidence
            .Where(snapshot => snapshot.Launch is not null &&
                               string.Equals(snapshot.Launch.LaunchEligibilityState, "eligible", StringComparison.Ordinal) &&
                               snapshot.Result is not null)
            .ToArray();
        var confirmingLaunches = supportingPreparedLaunches
            .Where(snapshot => IsBuilderReadinessConfirmation(snapshot.Result))
            .ToArray();
        var contradictionEvidence = routeEvidence
            .Select(snapshot => BuildBuilderTaskClassContradictionEvidence(snapshot, currentRoute))
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Note, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.ObservedUtc)
                .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
                .First())
            .ToArray();
        var contradictionAttributionState = DetermineBuilderContradictionAttributionState(contradictionEvidence);
        var reconfirmationThreshold = DetermineBuilderReconfirmationThreshold(
            contradictionAttributionState,
            usesPreparedLaunchConfirmations ? BuilderReadinessRequiredPreparedLaunches : 0);
        var latestContradictionUtc = contradictionEvidence
            .OrderByDescending(item => item.ObservedUtc)
            .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
            .Select(item => (DateTimeOffset?)item.ObservedUtc)
            .FirstOrDefault();
        var freshProofRunCount = latestContradictionUtc.HasValue
            ? routeEvidence.Count(snapshot => snapshot.ObservedUtc > latestContradictionUtc.Value)
            : supportingProofRunCount;
        var freshConfirmationCount = usesPreparedLaunchConfirmations
            ? (latestContradictionUtc.HasValue
                ? confirmingLaunches.Count(snapshot => snapshot.ObservedUtc > latestContradictionUtc.Value)
                : confirmingLaunches.Length)
            : (latestContradictionUtc.HasValue
                ? confirmingProofRuns.Count(snapshot => snapshot.ObservedUtc > latestContradictionUtc.Value)
                : confirmingProofRuns.Length);
        var freshPreparedLaunchConfirmations = latestContradictionUtc.HasValue
            ? confirmingLaunches.Count(snapshot => snapshot.ObservedUtc > latestContradictionUtc.Value)
            : confirmingLaunches.Length;
        var confirmationCount = usesPreparedLaunchConfirmations
            ? confirmingLaunches.Length
            : confirmingProofRuns.Length;
        var repairLoopExpected =
            string.Equals(entry.PolicyState, "low_floor_with_repair_loop_expected", StringComparison.Ordinal) ||
            confirmingLaunches.Any(snapshot =>
                string.Equals(snapshot.Result?.FinalRouteOutcomeClassification, "launched_and_passed_with_repair", StringComparison.Ordinal));
        var contradictionCount = contradictionEvidence.Length;
        var currentReadinessState = DetermineBuilderTaskClassReadinessState(
            entry.PolicyState,
            supportingProofRunCount,
            confirmationCount,
            contradictionCount,
            contradictionAttributionState,
            repairLoopExpected,
            reconfirmationThreshold.RequiredFreshPreparedLaunchConfirmations,
            freshProofRunCount,
            freshConfirmationCount,
            freshPreparedLaunchConfirmations);
        var summaryClassification = DetermineBuilderTaskClassSummaryClassification(
            entry.PolicyState,
            supportingProofRunCount,
            currentReadinessState);
        var builderReadyForBoundedUse =
            string.Equals(currentReadinessState, "confirmed_for_bounded_use", StringComparison.Ordinal) ||
            string.Equals(currentReadinessState, "confirmed_with_repair_loop", StringComparison.Ordinal);
        var reconfirmationRequired = contradictionCount > 0;
        var defaultRouteSuspended = reconfirmationRequired && !builderReadyForBoundedUse;
        var reconfirmationStatus = BuildBuilderTaskClassReconfirmationStatus(
            reconfirmationRequired,
            currentReadinessState,
            freshProofRunCount,
            freshPreparedLaunchConfirmations,
            reconfirmationThreshold.RequiredFreshProofRunCount,
            reconfirmationThreshold.RequiredFreshPreparedLaunchConfirmations);
        var latestContradictionNote = contradictionEvidence
            .OrderByDescending(item => item.ObservedUtc)
            .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
            .Select(item => item.Note)
            .FirstOrDefault() ?? string.Empty;
        var linkedEvidencePaths = routeEvidence
            .SelectMany(snapshot => new[]
            {
                snapshot.ProofRunArtifactPath,
                snapshot.CaseResult?.ValidationResultPath ?? string.Empty,
                snapshot.Intake?.ArtifactPath ?? string.Empty,
                snapshot.Prep?.ArtifactPath ?? string.Empty,
                snapshot.Launch?.ArtifactPath ?? string.Empty,
                snapshot.Result?.ArtifactPath ?? string.Empty
            })
            .Concat(entry.LinkedEvidencePaths)
            .Concat(new[]
            {
                BuilderDefaultPolicyPath(runFolder),
                BuilderDefaultPolicyHistoryPathForRepo(repoRoot),
                BuilderReadinessGateHistoryPathForRepo(repoRoot)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{entry.TargetLabel} uses {currentRoute} and is {currentReadinessState} with {confirmationCount} confirmation(s) and {contradictionCount} contradiction(s). {BuildBuilderTaskClassReadinessRecommendation(currentReadinessState, currentRoute, entry.TaskClass, defaultRouteSuspended)}";

        return new BuilderConfirmedTaskClassEntry(
            entry.ProofScope,
            entry.TargetId,
            entry.TargetLabel,
            entry.TaskClass,
            entry.PolicyState,
            currentRoute,
            supportingProofRunCount,
            supportingPreparedLaunches.Length,
            confirmationCount,
            contradictionCount,
            currentReadinessState,
            summaryClassification,
            builderReadyForBoundedUse,
            BuilderReadinessRequiredProofRuns,
            usesPreparedLaunchConfirmations ? BuilderReadinessRequiredPreparedLaunches : 0,
            freshProofRunCount,
            freshPreparedLaunchConfirmations,
            reconfirmationRequired,
            defaultRouteSuspended,
            reconfirmationStatus,
            contradictionAttributionState,
            latestContradictionNote,
            contradictionEvidence.Select(item => item.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            linkedEvidencePaths,
            summary);
    }

    private static BuilderReadinessContradictions BuildBuilderReadinessContradictions(
        string runFolder,
        BuilderConfirmedTaskClasses confirmedTaskClasses)
    {
        var entries = confirmedTaskClasses.Entries
            .Where(entry => entry.ContradictionCount > 0)
            .Select(entry => new BuilderReadinessContradictionEntry(
                entry.ProofScope,
                entry.TargetId,
                entry.TargetLabel,
                entry.TaskClass,
                entry.CurrentRoute,
                entry.ContradictionAttributionState,
                ResolveBuilderContradictoryRoute(entry),
                entry.ContradictoryRunIds,
                entry.LatestContradictionNote,
                entry.CurrentReadinessState,
                entry.DefaultRouteSuspended,
                DetermineBuilderReconfirmationThreshold(entry.ContradictionAttributionState, entry.RequiredPreparedLaunchConfirmations).RequiredFreshProofRunCount,
                DetermineBuilderReconfirmationThreshold(entry.ContradictionAttributionState, entry.RequiredPreparedLaunchConfirmations).RequiredFreshPreparedLaunchConfirmations,
                entry.FreshProofRunCountAfterLatestContradiction,
                entry.FreshPreparedLaunchConfirmationCountAfterLatestContradiction,
                entry.ReconfirmationStatus,
                entry.LinkedEvidencePaths))
            .OrderBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .ToArray();
        var summary = entries.Length == 0
            ? "No builder readiness contradictions are currently recorded."
            : $"{entries.Length} task class(es) carry contradiction evidence; override-only contradictions stay separated from true default-route failures while fresh corroboration rebuilds trust.";

        return new BuilderReadinessContradictions(
            confirmedTaskClasses.SourceProofRunId,
            entries,
            summary,
            BuilderReadinessContradictionsPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderDefaultRouteDecision BuildBuilderDefaultRouteDecision(
        string runFolder,
        BuilderRequestPolicyDecision requestDecision,
        BuilderPolicyStability stability,
        BuilderConfirmedTaskClasses confirmedTaskClasses,
        BuilderReadinessContradictions contradictions)
    {
        var confirmedEntry = confirmedTaskClasses.Entries.FirstOrDefault(entry =>
            string.Equals(entry.ProofScope, requestDecision.ProofScope, StringComparison.Ordinal) &&
            string.Equals(entry.TargetId, requestDecision.TargetId, StringComparison.Ordinal) &&
            string.Equals(entry.TaskClass, requestDecision.TaskClass, StringComparison.Ordinal));
        var chosenRoute = MapBuilderPolicyStateToRoute(requestDecision.ChosenPolicyState);
        var routeSourceState = confirmedEntry is not null &&
                               confirmedEntry.BuilderReadyForBoundedUse &&
                               !confirmedEntry.DefaultRouteSuspended &&
                               string.Equals(confirmedEntry.CurrentRoute, chosenRoute, StringComparison.Ordinal)
            ? "defaulted_by_confirmed_policy"
            : "suggested";
        var operatorOverrideState = "override_available_no_override";
        var defaultSuspended = confirmedEntry?.DefaultRouteSuspended ?? false;
        var confirmationEvidence = confirmedEntry?.ConfirmationCount ?? 0;
        var contradictionCount = confirmedEntry?.ContradictionCount ?? 0;
        var strongerTierRoleSummary = BuildBuilderRequestDefaultRouteReason(requestDecision, confirmedEntry, routeSourceState, defaultSuspended);
        var linkedArtifactPaths = requestDecision.LinkedEvidencePaths
            .Concat(stability.LatestCorroboratingArtifacts)
            .Concat(new[]
            {
                requestDecision.ArtifactPath,
                stability.ArtifactPath,
                confirmedTaskClasses.ArtifactPath,
                contradictions.ArtifactPath,
                BuilderReadinessGatePath(runFolder)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{requestDecision.TaskClass} will use {chosenRoute} as {routeSourceState}. {strongerTierRoleSummary}";

        return new BuilderDefaultRouteDecision(
            requestDecision.SourceProofRunId,
            requestDecision.TargetId,
            requestDecision.TargetLabel,
            requestDecision.TaskClass,
            chosenRoute,
            routeSourceState,
            operatorOverrideState,
            defaultSuspended,
            confirmationEvidence,
            contradictionCount,
            strongerTierRoleSummary,
            linkedArtifactPaths,
            summary,
            BuilderDefaultRouteDecisionPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderExecutionPrep ApplyBuilderRouteOverride(
        BuilderExecutionPrep prep,
        string? routeOverride,
        string? overrideReason,
        BuilderDefaultRouteDecision? defaultRouteDecision)
    {
        if (string.IsNullOrWhiteSpace(routeOverride) ||
            string.Equals(routeOverride, prep.SelectedRoute, StringComparison.Ordinal))
        {
            return prep;
        }

        var normalizedReason = string.IsNullOrWhiteSpace(overrideReason)
            ? BuildBuilderRouteOverrideReason(prep.SelectedRoute, routeOverride, prep.NormalizedTaskClass)
            : overrideReason.Trim();
        var splitPlanRequired = string.Equals(routeOverride, "split_first_low_floor_route", StringComparison.Ordinal);
        var requiredEvidencePaths = splitPlanRequired
            ? prep.RequiredEvidencePaths
            : prep.RequiredEvidencePaths
                .Where(path => !string.Equals(path, prep.SplitPlanPath, StringComparison.Ordinal) &&
                               !prep.FutureExecutionHookPaths.Contains(path, StringComparer.Ordinal))
                .ToArray();
        return prep with
        {
            SelectedRoute = routeOverride,
            SplitPlanRequired = splitPlanRequired,
            SplitPlanPath = splitPlanRequired ? prep.SplitPlanPath : string.Empty,
            FutureExecutionHookPaths = splitPlanRequired ? prep.FutureExecutionHookPaths : Array.Empty<string>(),
            RequiredEvidencePaths = requiredEvidencePaths,
            RouteSourceState = "overridden_by_operator",
            OperatorOverrideState = "overridden_by_operator",
            DefaultRouteReason = normalizedReason,
            Summary = $"{prep.TargetLabel} is prepared on route {routeOverride}. Route source=overridden_by_operator. {normalizedReason}"
        };
    }

    private static string BuildBuilderRouteOverrideReason(string defaultRoute, string overrideRoute, string taskClass)
        => defaultRoute switch
        {
            "split_first_low_floor_route" => $"Operator override selected {overrideRoute} to compare an unsplit launch against the confirmed split-first default for {taskClass}.",
            "low_floor_with_repair_loop_route" => $"Operator override selected {overrideRoute} to compare a no-repair launch against the repair-loop default for {taskClass}.",
            _ => $"Operator override selected {overrideRoute} instead of {defaultRoute} for {taskClass}."
        };

    private static BuilderLaunchDefaultDecision BuildBuilderLaunchDefaultDecision(
        string runFolder,
        BuilderRequestIntake intake,
        BuilderExecutionPrep effectivePrep,
        BuilderDefaultRouteDecision? defaultRouteDecision,
        BuilderReadinessGate? readinessGate,
        BuilderConfirmedTaskClasses? confirmedTaskClasses,
        BuilderReadinessContradictions? contradictions,
        PreparedBuilderExecutionResult? existingResult,
        string? routeOverride,
        string? overrideReason)
    {
        var confirmedEntry = confirmedTaskClasses?.Entries.FirstOrDefault(entry =>
            string.Equals(entry.TaskClass, intake.NormalizedTaskClass, StringComparison.Ordinal) &&
            string.Equals(entry.ProofScope, intake.ProofScope, StringComparison.Ordinal) &&
            string.Equals(entry.TargetId, intake.TargetId, StringComparison.Ordinal));
        var defaultRoute = defaultRouteDecision?.ChosenDefaultRoute ?? effectivePrep.SelectedRoute;
        var overrideSelected = !string.IsNullOrWhiteSpace(routeOverride) &&
                               !string.Equals(routeOverride, defaultRoute, StringComparison.Ordinal);
        var normalizedOverrideReason = overrideSelected
            ? (string.IsNullOrWhiteSpace(overrideReason)
                ? BuildBuilderRouteOverrideReason(defaultRoute, effectivePrep.SelectedRoute, intake.NormalizedTaskClass)
                : overrideReason.Trim())
            : string.Empty;
        var routeSourceState = overrideSelected
            ? "overridden_by_operator"
            : defaultRouteDecision?.RouteSourceState ?? effectivePrep.RouteSourceState;
        var operatorDecisionState = overrideSelected
            ? "operator_override_selected"
            : string.Equals(routeSourceState, "defaulted_by_confirmed_policy", StringComparison.Ordinal)
                ? "accepted_default_route"
                : "accepted_suggested_route";
        var operatorOverrideState = overrideSelected
            ? "overridden_by_operator"
            : defaultRouteDecision?.OperatorOverrideState ?? effectivePrep.OperatorOverrideState;
        var repairLoopExpectedDefault =
            string.Equals(defaultRoute, "low_floor_with_repair_loop_route", StringComparison.Ordinal) ||
            string.Equals(confirmedEntry?.CurrentReadinessState, "confirmed_with_repair_loop", StringComparison.Ordinal);
        var currentReadinessState = confirmedEntry?.CurrentReadinessState
                                    ?? readinessGate?.CurrentReadinessGateState
                                    ?? "not_recorded";
        var defaultRouteSuspended = defaultRouteDecision?.DefaultRouteSuspended
                                    ?? confirmedEntry?.DefaultRouteSuspended
                                    ?? readinessGate?.DefaultRouteSuspended
                                    ?? false;
        var reconfirmationStatus = confirmedEntry?.ReconfirmationStatus
                                   ?? readinessGate?.ReconfirmationStatus
                                   ?? "not_required";
        var (launchEligibilityState, blockReason) = DetermineBuilderExecutionLaunchEligibility(
            intake,
            effectivePrep,
            existingResult,
            operatorDecisionState,
            currentReadinessState,
            defaultRouteSuspended,
            effectivePrep.CapabilityRoutingState,
            effectivePrep.CapabilityBlockReason);
        var linkedArtifactPaths = intake.LinkedEvidencePaths
            .Concat(effectivePrep.LinkedArtifactPaths)
            .Concat(defaultRouteDecision?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Concat(readinessGate?.LinkedArtifactPaths ?? Array.Empty<string>())
            .Concat(contradictions?.Entries.SelectMany(entry => entry.LinkedArtifactPaths) ?? Array.Empty<string>())
            .Concat(new[]
            {
                intake.ArtifactPath,
                effectivePrep.ArtifactPath,
                defaultRouteDecision?.ArtifactPath ?? string.Empty,
                readinessGate?.ArtifactPath ?? string.Empty,
                confirmedTaskClasses?.ArtifactPath ?? string.Empty,
                contradictions?.ArtifactPath ?? string.Empty
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{intake.NormalizedTaskClass} launch will use {effectivePrep.SelectedRoute} as {routeSourceState}. Readiness={currentReadinessState}. " +
                      (repairLoopExpectedDefault
                          ? "This default expects the repair loop to remain available. "
                          : "This default is treated as a clean bounded route. ") +
                      (overrideSelected
                          ? normalizedOverrideReason
                          : defaultRouteDecision?.ReasonSummary ?? effectivePrep.DefaultRouteReason);

        return new BuilderLaunchDefaultDecision(
            intake.SourceProofRunId,
            intake.RequestId,
            BuildBuilderExecutionPrepId(intake.RequestId),
            intake.NormalizedTaskClass,
            defaultRoute,
            effectivePrep.SelectedRoute,
            routeSourceState,
            operatorDecisionState,
            operatorOverrideState,
            normalizedOverrideReason,
            repairLoopExpectedDefault,
            currentReadinessState,
            defaultRouteSuspended,
            reconfirmationStatus,
            launchEligibilityState,
            blockReason,
            linkedArtifactPaths,
            summary,
            BuilderLaunchDefaultDecisionPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderTaskClassEvidenceSnapshot[] LoadBuilderTaskClassEvidenceSnapshots(
        string repoRoot,
        BuilderDefaultPolicyTaskClassEntry currentEntry)
        => LoadBuilderProofHistory(repoRoot).Entries
            .GroupBy(entry => new { entry.RunId, entry.RunFolder, entry.CompletedUtc })
            .OrderByDescending(group => group.Key.CompletedUtc)
            .ThenByDescending(group => group.Key.RunId, StringComparer.Ordinal)
            .Select(group =>
            {
                var proofRun = LoadBuilderProofRun(group.Key.RunFolder);
                var policy = LoadBuilderDefaultPolicy(group.Key.RunFolder);
                var policyEntry = policy?.TaskClassEntries.FirstOrDefault(entry =>
                    string.Equals(entry.ProofScope, currentEntry.ProofScope, StringComparison.Ordinal) &&
                    string.Equals(entry.TargetId, currentEntry.TargetId, StringComparison.Ordinal) &&
                    string.Equals(entry.TaskClass, currentEntry.TaskClass, StringComparison.Ordinal));
                if (proofRun is null || policyEntry is null)
                {
                    return null;
                }

                var intake = LoadBuilderRequestIntake(group.Key.RunFolder);
                var prep = LoadBuilderExecutionPrep(group.Key.RunFolder);
                var scopedIntake = intake is not null &&
                                   string.Equals(intake.ProofScope, currentEntry.ProofScope, StringComparison.Ordinal) &&
                                   string.Equals(intake.TargetId, currentEntry.TargetId, StringComparison.Ordinal) &&
                                   string.Equals(intake.NormalizedTaskClass, currentEntry.TaskClass, StringComparison.Ordinal)
                    ? intake
                    : null;
                var scopedPrep = scopedIntake is not null ? prep : null;

                return new BuilderTaskClassEvidenceSnapshot(
                    group.Key.RunId,
                    group.Key.RunFolder,
                    proofRun.CompletedUtc,
                    policyEntry.PolicyState,
                    MapBuilderPolicyStateToRoute(policyEntry.PolicyState),
                    proofRun.RunArtifactPath,
                    proofRun.CaseResults.FirstOrDefault(result =>
                        string.Equals(result.ProofScope, currentEntry.ProofScope, StringComparison.Ordinal) &&
                        string.Equals(result.TargetId, currentEntry.TargetId, StringComparison.Ordinal) &&
                        string.Equals(result.TaskClass, currentEntry.TaskClass, StringComparison.Ordinal)),
                    scopedIntake,
                    scopedPrep,
                    scopedPrep is null ? null : LoadBuilderExecutionLaunch(group.Key.RunFolder),
                    scopedPrep is null ? null : LoadBuilderExecutionResult(group.Key.RunFolder));
            })
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToArray();

    private static BuilderReadinessContradictionEvidence? BuildBuilderTaskClassContradictionEvidence(
        BuilderTaskClassEvidenceSnapshot snapshot,
        string currentRoute)
    {
        if (string.Equals(currentRoute, "split_first_low_floor_route", StringComparison.Ordinal))
        {
            if (snapshot.Result is null || !IsBuilderRouteContradiction(snapshot.Result))
            {
                return null;
            }

            var contradictoryRoute = ResolveBuilderActualRoute(currentRoute, snapshot.Launch, snapshot.Result);
            var attributionState = DetermineBuilderContradictionAttributionState(currentRoute, contradictoryRoute);
            return new BuilderReadinessContradictionEvidence(
                snapshot.RunId,
                snapshot.ObservedUtc,
                BuildBuilderRouteContradictionNote(
                    snapshot.RunId,
                    currentRoute,
                    contradictoryRoute,
                    attributionState,
                    snapshot.Result.FinalRouteOutcomeClassification,
                    snapshot.Result.PreparedRouteComparisonState),
                snapshot.Result.ArtifactPath,
                attributionState,
                contradictoryRoute,
                currentRoute);
        }

        if (snapshot.Result is not null && IsBuilderRouteContradiction(snapshot.Result))
        {
            var expectedRoute = currentRoute;
            var contradictoryRoute = ResolveBuilderActualRoute(currentRoute, snapshot.Launch, snapshot.Result);
            var attributionState = DetermineBuilderContradictionAttributionState(expectedRoute, contradictoryRoute);
            return new BuilderReadinessContradictionEvidence(
                snapshot.RunId,
                snapshot.ObservedUtc,
                BuildBuilderRouteContradictionNote(
                    snapshot.RunId,
                    expectedRoute,
                    contradictoryRoute,
                    attributionState,
                    snapshot.Result.FinalRouteOutcomeClassification,
                    snapshot.Result.PreparedRouteComparisonState),
                snapshot.Result.ArtifactPath,
                attributionState,
                contradictoryRoute,
                expectedRoute);
        }

        if (snapshot.CaseResult is null)
        {
            return null;
        }

        if (string.Equals(snapshot.CaseResult.FinalClassification, "failed_after_followup", StringComparison.Ordinal))
        {
            return new BuilderReadinessContradictionEvidence(
                snapshot.RunId,
                snapshot.ObservedUtc,
                $"{snapshot.RunId}: proof case for {snapshot.CaseResult.TargetLabel} ended as failed_after_followup.",
                snapshot.CaseResult.ValidationResultPath,
                "default_route_failure",
                currentRoute,
                currentRoute);
        }

        if (string.Equals(currentRoute, "direct_low_floor_route", StringComparison.Ordinal) &&
            string.Equals(snapshot.CaseResult.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal))
        {
            return new BuilderReadinessContradictionEvidence(
                snapshot.RunId,
                snapshot.ObservedUtc,
                $"{snapshot.RunId}: repair burden rose above the direct low-floor expectation.",
                snapshot.CaseResult.RecoveryValidationResultPath,
                "default_route_failure",
                currentRoute,
                currentRoute);
        }

        return null;
    }

    private static bool IsBuilderProofConfirmation(BuilderProofCaseResult? result)
        => result is not null &&
           (string.Equals(result.FinalClassification, "passed_cleanly", StringComparison.Ordinal) ||
            string.Equals(result.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal));

    private static string DetermineBuilderTaskClassReadinessState(
        string policyState,
        int supportingProofRunCount,
        int confirmationCount,
        int contradictionCount,
        string contradictionAttributionState,
        bool repairLoopExpected,
        int requiredPreparedLaunchConfirmations,
        int freshProofRunCount,
        int freshConfirmationCount,
        int freshPreparedLaunchConfirmations)
    {
        var requiredConfirmationCount = requiredPreparedLaunchConfirmations > 0
            ? requiredPreparedLaunchConfirmations
            : BuilderReadinessRequiredProofRuns;
        if (contradictionCount > 0)
        {
            if (freshProofRunCount >= (string.Equals(contradictionAttributionState, "override_route_failure", StringComparison.Ordinal)
                    ? BuilderOverrideRecoveryRequiredProofRuns
                    : BuilderReadinessRequiredProofRuns) &&
                freshConfirmationCount >= requiredConfirmationCount &&
                (requiredPreparedLaunchConfirmations == 0 || freshPreparedLaunchConfirmations >= requiredPreparedLaunchConfirmations))
            {
                return repairLoopExpected
                    ? "confirmed_with_repair_loop"
                    : "confirmed_for_bounded_use";
            }

            if (!string.Equals(contradictionAttributionState, "override_route_failure", StringComparison.Ordinal) &&
                (contradictionCount >= 2 || confirmationCount == 0 || string.Equals(policyState, "stronger_tier_required", StringComparison.Ordinal)))
            {
                return "contradicted";
            }

            return "unstable_needs_more_evidence";
        }

        if (supportingProofRunCount >= BuilderReadinessRequiredProofRuns &&
            confirmationCount >= requiredConfirmationCount)
        {
            return repairLoopExpected
                ? "confirmed_with_repair_loop"
                : "confirmed_for_bounded_use";
        }

        return supportingProofRunCount == 0 ? "not_yet_proven" : "provisional";
    }

    private static string DetermineBuilderTaskClassSummaryClassification(
        string policyState,
        int supportingProofRunCount,
        string currentReadinessState)
        => currentReadinessState switch
        {
            "confirmed_for_bounded_use" => "confirmed_for_bounded_use",
            "confirmed_with_repair_loop" => "confirmed_with_repair_loop",
            "contradicted" => "contradicted",
            "not_yet_proven" => "not_yet_proven",
            _ when supportingProofRunCount == 0 ||
                   string.Equals(policyState, "stronger_tier_required", StringComparison.Ordinal) => "not_yet_proven",
            _ => "provisional"
        };

    private static string BuildBuilderTaskClassReadinessRecommendation(
        string currentReadinessState,
        string currentRoute,
        string taskClass,
        bool defaultRouteSuspended)
        => currentReadinessState switch
        {
            "confirmed_for_bounded_use" => $"{currentRoute} is operationally confirmed for bounded {taskClass} work.",
            "confirmed_with_repair_loop" => $"{currentRoute} is confirmed for bounded {taskClass} work while keeping the repair loop available.",
            "contradicted" => $"{currentRoute} is contradicted for bounded {taskClass} work and should not be treated as the current default.",
            "unstable_needs_more_evidence" when defaultRouteSuspended => $"{currentRoute} is suspended until fresh corroborating evidence is recorded for {taskClass}.",
            "unstable_needs_more_evidence" => $"{currentRoute} needs fresh corroborating evidence before it can resume bounded use for {taskClass}.",
            "not_yet_proven" => $"{currentRoute} has not yet earned bounded-use confirmation for {taskClass}.",
            _ => $"{currentRoute} remains provisional for bounded {taskClass} work."
        };

    private static string BuildBuilderTaskClassReconfirmationStatus(
        bool reconfirmationRequired,
        string currentReadinessState,
        int freshProofRunCount,
        int freshPreparedLaunchConfirmations,
        int requiredFreshProofRunCount,
        int requiredPreparedLaunchConfirmations)
    {
        if (!reconfirmationRequired)
        {
            return "not_required";
        }

        if (string.Equals(currentReadinessState, "confirmed_for_bounded_use", StringComparison.Ordinal) ||
            string.Equals(currentReadinessState, "confirmed_with_repair_loop", StringComparison.Ordinal))
        {
            return "reconfirmed_after_contradiction";
        }

        if (freshProofRunCount >= requiredFreshProofRunCount ||
            freshPreparedLaunchConfirmations > 0)
        {
            return "collecting_fresh_evidence";
        }

        return requiredPreparedLaunchConfirmations > 0
            ? "waiting_for_fresh_launch_confirmation"
            : "waiting_for_fresh_proof";
    }

    private static string BuildBuilderRequestDefaultRouteReason(
        BuilderRequestPolicyDecision requestDecision,
        BuilderConfirmedTaskClassEntry? confirmedEntry,
        string routeSourceState,
        bool defaultSuspended)
    {
        if (confirmedEntry is null)
        {
            return $"{requestDecision.ChosenPolicyState} is still suggested because no class-specific bounded-use confirmation has been recorded yet.";
        }

        if (defaultSuspended)
        {
            return string.Equals(confirmedEntry.ContradictionAttributionState, "override_route_failure", StringComparison.Ordinal)
                ? $"{confirmedEntry.CurrentRoute} is temporarily suspended because an override route contradicted the default and reconfirmation status is {confirmedEntry.ReconfirmationStatus}."
                : $"{confirmedEntry.CurrentRoute} is temporarily suspended because contradiction evidence is active and reconfirmation status is {confirmedEntry.ReconfirmationStatus}.";
        }

        if (string.Equals(routeSourceState, "defaulted_by_confirmed_policy", StringComparison.Ordinal))
        {
            return $"{confirmedEntry.CurrentRoute} is defaulted by confirmed evidence with {confirmedEntry.ConfirmationCount} confirmation(s) for {confirmedEntry.TaskClass}.";
        }

        return $"{confirmedEntry.CurrentRoute} is still suggested while the class remains {confirmedEntry.CurrentReadinessState}.";
    }

    private static string ResolveBuilderContradictoryRoute(BuilderConfirmedTaskClassEntry entry)
    {
        if (string.Equals(entry.ContradictionAttributionState, "override_route_failure", StringComparison.Ordinal))
        {
            return entry.CurrentRoute == "split_first_low_floor_route"
                ? "direct_low_floor_route"
                : "override_route";
        }

        return entry.CurrentRoute;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string MapBuilderPolicyStateToRoute(string policyState)
        => policyState switch
        {
            "direct_low_floor" => "direct_low_floor_route",
            "split_first_low_floor" => "split_first_low_floor_route",
            "low_floor_with_repair_loop_expected" => "low_floor_with_repair_loop_route",
            "stronger_tier_optional" => "current_model_with_optional_stronger_tier_route",
            "stronger_tier_recommended" => "stronger_tier_recommended_route",
            "stronger_tier_required" => "task_out_of_scope_route",
            _ => "task_out_of_scope_route"
        };

    private sealed record BuilderTaskClassEvidenceSnapshot(
        string RunId,
        string RunFolder,
        DateTimeOffset ObservedUtc,
        string PolicyState,
        string CurrentRoute,
        string ProofRunArtifactPath,
        BuilderProofCaseResult? CaseResult,
        BuilderRequestIntake? Intake,
        BuilderExecutionPrep? Prep,
        PreparedBuilderExecutionLaunch? Launch,
        PreparedBuilderExecutionResult? Result);

    private static string BuildBuilderReconfirmationStatus(
        bool reconfirmationRequired,
        string readinessGateState,
        int freshSupportingProofRunCount,
        int freshPreparedLaunchConfirmations,
        int requiredFreshProofRunCount,
        int requiredPreparedLaunchConfirmations)
    {
        if (!reconfirmationRequired)
        {
            return "not_required";
        }

        if (string.Equals(readinessGateState, "confirmed_for_bounded_use", StringComparison.Ordinal) ||
            string.Equals(readinessGateState, "confirmed_with_repair_loop", StringComparison.Ordinal))
        {
            return "reconfirmed_after_contradiction";
        }

        if (freshSupportingProofRunCount >= requiredFreshProofRunCount ||
            freshPreparedLaunchConfirmations > 0)
        {
            return "collecting_fresh_evidence";
        }

        if (requiredPreparedLaunchConfirmations > 0)
        {
            return "waiting_for_fresh_launch_confirmation";
        }

        return "waiting_for_fresh_proof";
    }

    private static string BuildBuilderReconfirmationNarration(
        string reconfirmationStatus,
        int freshSupportingProofRunCount,
        int freshPreparedLaunchConfirmations)
        => reconfirmationStatus switch
        {
            "reconfirmed_after_contradiction" => $"Fresh corroboration recovered with {freshSupportingProofRunCount} proof run(s) and {freshPreparedLaunchConfirmations} launch confirmation(s).",
            "collecting_fresh_evidence" => $"Fresh corroboration is still building: proof runs={freshSupportingProofRunCount}, launch confirmations={freshPreparedLaunchConfirmations}.",
            "waiting_for_fresh_launch_confirmation" => "Fresh prepared-launch confirmation is required before the route can be treated as current again.",
            "waiting_for_fresh_proof" => "Fresh corroborating proof is required before the route can be treated as current again.",
            _ => string.Empty
        };

    private sealed record BuilderReadinessContradictionEvidence(
        string RunId,
        DateTimeOffset ObservedUtc,
        string Note,
        string ArtifactPath,
        string ContradictionAttributionState,
        string ContradictoryRoute,
        string ExpectedRoute);

    private sealed record BuilderReconfirmationThreshold(
        string ContradictionAttributionState,
        int RequiredFreshProofRunCount,
        int RequiredFreshPreparedLaunchConfirmations);

    private sealed record BuilderReadinessEvidenceSnapshot(
        string RunId,
        string RunFolder,
        DateTimeOffset ObservedUtc,
        BuilderRequestIntake Intake,
        BuilderExecutionPrep Prep,
        PreparedBuilderExecutionLaunch? Launch,
        PreparedBuilderExecutionResult? Result);

    private static BuilderDefaultPolicy BuildBuilderDefaultPolicy(
        string runFolder,
        BuilderProofRun proofRun,
        BuilderModelTrustBands trustBands,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan,
        BuilderComparativeProofRun? comparativeRun,
        BuilderRoutingPolicyEvidence? routingPolicyEvidence,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome)
    {
        var entries = trustBands.Entries
            .Select(entry =>
            {
                var state = DetermineBuilderDefaultPolicyState(
                    entry,
                    routingRecommendation,
                    escalationDecision,
                    routingPlan,
                    comparativeRun,
                    routingPolicyEvidence,
                    splitPlan,
                    tieredRoutingPolicy,
                    splitOutcome);
                var weakSpot = ResolveBuilderDefaultPolicyWeakSpot(entry.TargetId, trustBands, escalationDecision);
                var reasons = BuildBuilderDefaultPolicyReasons(
                    entry,
                    state,
                    routingRecommendation,
                    escalationDecision,
                    routingPlan,
                    comparativeRun,
                    tieredRoutingPolicy,
                    splitOutcome,
                    weakSpot);
                var linkedEvidencePaths = BuildBuilderDefaultPolicyEvidencePaths(
                    runFolder,
                    entry,
                    routingRecommendation,
                    escalationDecision,
                    routingPlan,
                    comparativeRun,
                    routingPolicyEvidence,
                    splitPlan,
                    tieredRoutingPolicy,
                    splitOutcome);
                var summary = BuildBuilderDefaultPolicyEntrySummary(entry, state, weakSpot, reasons);
                return new BuilderDefaultPolicyTaskClassEntry(
                    entry.ProofScope,
                    entry.TargetId,
                    entry.TargetLabel,
                    entry.TaskClass,
                    entry.ComplexityDimensions,
                    state,
                    weakSpot,
                    summary,
                    reasons,
                    linkedEvidencePaths);
            })
            .OrderBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .ToArray();

        var inBandTaskClasses = CollectBuilderDefaultPolicyTaskClasses(entries, "direct_low_floor");
        var splitFirstTaskClasses = CollectBuilderDefaultPolicyTaskClasses(entries, "split_first_low_floor");
        var repairLoopTaskClasses = CollectBuilderDefaultPolicyTaskClasses(entries, "low_floor_with_repair_loop_expected");
        var strongerTierOptionalTaskClasses = CollectBuilderDefaultPolicyTaskClasses(entries, "stronger_tier_optional");
        var strongerTierRecommendedTaskClasses = CollectBuilderDefaultPolicyTaskClasses(entries, "stronger_tier_recommended");
        var strongerTierRequiredTaskClasses = CollectBuilderDefaultPolicyTaskClasses(entries, "stronger_tier_required");
        var linkedEvidencePaths = entries
            .SelectMany(entry => entry.LinkedEvidencePaths)
            .Concat(new[]
            {
                BuilderProofRunArtifactPath(runFolder),
                BuilderModelTrustBandsPath(runFolder),
                BuilderModelRoutingRecommendationPath(runFolder),
                BuilderModelEscalationDecisionPath(runFolder),
                BuilderModelRoutingPlanPath(runFolder),
                BuilderComparativeProofRunPath(runFolder),
                BuilderRoutingPolicyEvidencePath(runFolder),
                BuilderSplitFirstPlanPath(runFolder),
                BuilderTieredRoutingPolicyPath(runFolder),
                BuilderSplitFirstOutcomePath(runFolder)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = BuildBuilderDefaultPolicySummary(
            proofRun.ModelId,
            inBandTaskClasses,
            splitFirstTaskClasses,
            repairLoopTaskClasses,
            strongerTierOptionalTaskClasses,
            strongerTierRecommendedTaskClasses,
            strongerTierRequiredTaskClasses,
            splitOutcome);

        return new BuilderDefaultPolicy(
            proofRun.ProofRunId,
            proofRun.ModelId,
            inBandTaskClasses,
            splitFirstTaskClasses,
            repairLoopTaskClasses,
            strongerTierOptionalTaskClasses,
            strongerTierRecommendedTaskClasses,
            strongerTierRequiredTaskClasses,
            entries,
            linkedEvidencePaths,
            summary,
            BuilderDefaultPolicyPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderDefaultPolicyHistory BuildBuilderDefaultPolicyHistory(
        BuilderDefaultPolicyHistory priorHistory,
        BuilderDefaultPolicy current)
    {
        var retentionCount = Math.Max(priorHistory.RetentionCount, 20);
        var currentEntry = new BuilderDefaultPolicyHistoryEntry(
            current.SourceProofRunId,
            current.CurrentModelId,
            current.Summary,
            current.ArtifactPath,
            current.ObservedUtc,
            current.TaskClassEntries);
        var entries = priorHistory.Entries
            .Where(entry => !string.Equals(entry.SourceProofRunId, current.SourceProofRunId, StringComparison.Ordinal))
            .Concat(new[] { currentEntry })
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SourceProofRunId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();

        return new BuilderDefaultPolicyHistory(retentionCount, entries);
    }

    private static BuilderRequestPolicyDecision? BuildBuilderRequestPolicyDecision(
        string runFolder,
        BuilderDefaultPolicy policy,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome)
    {
        var proofScope = !string.IsNullOrWhiteSpace(tieredRoutingPolicy?.ProofScope)
            ? tieredRoutingPolicy.ProofScope
            : routingRecommendation.FeaturedProofScope;
        var targetId = !string.IsNullOrWhiteSpace(tieredRoutingPolicy?.TargetId)
            ? tieredRoutingPolicy.TargetId
            : routingRecommendation.FeaturedTargetId;
        var taskClass = !string.IsNullOrWhiteSpace(tieredRoutingPolicy?.TaskClass)
            ? tieredRoutingPolicy.TaskClass
            : routingRecommendation.FeaturedTaskClass;
        if (string.IsNullOrWhiteSpace(taskClass))
        {
            return null;
        }

        var entry = policy.TaskClassEntries.FirstOrDefault(value =>
            string.Equals(value.ProofScope, proofScope, StringComparison.Ordinal) &&
            (string.Equals(value.TargetId, targetId, StringComparison.Ordinal) ||
             string.Equals(value.TaskClass, taskClass, StringComparison.Ordinal)));
        if (entry is null)
        {
            return null;
        }

        var splitFirstIsDefault = string.Equals(entry.PolicyState, "split_first_low_floor", StringComparison.Ordinal);
        var strongerTierDisposition = DetermineBuilderRequestStrongerTierDisposition(entry.PolicyState, tieredRoutingPolicy, splitOutcome);
        var knownWeakSpotLikelihood = string.IsNullOrWhiteSpace(entry.PrimaryWeakSpot)
            ? "No linked weak-spot likelihood recorded."
            : $"{BuildWeakSpotLabel(entry.PrimaryWeakSpot)} remains the primary weak-spot risk for this bounded request.";
        var reasons = entry.Reasons
            .Concat(new[]
            {
                BuildBuilderRequestComplexitySummary(entry.ComplexityDimensions),
                splitFirstIsDefault
                    ? "Split-first is the default route for this bounded request."
                    : "Split-first is not the default route for this bounded request.",
                BuildBuilderRequestStrongerTierSummary(strongerTierDisposition, routingPlan, splitOutcome)
            })
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var linkedEvidencePaths = entry.LinkedEvidencePaths
            .Concat(new[]
            {
                policy.ArtifactPath,
                BuilderModelRoutingRecommendationPath(runFolder),
                BuilderModelEscalationDecisionPath(runFolder),
                BuilderModelRoutingPlanPath(runFolder),
                BuilderTieredRoutingPolicyPath(runFolder),
                BuilderSplitFirstOutcomePath(runFolder)
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{entry.TargetLabel} ({entry.TaskClass}) is classified as {entry.PolicyState}. {BuildBuilderRequestComplexitySummary(entry.ComplexityDimensions)} {entry.Summary} {BuildBuilderRequestStrongerTierSummary(strongerTierDisposition, routingPlan, splitOutcome)}";

        return new BuilderRequestPolicyDecision(
            policy.SourceProofRunId,
            tieredRoutingPolicy is null ? "featured_builder_routing_target" : "featured_builder_comparative_target",
            policy.CurrentModelId,
            entry.ProofScope,
            entry.TargetId,
            entry.TargetLabel,
            entry.TaskClass,
            entry.ComplexityDimensions,
            entry.PolicyState,
            strongerTierDisposition,
            splitFirstIsDefault,
            knownWeakSpotLikelihood,
            reasons,
            linkedEvidencePaths,
            summary,
            BuilderRequestPolicyDecisionPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderPolicyStability BuildBuilderPolicyStability(
        BuilderDefaultPolicyHistory history,
        BuilderRequestPolicyDecision requestDecision)
    {
        var matchingEntries = history.Entries
            .SelectMany(entry => entry.TaskClassEntries.Select(taskEntry => new
            {
                entry.SourceProofRunId,
                entry.ArtifactPath,
                entry.ObservedUtc,
                TaskEntry = taskEntry
            }))
            .Where(value =>
                string.Equals(value.TaskEntry.ProofScope, requestDecision.ProofScope, StringComparison.Ordinal) &&
                string.Equals(value.TaskEntry.TaskClass, requestDecision.TaskClass, StringComparison.Ordinal))
            .OrderByDescending(value => value.ObservedUtc)
            .ThenByDescending(value => value.SourceProofRunId, StringComparer.Ordinal)
            .ToArray();

        var supporting = matchingEntries
            .Where(value => string.Equals(value.TaskEntry.PolicyState, requestDecision.ChosenPolicyState, StringComparison.Ordinal))
            .ToArray();
        var contradictions = matchingEntries
            .Where(value => !string.Equals(value.TaskEntry.PolicyState, requestDecision.ChosenPolicyState, StringComparison.Ordinal))
            .ToArray();
        var supportLevel = supporting.Length >= 3 && contradictions.Length == 0
            ? "stable"
            : supporting.Length >= 2 && contradictions.Length == 0
                ? "corroborated"
                : "provisional";
        var corroboratingArtifacts = supporting
            .Select(value => value.ArtifactPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var summary = $"{requestDecision.TaskClass} is {supportLevel} for {requestDecision.ChosenPolicyState} across {supporting.Length} supporting proof run(s) with {contradictions.Length} contradiction(s).";

        return new BuilderPolicyStability(
            requestDecision.SourceProofRunId,
            requestDecision.CurrentModelId,
            requestDecision.TaskClass,
            requestDecision.ChosenPolicyState,
            supportLevel,
            supporting.Length,
            contradictions.Length,
            corroboratingArtifacts,
            summary,
            BuilderPolicyStabilityPath(Path.GetDirectoryName(requestDecision.ArtifactPath) ?? string.Empty),
            DateTimeOffset.UtcNow);
    }

    private static BuilderCapabilityBlockDecision BuildBuilderCapabilityBlockDecision(
        string runFolder,
        BuilderRequestPolicyDecision requestDecision,
        BuilderDefaultRouteDecision defaultRouteDecision,
        BuilderToolchainCapabilityRegistry capabilityRegistry,
        BuilderLanguageEligibility languageEligibility)
    {
        var requestedStackId = ResolveBuilderRequestedStackId(requestDecision);
        var effectiveStackId = ResolveBuilderEffectiveStackId(requestDecision, requestedStackId, capabilityRegistry.PreferredStackId);
        var requestedStackLabel = ResolveBuilderStackLabel(requestedStackId);
        var effectiveStackLabel = ResolveBuilderStackLabel(effectiveStackId);
        var eligibilityEntry = languageEligibility.Entries.FirstOrDefault(entry =>
            string.Equals(entry.StackId, effectiveStackId, StringComparison.Ordinal));
        var eligibilityState = eligibilityEntry?.EligibilityState ?? "unsupported_for_repo";
        var routingDecisionState = DetermineBuilderCapabilityRoutingState(
            requestedStackId,
            effectiveStackId,
            eligibilityState);
        var blockReason = routingDecisionState switch
        {
            "route_blocked_missing_toolchain" => eligibilityEntry?.BlockedReason ?? $"Required toolchain is unavailable for {effectiveStackLabel}.",
            "route_blocked_repo_policy" => eligibilityEntry?.BlockedReason ?? $"{effectiveStackLabel} is blocked by repo policy.",
            _ => string.Empty
        };
        var summary = routingDecisionState switch
        {
            "route_redirected_to_preferred_stack" => $"Builder request implied {requestedStackLabel} and was redirected to preferred stack {effectiveStackLabel}.",
            "route_allowed_but_not_preferred" => $"Builder request implied {effectiveStackLabel} and is allowed, but not preferred for this repo.",
            "route_blocked_missing_toolchain" => $"Builder request implied {effectiveStackLabel} and is route blocked because the required toolchain is unavailable.",
            "route_blocked_repo_policy" => $"Builder request implied {effectiveStackLabel} and is route blocked by repo policy.",
            _ => $"Builder request implied {effectiveStackLabel} and is allowed for this repo."
        };
        var linkedArtifactPaths = new[]
        {
            capabilityRegistry.ArtifactPath,
            languageEligibility.ArtifactPath
        }
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new BuilderCapabilityBlockDecision(
            requestDecision.SourceProofRunId,
            requestDecision.TaskClass,
            requestDecision.ProofScope,
            requestedStackId,
            requestedStackLabel,
            effectiveStackId,
            effectiveStackLabel,
            eligibilityEntry?.ToolchainRequirementSummary ?? BuildBuilderToolchainRequirementSummary(effectiveStackId),
            eligibilityState switch
            {
                "ready_and_preferred" => "preferred_and_ready",
                "ready_but_not_preferred" => "approved_but_not_preferred",
                "unavailable" => "missing_required_toolchain",
                "installed_but_disallowed" => "callable_but_repo_blocked",
                _ => "unsupported_for_repo"
            },
            eligibilityState,
            routingDecisionState,
            routingDecisionState is "route_allowed" or "route_allowed_but_not_preferred" or "route_redirected_to_preferred_stack"
                ? defaultRouteDecision.ChosenDefaultRoute
                : string.Empty,
            routingDecisionState is "route_allowed" or "route_allowed_but_not_preferred" or "route_redirected_to_preferred_stack"
                ? effectiveStackId
                : string.Empty,
            blockReason,
            linkedArtifactPaths,
            summary,
            ShouldPersistBuilderCapabilityBlockDecision(routingDecisionState) ? BuilderCapabilityBlockDecisionPath(runFolder) : string.Empty,
            DateTimeOffset.UtcNow);
    }

    private static bool ShouldPersistBuilderCapabilityBlockDecision(BuilderCapabilityBlockDecision decision)
        => ShouldPersistBuilderCapabilityBlockDecision(decision.RoutingDecisionState);

    private static bool ShouldPersistBuilderCapabilityBlockDecision(string routingDecisionState)
        => !string.Equals(routingDecisionState, "route_allowed", StringComparison.Ordinal);

    private static string ResolveBuilderRequestedStackId(BuilderRequestPolicyDecision requestDecision)
    {
        var composite = $"{requestDecision.ProofScope}|{requestDecision.TaskClass}|{requestDecision.TargetId}".ToLowerInvariant();
        if (composite.Contains("wpf", StringComparison.Ordinal))
        {
            return "csharp_dotnet";
        }

        if (composite.Contains("javascript", StringComparison.Ordinal) ||
            composite.Contains("typescript", StringComparison.Ordinal) ||
            composite.Contains("node", StringComparison.Ordinal))
        {
            return "javascript_typescript";
        }

        if (composite.Contains("python", StringComparison.Ordinal))
        {
            return "python";
        }

        if (composite.Contains("java", StringComparison.Ordinal))
        {
            return "java";
        }

        if (composite.Contains("c++", StringComparison.Ordinal) ||
            composite.Contains("cpp", StringComparison.Ordinal) ||
            composite.Contains("native", StringComparison.Ordinal) ||
            composite.Contains("cmake", StringComparison.Ordinal))
        {
            return "cpp_native";
        }

        return "csharp_dotnet";
    }

    private static string ResolveBuilderEffectiveStackId(
        BuilderRequestPolicyDecision requestDecision,
        string requestedStackId,
        string preferredStackId)
        => string.Equals(requestDecision.ProofScope, "wpf_app", StringComparison.Ordinal) &&
           string.Equals(requestedStackId, "csharp_dotnet", StringComparison.Ordinal)
            ? "wpf_desktop_dotnet"
            : requestedStackId;

    private static string ResolveBuilderStackLabel(string stackId)
        => GetBuilderLanguageStackDefinitions()
            .FirstOrDefault(entry => string.Equals(entry.StackId, stackId, StringComparison.Ordinal))
            ?.StackLabel ?? stackId;

    private static string BuildBuilderToolchainRequirementSummary(string stackId)
        => GetBuilderLanguageStackDefinitions()
            .FirstOrDefault(entry => string.Equals(entry.StackId, stackId, StringComparison.Ordinal))
            ?.ToolchainRequirementSummary ?? "No toolchain requirement recorded.";

    private static string DetermineBuilderCapabilityRoutingState(
        string requestedStackId,
        string effectiveStackId,
        string eligibilityState)
        => eligibilityState switch
        {
            "unavailable" => "route_blocked_missing_toolchain",
            "installed_but_disallowed" or "unsupported_for_repo" => "route_blocked_repo_policy",
            "ready_and_preferred" when !string.Equals(requestedStackId, effectiveStackId, StringComparison.Ordinal) => "route_redirected_to_preferred_stack",
            "ready_but_not_preferred" => "route_allowed_but_not_preferred",
            _ => "route_allowed"
        };

    private static BuilderRequestIntake BuildBuilderRequestIntake(
        string runFolder,
        BuilderRequestPolicyDecision requestDecision,
        BuilderPolicyStability stability,
        BuilderModelRoutingPlan routingPlan,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome,
        BuilderDefaultRouteDecision defaultRouteDecision,
        BuilderCapabilityBlockDecision capabilityDecision)
    {
        var requestId = $"{requestDecision.SourceProofRunId}-{requestDecision.TargetId}-intake";
        var intakeState = DetermineBuilderRequestIntakeState(requestDecision);
        var normalizationState = DetermineBuilderRequestNormalizationState(requestDecision);
        var strongerTierRole = BuildBuilderRequestStrongerTierSummary(requestDecision.StrongerTierDisposition, routingPlan, splitOutcome);
        var linkedEvidencePaths = requestDecision.LinkedEvidencePaths
            .Concat(new[]
            {
                requestDecision.ArtifactPath,
                stability.ArtifactPath,
                defaultRouteDecision.ArtifactPath,
                capabilityDecision.ArtifactPath,
                splitPlan?.ArtifactPath ?? string.Empty,
                tieredRoutingPolicy?.ArtifactPath ?? string.Empty,
                splitOutcome?.ArtifactPath ?? string.Empty
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{requestDecision.TargetLabel} is {intakeState}. {normalizationState} Route source={defaultRouteDecision.RouteSourceState}. Support={stability.SupportLevel}. {strongerTierRole} {capabilityDecision.Summary}";

        return new BuilderRequestIntake(
            requestId,
            requestDecision.SourceProofRunId,
            requestDecision.ArtifactPath,
            requestDecision.DecisionSource,
            requestDecision.CurrentModelId,
            requestDecision.ProofScope,
            requestDecision.TargetId,
            requestDecision.TargetLabel,
            requestDecision.TaskClass,
            requestDecision.ComplexityDimensions,
            requestDecision.ChosenPolicyState,
            requestDecision.StrongerTierDisposition,
            stability.SupportLevel,
            normalizationState,
            intakeState,
            requestDecision.KnownWeakSpotLikelihood,
            requestDecision.Reasons,
            linkedEvidencePaths,
            "current",
            summary,
            BuilderRequestIntakePath(runFolder),
            DateTimeOffset.UtcNow,
            defaultRouteDecision.RouteSourceState,
            defaultRouteDecision.OperatorOverrideState,
            defaultRouteDecision.ReasonSummary,
            capabilityDecision.RequestedStackId,
            capabilityDecision.EffectiveStackId,
            capabilityDecision.EffectiveStackLabel,
            capabilityDecision.EligibilityState,
            capabilityDecision.RoutingDecisionState,
            capabilityDecision.BlockReason,
            capabilityDecision.Summary,
            capabilityDecision.ArtifactPath);
    }

    private static BuilderRequestIntakeHistory BuildBuilderRequestIntakeHistory(
        BuilderRequestIntakeHistory priorHistory,
        BuilderRequestIntake current)
    {
        var retentionCount = Math.Max(priorHistory.RetentionCount, 20);
        var currentEntry = new BuilderRequestIntakeHistoryEntry(
            current.RequestId,
            current.SourceProofRunId,
            current.CurrentModelId,
            current.NormalizedTaskClass,
            current.IntakeClassificationState,
            current.FreshnessState,
            current.ArtifactPath,
            current.Summary,
            current.ObservedUtc);
        var entries = priorHistory.Entries
            .Where(entry => !string.Equals(entry.RequestId, current.RequestId, StringComparison.Ordinal))
            .Select(entry => string.Equals(entry.SourceProofRunId, current.SourceProofRunId, StringComparison.Ordinal)
                ? entry with { FreshnessState = "stale" }
                : entry with { FreshnessState = "stale" })
            .Concat(new[] { currentEntry })
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.RequestId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();

        return new BuilderRequestIntakeHistory(retentionCount, entries);
    }

    private static BuilderExecutionPrep BuildBuilderExecutionPrep(
        string runFolder,
        BuilderRequestIntake intake,
        BuilderRequestPolicyDecision requestDecision,
        BuilderPolicyStability stability,
        BuilderModelRoutingPlan routingPlan,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome,
        BuilderDefaultRouteDecision defaultRouteDecision,
        BuilderCapabilityBlockDecision capabilityDecision)
    {
        var selectedRoute = DetermineBuilderExecutionPrepRoute(intake);
        var splitPlanRequired = string.Equals(intake.IntakeClassificationState, "ready_for_split_first_low_floor", StringComparison.Ordinal);
        var rerunRepairExpectationLevel = DetermineBuilderExecutionPrepExpectation(intake.IntakeClassificationState, requestDecision, splitOutcome);
        var requiredEvidencePaths = BuildBuilderExecutionPrepRequiredEvidencePaths(
            requestDecision,
            splitPlan,
            tieredRoutingPolicy,
            splitOutcome);
        var nextActions = BuildBuilderExecutionPrepNextActions(
            intake,
            requestDecision,
            routingPlan,
            splitPlan,
            tieredRoutingPolicy,
            splitOutcome);
        var futureExecutionHookPaths = splitPlan?.Steps
            .Select(step => step.ExecutionHook.FutureExecutionArtifactPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        var linkedArtifactPaths = requiredEvidencePaths
            .Concat(futureExecutionHookPaths)
            .Concat(new[]
            {
                intake.ArtifactPath,
                requestDecision.ArtifactPath,
                stability.ArtifactPath,
                defaultRouteDecision.ArtifactPath,
                capabilityDecision.ArtifactPath,
                splitPlan?.ArtifactPath ?? string.Empty,
                tieredRoutingPolicy?.ArtifactPath ?? string.Empty,
                splitOutcome?.ArtifactPath ?? string.Empty
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = BuildBuilderExecutionPrepSummary(intake, selectedRoute, rerunRepairExpectationLevel, routingPlan, splitPlanRequired, defaultRouteDecision, capabilityDecision);

        return new BuilderExecutionPrep(
            intake.RequestId,
            intake.SourceProofRunId,
            intake.CurrentModelId,
            intake.ProofScope,
            intake.TargetId,
            intake.TargetLabel,
            intake.NormalizedTaskClass,
            intake.IntakeClassificationState,
            selectedRoute,
            BuildBuilderRequestStrongerTierSummary(intake.StrongerTierDisposition, routingPlan, splitOutcome),
            intake.SupportLevel,
            rerunRepairExpectationLevel,
            splitPlanRequired,
            splitPlan?.ArtifactPath ?? string.Empty,
            tieredRoutingPolicy?.ArtifactPath ?? string.Empty,
            requestDecision.KnownWeakSpotLikelihood,
            requiredEvidencePaths,
            nextActions,
            futureExecutionHookPaths,
            linkedArtifactPaths,
            "current",
            summary,
            BuilderExecutionPrepPath(runFolder),
            DateTimeOffset.UtcNow,
            defaultRouteDecision.RouteSourceState,
            defaultRouteDecision.OperatorOverrideState,
            defaultRouteDecision.ReasonSummary,
            capabilityDecision.RequestedStackId,
            capabilityDecision.EffectiveStackId,
            capabilityDecision.EffectiveStackLabel,
            capabilityDecision.EligibilityState,
            capabilityDecision.RoutingDecisionState,
            capabilityDecision.BlockReason,
            capabilityDecision.Summary,
            capabilityDecision.ArtifactPath);
    }

    private static BuilderExecutionPrepHistory BuildBuilderExecutionPrepHistory(
        BuilderExecutionPrepHistory priorHistory,
        BuilderExecutionPrep current)
    {
        var retentionCount = Math.Max(priorHistory.RetentionCount, 20);
        var currentEntry = new BuilderExecutionPrepHistoryEntry(
            current.SourceIntakeId,
            current.SourceProofRunId,
            current.NormalizedTaskClass,
            current.SelectedRoute,
            current.FreshnessState,
            current.ArtifactPath,
            current.Summary,
            current.ObservedUtc);
        var entries = priorHistory.Entries
            .Where(entry => !string.Equals(entry.SourceIntakeId, current.SourceIntakeId, StringComparison.Ordinal))
            .Select(entry => entry with { FreshnessState = "stale" })
            .Concat(new[] { currentEntry })
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenByDescending(entry => entry.SourceIntakeId, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();

        return new BuilderExecutionPrepHistory(retentionCount, entries);
    }

    private static void RewriteStaleBuilderRequestIntakes(string currentRunFolder, BuilderRequestIntakeHistory history)
    {
        foreach (var entry in history.Entries.Where(entry =>
                     string.Equals(entry.FreshnessState, "stale", StringComparison.Ordinal) &&
                     File.Exists(entry.ArtifactPath)))
        {
            var intake = LoadBuilderRequestIntake(Path.GetDirectoryName(entry.ArtifactPath) ?? string.Empty);
            if (intake is null || string.Equals(intake.FreshnessState, "stale", StringComparison.Ordinal))
            {
                continue;
            }

            var updated = intake with
            {
                FreshnessState = "stale",
                Summary = $"Stale intake. {intake.Summary}"
            };
            File.WriteAllText(entry.ArtifactPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void RewriteStaleBuilderExecutionPrep(string currentRunFolder, BuilderExecutionPrepHistory history)
    {
        foreach (var entry in history.Entries.Where(entry =>
                     string.Equals(entry.FreshnessState, "stale", StringComparison.Ordinal) &&
                     File.Exists(entry.ArtifactPath)))
        {
            var prep = LoadBuilderExecutionPrep(Path.GetDirectoryName(entry.ArtifactPath) ?? string.Empty);
            if (prep is null || string.Equals(prep.FreshnessState, "stale", StringComparison.Ordinal))
            {
                continue;
            }

            var updated = prep with
            {
                FreshnessState = "stale",
                Summary = $"Stale execution prep. {prep.Summary}"
            };
            File.WriteAllText(entry.ArtifactPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void RewriteSupersededBuilderExecutionLaunchArtifacts(string repoRoot, string currentRunFolder)
    {
        foreach (var entry in LoadBuilderProofHistory(repoRoot).Entries.Where(entry =>
                     !string.Equals(entry.RunFolder, currentRunFolder, StringComparison.Ordinal)))
        {
            var artifactPath = BuilderExecutionLaunchPath(entry.RunFolder);
            if (!File.Exists(artifactPath))
            {
                continue;
            }

            var launch = LoadBuilderExecutionLaunch(entry.RunFolder);
            if (launch is null || string.Equals(launch.FreshnessState, "superseded", StringComparison.Ordinal))
            {
                continue;
            }

            var updated = launch with
            {
                FreshnessState = "superseded",
                Summary = $"Superseded launch. {launch.Summary}"
            };
            File.WriteAllText(artifactPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void RewriteSupersededBuilderExecutionResultArtifacts(string repoRoot, string currentRunFolder)
    {
        foreach (var entry in LoadBuilderProofHistory(repoRoot).Entries.Where(entry =>
                     !string.Equals(entry.RunFolder, currentRunFolder, StringComparison.Ordinal)))
        {
            var artifactPath = BuilderExecutionResultPath(entry.RunFolder);
            if (!File.Exists(artifactPath))
            {
                continue;
            }

            var result = LoadBuilderExecutionResult(entry.RunFolder);
            if (result is null || string.Equals(result.FreshnessState, "superseded", StringComparison.Ordinal))
            {
                continue;
            }

            var updated = result with
            {
                FreshnessState = "superseded",
                Summary = $"Superseded route result. {result.Summary}"
            };
            File.WriteAllText(artifactPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string DetermineBuilderRequestNormalizationState(BuilderRequestPolicyDecision requestDecision)
        => string.IsNullOrWhiteSpace(requestDecision.TaskClass)
            ? "ambiguous_request"
            : requestDecision.ComplexityDimensions.PromptAmbiguity switch
            {
                "high" => "normalized_bounded_request_with_high_prompt_ambiguity",
                "medium" => "normalized_bounded_request_with_medium_prompt_ambiguity",
                _ => "normalized_bounded_request"
            };

    private static string DetermineBuilderRequestIntakeState(BuilderRequestPolicyDecision requestDecision)
        => requestDecision.ChosenPolicyState switch
        {
            "direct_low_floor" => "ready_for_direct_low_floor",
            "split_first_low_floor" => "ready_for_split_first_low_floor",
            "low_floor_with_repair_loop_expected" => "ready_for_low_floor_with_repair_loop",
            "stronger_tier_optional" => "stronger_tier_optional",
            "stronger_tier_recommended" => "stronger_tier_recommended",
            "stronger_tier_required" => "task_out_of_scope",
            _ => "task_out_of_scope"
        };

    private static string DetermineBuilderExecutionPrepRoute(BuilderRequestIntake intake)
        => intake.IntakeClassificationState switch
        {
            "ready_for_direct_low_floor" => "direct_low_floor_route",
            "ready_for_split_first_low_floor" => "split_first_low_floor_route",
            "ready_for_low_floor_with_repair_loop" => "low_floor_with_repair_loop_route",
            "stronger_tier_optional" => "current_model_with_optional_stronger_tier_route",
            "stronger_tier_recommended" => "stronger_tier_recommended_route",
            _ => "task_out_of_scope_route"
        };

    private static string DetermineBuilderExecutionPrepExpectation(
        string intakeState,
        BuilderRequestPolicyDecision requestDecision,
        BuilderSplitFirstOutcome? splitOutcome)
        => intakeState switch
        {
            "ready_for_direct_low_floor" => "low",
            "ready_for_split_first_low_floor" when string.Equals(splitOutcome?.ClosureClassification, "split_equal_to_stronger_tier", StringComparison.Ordinal) => "moderate",
            "ready_for_split_first_low_floor" => "moderate",
            "ready_for_low_floor_with_repair_loop" => "high",
            "stronger_tier_optional" => "low",
            "stronger_tier_recommended" => "high",
            _ => "not_acceptable_on_current_floor"
        };

    private static IReadOnlyList<string> BuildBuilderExecutionPrepRequiredEvidencePaths(
        BuilderRequestPolicyDecision requestDecision,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome)
    {
        var paths = new List<string>(requestDecision.LinkedEvidencePaths);
        if (splitPlan is not null)
        {
            paths.Add(splitPlan.ArtifactPath);
        }

        if (tieredRoutingPolicy is not null)
        {
            paths.Add(tieredRoutingPolicy.ArtifactPath);
        }

        if (splitOutcome is not null)
        {
            paths.Add(splitOutcome.ArtifactPath);
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildBuilderExecutionPrepNextActions(
        BuilderRequestIntake intake,
        BuilderRequestPolicyDecision requestDecision,
        BuilderModelRoutingPlan routingPlan,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome)
    {
        return intake.IntakeClassificationState switch
        {
            "ready_for_direct_low_floor" => new[]
            {
                "Review the current routing summary and keep the task inside the recorded file and project envelope.",
                "Run the bounded builder step on the current model without widening scope.",
                "Build and test the touched scope immediately after the change lands."
            },
            "ready_for_split_first_low_floor" => new[]
            {
                "Review the split-first plan before starting any code change.",
                "Use the split execution hooks one bounded step at a time instead of attempting the unsplit task.",
                "Keep the linked weak-spot mitigation summary visible while executing the split path."
            },
            "ready_for_low_floor_with_repair_loop" => new[]
            {
                "Review the first bounded failure evidence before editing code.",
                "Keep the repair loop enabled and rerun the narrowest failing scope after each bounded fix.",
                "Do not widen the task beyond the recorded proof envelope."
            },
            "stronger_tier_optional" => new[]
            {
                "The current model is still acceptable for this bounded task.",
                "Use the stronger tier only if cleaner speed matters more than staying on the floor model.",
                "Keep the comparative proof summary nearby so the operator can justify that choice."
            },
            "stronger_tier_recommended" => new[]
            {
                "Review the stronger-tier recommendation before starting a new low-floor attempt.",
                "If staying on the floor model, keep the task tightly bounded and expect extra repair burden.",
                "If the operator wants cleaner success, prepare the stronger-tier route explicitly."
            },
            _ => new[]
            {
                "Do not run the unsplit task on the low-floor model.",
                "Review the stronger-tier route and split guidance before proceeding.",
                $"Reason the current floor is not acceptable: {routingPlan.ReasonForEscalation}"
            }
        };
    }

    private static string BuildBuilderExecutionPrepSummary(
        BuilderRequestIntake intake,
        string selectedRoute,
        string rerunRepairExpectationLevel,
        BuilderModelRoutingPlan routingPlan,
        bool splitPlanRequired,
        BuilderDefaultRouteDecision defaultRouteDecision,
        BuilderCapabilityBlockDecision capabilityDecision)
    {
        var splitText = splitPlanRequired
            ? "Split-first prep is required before execution."
            : "No split-first prep is required for the selected route.";
        var escalationText = intake.IntakeClassificationState switch
        {
            "stronger_tier_optional" => "The stronger tier is optional cleaner speed only.",
            "stronger_tier_recommended" => $"The stronger tier is recommended because {routingPlan.ReasonForEscalation.TrimEnd('.').ToLowerInvariant()}.",
            "task_out_of_scope" => $"The current floor is out of scope because {routingPlan.ReasonForEscalation.TrimEnd('.').ToLowerInvariant()}.",
            _ => "The current model remains acceptable for the selected bounded route."
        };
        return $"{intake.TargetLabel} is prepared on route {selectedRoute}. Route source={defaultRouteDecision.RouteSourceState}. Repair/rerun expectation={rerunRepairExpectationLevel}. Capability={capabilityDecision.RoutingDecisionState}. {splitText} {escalationText}";
    }

    private static string BuildBuilderExecutionPrepId(string sourceIntakeId)
        => $"{sourceIntakeId}-prep";

    private static PreparedBuilderExecutionLaunch BuildBuilderExecutionLaunch(
        string runFolder,
        BuilderProofRun proofRun,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep,
        PreparedBuilderExecutionResult? existingResult,
        BuilderLaunchDefaultDecision launchDefaultDecision)
    {
        var eligibilityState = launchDefaultDecision.LaunchEligibilityState;
        var blockReason = launchDefaultDecision.BlockReason;
        var selectedModelTier = string.Equals(prep.SelectedRoute, "stronger_tier_recommended_route", StringComparison.Ordinal) ||
                                string.Equals(prep.SelectedRoute, "task_out_of_scope_route", StringComparison.Ordinal)
            ? "current_model_tier_with_escalation_blocked"
            : "current_model_tier";
        var linkedArtifactPaths = intake.LinkedEvidencePaths
            .Concat(prep.LinkedArtifactPaths)
            .Concat(new[]
            {
                intake.ArtifactPath,
                prep.ArtifactPath,
                launchDefaultDecision.ArtifactPath,
                BuilderRequestPolicyDecisionPath(runFolder),
                BuilderPolicyStabilityPath(runFolder),
                BuilderDefaultPolicyPath(runFolder)
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = string.Equals(eligibilityState, "eligible", StringComparison.Ordinal)
            ? $"{prep.TargetLabel} is ready to launch on {prep.SelectedRoute} with {prep.CurrentModelId}."
            : $"{prep.TargetLabel} launch is blocked on {prep.SelectedRoute}: {blockReason}";
        return new PreparedBuilderExecutionLaunch(
            $"{proofRun.ProofRunId}-{SanitizeBuilderProofToken(prep.SelectedRoute)}-launch",
            proofRun.ProofRunId,
            intake.RequestId,
            BuildBuilderExecutionPrepId(intake.RequestId),
            prep.ArtifactPath,
            prep.SelectedRoute,
            selectedModelTier,
            prep.CurrentModelId,
            eligibilityState,
            blockReason,
            "current",
            linkedArtifactPaths,
            summary,
            BuilderExecutionLaunchPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static (string State, string BlockReason) DetermineBuilderExecutionLaunchEligibility(
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep,
        PreparedBuilderExecutionResult? existingResult,
        string operatorDecisionState,
        string currentReadinessState,
        bool defaultRouteSuspended,
        string capabilityRoutingState,
        string capabilityBlockReason)
    {
        if (!string.Equals(intake.FreshnessState, "current", StringComparison.Ordinal))
        {
            return ("blocked_stale_intake", "Prepared launch is blocked because the current builder intake is stale.");
        }

        if (!string.Equals(prep.FreshnessState, "current", StringComparison.Ordinal))
        {
            return ("blocked_stale_execution_prep", "Prepared launch is blocked because the current execution prep is stale.");
        }

        if (existingResult is not null &&
            string.Equals(existingResult.FreshnessState, "current", StringComparison.Ordinal) &&
            string.Equals(existingResult.SourceExecutionPrepId, BuildBuilderExecutionPrepId(intake.RequestId), StringComparison.Ordinal))
        {
            return ("blocked_already_launched", "Prepared launch is blocked because the current execution prep already has a recorded route result.");
        }

        if (string.Equals(capabilityRoutingState, "route_blocked_missing_toolchain", StringComparison.Ordinal))
        {
            return ("blocked_missing_toolchain", FirstNonEmpty(capabilityBlockReason, "Prepared launch is blocked because the required repo toolchain is unavailable."));
        }

        if (string.Equals(capabilityRoutingState, "route_blocked_repo_policy", StringComparison.Ordinal))
        {
            return ("blocked_repo_policy", FirstNonEmpty(capabilityBlockReason, "Prepared launch is blocked by repo toolchain policy."));
        }

        if (string.Equals(operatorDecisionState, "accepted_default_route", StringComparison.Ordinal))
        {
            if (defaultRouteSuspended)
            {
                return ("blocked_default_suspended", "Prepared launch is blocked because the confirmed default route is currently suspended by contradiction evidence.");
            }

            if (!string.Equals(currentReadinessState, "confirmed_for_bounded_use", StringComparison.Ordinal) &&
                !string.Equals(currentReadinessState, "confirmed_with_repair_loop", StringComparison.Ordinal))
            {
                return ("blocked_default_not_confirmed", "Prepared launch is blocked because the current default route is not presently confirmed for bounded use.");
            }
        }

        if (string.Equals(operatorDecisionState, "operator_override_selected", StringComparison.Ordinal) &&
            !string.Equals(prep.OperatorOverrideState, "overridden_by_operator", StringComparison.Ordinal))
        {
            return ("blocked_override_not_explicit", "Prepared launch is blocked because the override route was not recorded explicitly.");
        }

        if (!IsBuilderPreparedRouteSupported(prep.SelectedRoute))
        {
            return ("blocked_route_unsupported", $"Prepared launch does not support route {prep.SelectedRoute} without a stronger-tier decision.");
        }

        var missingEvidence = prep.RequiredEvidencePaths.FirstOrDefault(path => !BuilderArtifactPathExists(path));
        if (!string.IsNullOrWhiteSpace(missingEvidence))
        {
            return ("blocked_missing_required_artifacts", $"Prepared launch is blocked because required evidence is missing: {missingEvidence}");
        }

        if (prep.SplitPlanRequired)
        {
            if (!BuilderArtifactPathExists(prep.SplitPlanPath))
            {
                return ("blocked_missing_split_plan", "Prepared launch is blocked because the split-first plan artifact is unavailable.");
            }

            if (prep.FutureExecutionHookPaths.Count == 0)
            {
                return ("blocked_missing_future_hooks", "Prepared launch is blocked because the split-first execution hooks are unavailable.");
            }

            var missingHook = prep.FutureExecutionHookPaths.FirstOrDefault(path => !BuilderArtifactPathExists(path));
            if (!string.IsNullOrWhiteSpace(missingHook))
            {
                return ("blocked_missing_future_hooks", $"Prepared launch is blocked because a split-first execution hook is missing: {missingHook}");
            }
        }

        return ("eligible", string.Empty);
    }

    private static bool IsBuilderPreparedRouteSupported(string route)
        => string.Equals(route, "direct_low_floor_route", StringComparison.Ordinal) ||
           string.Equals(route, "split_first_low_floor_route", StringComparison.Ordinal) ||
           string.Equals(route, "low_floor_with_repair_loop_route", StringComparison.Ordinal) ||
           string.Equals(route, "current_model_with_optional_stronger_tier_route", StringComparison.Ordinal);

    private static PreparedBuilderExecutionResult BuildBlockedBuilderExecutionResult(
        string runFolder,
        PreparedBuilderExecutionLaunch launch,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep)
    {
        var summary = $"{prep.TargetLabel} launch did not run. {launch.BlockReason}";
        return new PreparedBuilderExecutionResult(
            launch.LaunchId,
            launch.SourceProofRunId,
            launch.SourceIntakeId,
            launch.SourceExecutionPrepId,
            prep.SelectedRoute,
            launch.SelectedModelTier,
            launch.SelectedModelId,
            prep.TargetLabel,
            "not_run",
            "not_run",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "launch_blocked",
            "insufficient_for_scope",
            "current",
            launch.LinkedArtifactPaths
                .Concat(new[] { intake.ArtifactPath, prep.ArtifactPath, launch.ArtifactPath })
                .Where(BuilderArtifactPathExists)
                .Distinct(StringComparer.Ordinal)
            .ToArray(),
            summary,
            BuilderExecutionResultPath(runFolder),
            DateTimeOffset.UtcNow,
            string.Empty,
            string.Empty);
    }

    private static PreparedBuilderExecutionResult BuildBuilderExecutionResultFromProofCase(
        string runFolder,
        PreparedBuilderExecutionLaunch launch,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep,
        BuilderProofCaseResult caseResult)
    {
        var outcomeClassification = DetermineBuilderExecutionResultClassification(caseResult);
        var routeComparison = DetermineBuilderExecutionRouteComparison(caseResult);
        var linkedArtifactPaths = new[]
            {
                launch.ArtifactPath,
                intake.ArtifactPath,
                prep.ArtifactPath,
                caseResult.ValidationResultPath,
                caseResult.FollowupIntakePath,
                caseResult.FollowupPlanPath,
                caseResult.RepairPrepBundlePath,
                caseResult.RepairBundlePath,
                caseResult.RecoveryValidationResultPath,
                caseResult.FollowupExecutionOutcomePath
            }
            .Concat(caseResult.StageResults.Select(stage => stage.LogPath))
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{prep.TargetLabel} launched on {prep.SelectedRoute}: {outcomeClassification.Replace('_', ' ')}. {caseResult.FinalSummary}";
        return new PreparedBuilderExecutionResult(
            launch.LaunchId,
            launch.SourceProofRunId,
            launch.SourceIntakeId,
            launch.SourceExecutionPrepId,
            prep.SelectedRoute,
            launch.SelectedModelTier,
            caseResult.ModelId,
            caseResult.TargetScope,
            caseResult.BuildResult,
            caseResult.TestResult,
            caseResult.FollowupState,
            caseResult.FollowupIntakePath,
            caseResult.FollowupPlanPath,
            caseResult.RepairPrepBundlePath,
            caseResult.RepairBundlePath,
            caseResult.FollowupExecutionOutcomePath,
            outcomeClassification,
            routeComparison,
            "current",
            linkedArtifactPaths,
            summary,
            BuilderExecutionResultPath(runFolder),
            DateTimeOffset.UtcNow,
            caseResult.TargetFolder,
            caseResult.StarterStateManifestPath);
    }

    private static PreparedBuilderExecutionResult BuildBuilderExecutionResultFromSplitOutcome(
        string runFolder,
        PreparedBuilderExecutionLaunch launch,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep,
        BuilderSplitFirstOutcome splitOutcome)
    {
        var outcomeClassification = DetermineBuilderExecutionResultClassification(splitOutcome);
        var routeComparison = DetermineBuilderExecutionRouteComparison(splitOutcome);
        var linkedArtifactPaths = new[]
            {
                launch.ArtifactPath,
                intake.ArtifactPath,
                prep.ArtifactPath,
                splitOutcome.SplitPlanPath,
                splitOutcome.SplitStepExecutionPath,
                splitOutcome.ComparativeProofArtifactPath,
                splitOutcome.SplitValidationResultPath,
                splitOutcome.SplitFollowupPlanPath,
                splitOutcome.SplitRepairPrepBundlePath,
                splitOutcome.SplitFollowupExecutionOutcomePath
            }
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{prep.TargetLabel} launched on {prep.SelectedRoute}: {outcomeClassification.Replace('_', ' ')}. {splitOutcome.Summary}";
        return new PreparedBuilderExecutionResult(
            launch.LaunchId,
            launch.SourceProofRunId,
            launch.SourceIntakeId,
            launch.SourceExecutionPrepId,
            prep.SelectedRoute,
            launch.SelectedModelTier,
            launch.SelectedModelId,
            splitOutcome.ExecutedStepLabel,
            splitOutcome.SplitBuildResult,
            splitOutcome.SplitTestResult,
            string.IsNullOrWhiteSpace(splitOutcome.SplitFollowupPlanPath) ? "not_needed" : "created",
            string.Empty,
            splitOutcome.SplitFollowupPlanPath,
            splitOutcome.SplitRepairPrepBundlePath,
            string.Empty,
            splitOutcome.SplitFollowupExecutionOutcomePath,
            outcomeClassification,
            routeComparison,
            "current",
            linkedArtifactPaths,
            summary,
            BuilderExecutionResultPath(runFolder),
            DateTimeOffset.UtcNow,
            splitOutcome.SplitTargetFolderPath,
            splitOutcome.SplitStarterStateManifestPath);
    }

    private static string DetermineBuilderExecutionResultClassification(BuilderProofCaseResult caseResult)
    {
        if (string.Equals(caseResult.FinalClassification, "passed_cleanly", StringComparison.Ordinal))
        {
            return "launched_and_passed";
        }

        if (string.Equals(caseResult.FinalClassification, "recovered_with_guidance", StringComparison.Ordinal) || caseResult.RecoveryRequired)
        {
            return "launched_and_passed_with_repair";
        }

        if (string.Equals(caseResult.RepeatedFailureClassification, "beyond_model_floor", StringComparison.Ordinal))
        {
            return "launched_and_failed_out_of_scope";
        }

        return "launched_and_failed_followup_created";
    }

    private static string DetermineBuilderExecutionResultClassification(BuilderSplitFirstOutcome splitOutcome)
    {
        if (string.Equals(splitOutcome.SplitResultFinalClassification, "passed_cleanly", StringComparison.Ordinal) &&
            !splitOutcome.SplitRecoveryRequired)
        {
            return "launched_and_passed";
        }

        if (string.Equals(splitOutcome.SplitResultFinalClassification, "recovered_with_guidance", StringComparison.Ordinal) ||
            splitOutcome.SplitRecoveryRequired)
        {
            return "launched_and_passed_with_repair";
        }

        return splitOutcome.ClosureClassification switch
        {
            "stronger_tier_still_preferred" => "launched_and_failed_out_of_scope",
            "split_failed" => "launched_and_failed_out_of_scope",
            _ => "launched_and_failed_followup_created"
        };
    }

    private static string DetermineBuilderExecutionRouteComparison(BuilderProofCaseResult caseResult)
        => DetermineBuilderExecutionResultClassification(caseResult) switch
        {
            "launched_and_passed" => "confirmed",
            "launched_and_passed_with_repair" => "optimistic_but_recoverable",
            "launched_and_failed_followup_created" => "optimistic_but_recoverable",
            _ => "insufficient_for_scope"
        };

    private static string DetermineBuilderExecutionRouteComparison(BuilderSplitFirstOutcome splitOutcome)
        => splitOutcome.ClosureClassification switch
        {
            "split_equal_to_stronger_tier" => "confirmed",
            "split_closed_gap" => "confirmed",
            "split_viable_but_costlier" => "optimistic_but_recoverable",
            "split_improved_but_not_closed" => "optimistic_but_recoverable",
            _ => "insufficient_for_scope"
        };

    private static BuilderRouteOverrideEvidence BuildBuilderRouteOverrideEvidence(
        string repoRoot,
        string runFolder,
        BuilderLaunchDefaultDecision launchDecision,
        PreparedBuilderExecutionResult result,
        BuilderDefaultRouteDecision? defaultRouteDecision,
        BuilderReadinessGate? readinessGate,
        BuilderReadinessContradictions? contradictions)
    {
        var baseline = LoadBuilderOverrideBaseline(repoRoot, runFolder, launchDecision.TaskClass, launchDecision.ConfirmedDefaultRoute);
        var comparisonState = DetermineBuilderOverrideOutcomeComparisonState(
            launchDecision,
            result,
            baseline?.FinalRouteOutcomeClassification,
            baseline?.PreparedRouteComparisonState,
            readinessGate,
            contradictions);
        var linkedArtifactPaths = launchDecision.LinkedArtifactPaths
            .Concat(result.LinkedArtifactPaths)
            .Concat(new[]
            {
                launchDecision.ArtifactPath,
                result.ArtifactPath,
                defaultRouteDecision?.ArtifactPath ?? string.Empty,
                readinessGate?.ArtifactPath ?? string.Empty,
                contradictions?.ArtifactPath ?? string.Empty
            })
            .Where(BuilderArtifactPathExists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = string.Equals(launchDecision.OperatorDecisionState, "operator_override_selected", StringComparison.Ordinal)
            ? $"{launchDecision.TaskClass} launched on override route {launchDecision.ActualLaunchRoute}. Outcome={result.FinalRouteOutcomeClassification}. Compared to the confirmed default, this override {comparisonState.Replace('_', ' ')}."
            : $"{launchDecision.TaskClass} accepted the confirmed default route {launchDecision.ConfirmedDefaultRoute}. Outcome={result.FinalRouteOutcomeClassification}.";

        return new BuilderRouteOverrideEvidence(
            launchDecision.SourceProofRunId,
            launchDecision.SourceIntakeId,
            launchDecision.SourceExecutionPrepId,
            result.SourceLaunchId,
            launchDecision.TaskClass,
            launchDecision.ConfirmedDefaultRoute,
            launchDecision.ActualLaunchRoute,
            launchDecision.OperatorDecisionState,
            launchDecision.OverrideReason,
            result.FinalRouteOutcomeClassification,
            baseline?.FinalRouteOutcomeClassification ?? string.Empty,
            comparisonState,
            linkedArtifactPaths,
            summary,
            BuilderRouteOverrideEvidencePath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static PreparedBuilderExecutionResult? LoadBuilderOverrideBaseline(
        string repoRoot,
        string currentRunFolder,
        string taskClass,
        string defaultRoute)
        => LoadBuilderProofHistory(repoRoot).Entries
            .Where(entry => !string.Equals(entry.RunFolder, currentRunFolder, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CompletedUtc)
            .ThenByDescending(entry => entry.RunId, StringComparer.Ordinal)
            .Select(entry => new
            {
                Intake = LoadBuilderRequestIntake(entry.RunFolder),
                LaunchDecision = LoadBuilderLaunchDefaultDecision(entry.RunFolder),
                Result = LoadBuilderExecutionResult(entry.RunFolder)
            })
            .Where(item => item.Intake is not null &&
                           item.LaunchDecision is not null &&
                           item.Result is not null &&
                           string.Equals(item.Intake.NormalizedTaskClass, taskClass, StringComparison.Ordinal) &&
                           string.Equals(item.LaunchDecision.ConfirmedDefaultRoute, defaultRoute, StringComparison.Ordinal) &&
                           string.Equals(item.LaunchDecision.OperatorDecisionState, "accepted_default_route", StringComparison.Ordinal))
            .Select(item => item.Result)
            .FirstOrDefault();

    private static string DetermineBuilderOverrideOutcomeComparisonState(
        BuilderLaunchDefaultDecision launchDecision,
        PreparedBuilderExecutionResult result,
        string? baselineDefaultOutcomeClassification,
        string? baselineDefaultComparisonState,
        BuilderReadinessGate? readinessGate,
        BuilderReadinessContradictions? contradictions)
    {
        if (!string.Equals(launchDecision.OperatorDecisionState, "operator_override_selected", StringComparison.Ordinal))
        {
            return "not_applicable";
        }

        if ((readinessGate?.DefaultRouteSuspended ?? false) || contradictions is { Entries.Count: > 0 })
        {
            return OutcomeScore(result.FinalRouteOutcomeClassification) >= OutcomeScore("launched_and_passed_with_repair")
                ? "avoided_contradiction"
                : "regressed_outcome";
        }

        if (string.IsNullOrWhiteSpace(baselineDefaultOutcomeClassification))
        {
            return "not_applicable";
        }

        var currentScore = OutcomeScore(result.FinalRouteOutcomeClassification);
        var baselineScore = OutcomeScore(baselineDefaultOutcomeClassification);
        if (currentScore > baselineScore)
        {
            return "improved_outcome";
        }

        if (currentScore == baselineScore)
        {
            return string.Equals(result.PreparedRouteComparisonState, baselineDefaultComparisonState, StringComparison.Ordinal)
                ? "matched_default_outcome"
                : "improved_outcome";
        }

        return "regressed_outcome";
    }

    private static int OutcomeScore(string? classification)
        => classification switch
        {
            "launched_and_passed" => 4,
            "launched_and_passed_with_repair" => 3,
            "launched_and_failed_followup_created" => 2,
            "launched_and_failed_out_of_scope" => 1,
            "launch_blocked" => 0,
            _ => -1
        };

    private static BuilderRouteReviewCandidates BuildBuilderRouteReviewCandidates(
        string repoRoot,
        string runFolder,
        BuilderConfirmedTaskClasses? confirmedTaskClasses,
        BuilderReadinessContradictions? contradictions)
    {
        var overrideEvidence = LoadBuilderProofHistory(repoRoot).Entries
            .OrderByDescending(entry => entry.CompletedUtc)
            .ThenByDescending(entry => entry.RunId, StringComparer.Ordinal)
            .Select(entry => LoadBuilderRouteOverrideEvidence(entry.RunFolder))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        var entries = new List<BuilderRouteReviewCandidateEntry>();

        if (confirmedTaskClasses is not null)
        {
            foreach (var confirmedEntry in confirmedTaskClasses.Entries
                         .Where(entry => entry.BuilderReadyForBoundedUse &&
                                         !entry.DefaultRouteSuspended &&
                                         entry.ContradictionCount == 0))
            {
                entries.Add(new BuilderRouteReviewCandidateEntry(
                    confirmedEntry.TaskClass,
                    "stable_default",
                    confirmedEntry.CurrentRoute,
                    confirmedEntry.ConfirmationCount,
                    $"{confirmedEntry.CurrentRoute} is stable for {confirmedEntry.TaskClass} with {confirmedEntry.ConfirmationCount} confirmation(s).",
                    confirmedEntry.LinkedEvidencePaths));
            }
        }

        foreach (var overrideGroup in overrideEvidence
                     .Where(item => string.Equals(item.OverrideState, "operator_override_selected", StringComparison.Ordinal))
                     .GroupBy(item => item.TaskClass, StringComparer.Ordinal))
        {
            var outperforming = overrideGroup.Count(item =>
                string.Equals(item.OverrideOutcomeComparisonState, "improved_outcome", StringComparison.Ordinal) ||
                string.Equals(item.OverrideOutcomeComparisonState, "avoided_contradiction", StringComparison.Ordinal));
            if (outperforming >= 2)
            {
                var latest = overrideGroup
                    .OrderByDescending(item => item.ObservedUtc)
                    .ThenByDescending(item => item.SourceProofRunId, StringComparer.Ordinal)
                    .First();
                entries.Add(new BuilderRouteReviewCandidateEntry(
                    overrideGroup.Key,
                    "overrides_outperforming_default",
                    latest.SelectedRoute,
                    outperforming,
                    $"{overrideGroup.Key} has {outperforming} override run(s) outperforming the default route and should be reviewed explicitly.",
                    overrideGroup.SelectMany(item => item.LinkedArtifactPaths).Distinct(StringComparer.Ordinal).ToArray()));
            }
        }

        if (contradictions is not null)
        {
            foreach (var contradiction in contradictions.Entries)
            {
                entries.Add(new BuilderRouteReviewCandidateEntry(
                    contradiction.TaskClass,
                    contradiction.ContradictionAttributionState switch
                    {
                        "override_route_failure" => "override_caused_contradiction",
                        "default_route_failure" => "default_route_contradiction",
                        _ => "mixed_contradiction"
                    },
                    contradiction.PriorConfirmedRoute,
                    contradiction.ContradictoryRunIds.Count,
                    contradiction.ContradictionAttributionState switch
                    {
                        "override_route_failure" => $"{contradiction.TaskClass} carries override-caused contradiction evidence against {contradiction.PriorConfirmedRoute}.",
                        "default_route_failure" => $"{contradiction.TaskClass} carries true default-route contradiction evidence against {contradiction.PriorConfirmedRoute}.",
                        _ => $"{contradiction.TaskClass} carries mixed contradiction evidence against {contradiction.PriorConfirmedRoute}."
                    },
                    contradiction.LinkedArtifactPaths));
            }
        }

        var orderedEntries = entries
            .GroupBy(entry => $"{entry.TaskClass}:{entry.CandidateState}", StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.EvidenceCount)
                .ThenBy(item => item.TaskClass, StringComparer.Ordinal)
                .First())
            .OrderBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.CandidateState, StringComparer.Ordinal)
            .ToArray();
        var summary = orderedEntries.Length == 0
            ? "No builder route review candidates are currently recorded."
            : $"{orderedEntries.Count(entry => string.Equals(entry.CandidateState, "stable_default", StringComparison.Ordinal))} stable default class(es), " +
              $"{orderedEntries.Count(entry => string.Equals(entry.CandidateState, "overrides_outperforming_default", StringComparison.Ordinal))} override review candidate(s), and " +
              $"{orderedEntries.Count(entry =>
                    string.Equals(entry.CandidateState, "override_caused_contradiction", StringComparison.Ordinal) ||
                    string.Equals(entry.CandidateState, "default_route_contradiction", StringComparison.Ordinal) ||
                    string.Equals(entry.CandidateState, "mixed_contradiction", StringComparison.Ordinal))} contradiction review candidate(s) are recorded.";

        return new BuilderRouteReviewCandidates(
            Path.GetFileName(runFolder),
            orderedEntries,
            summary,
            BuilderPolicyReviewCandidatesPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderRouteReconfirmation BuildBuilderRouteReconfirmation(
        string repoRoot,
        string runFolder,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep,
        BuilderDefaultRouteDecision? defaultRouteDecision,
        BuilderReadinessGate? readinessGate,
        BuilderReadinessContradictions? contradictions,
        BuilderLaunchDefaultDecision? launchDecision,
        BuilderRouteOverrideEvidence? overrideEvidence)
    {
        var evidence = LoadBuilderReadinessEvidenceSnapshots(repoRoot, intake, prep);
        var contradictionEvidence = BuildBuilderReadinessContradictionEvidence(evidence);
        var attributionState = DetermineBuilderContradictionAttributionState(contradictionEvidence);
        var threshold = DetermineBuilderReconfirmationThreshold(attributionState, BuilderReadinessRequiredPreparedLaunches);
        var latestContradictionUtc = contradictionEvidence
            .OrderByDescending(item => item.ObservedUtc)
            .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
            .Select(item => (DateTimeOffset?)item.ObservedUtc)
            .FirstOrDefault();
        var reconfirmationSnapshots = latestContradictionUtc.HasValue
            ? evidence
                .Where(snapshot =>
                    snapshot.ObservedUtc > latestContradictionUtc.Value &&
                    IsBuilderReadinessConfirmation(snapshot.Result) &&
                    string.Equals(
                        ResolveBuilderActualRoute(snapshot.Prep.SelectedRoute, snapshot.Launch, snapshot.Result),
                        prep.SelectedRoute,
                        StringComparison.Ordinal))
                .OrderBy(snapshot => snapshot.ObservedUtc)
                .ThenBy(snapshot => snapshot.RunId, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<BuilderReadinessEvidenceSnapshot>();
        var freshProofRunCount = readinessGate?.FreshProofRunCountAfterLatestContradiction
                                 ?? (latestContradictionUtc.HasValue
                                     ? evidence.Count(snapshot => snapshot.ObservedUtc > latestContradictionUtc.Value)
                                     : 0);
        var freshPreparedLaunchConfirmations = readinessGate?.FreshPreparedLaunchConfirmationCountAfterLatestContradiction
                                               ?? reconfirmationSnapshots.Length;
        var reconfirmationState = DetermineBuilderRouteReconfirmationState(
            contradictionEvidence.Length,
            readinessGate?.CurrentReadinessGateState ?? "not_recorded",
            defaultRouteDecision?.DefaultRouteSuspended ?? false,
            freshProofRunCount,
            freshPreparedLaunchConfirmations,
            threshold.RequiredFreshProofRunCount,
            threshold.RequiredFreshPreparedLaunchConfirmations);
        var linkedArtifactPaths = new[]
            {
                intake.ArtifactPath,
                prep.ArtifactPath,
                defaultRouteDecision?.ArtifactPath ?? string.Empty,
                readinessGate?.ArtifactPath ?? string.Empty,
                contradictions?.ArtifactPath ?? string.Empty,
                launchDecision?.ArtifactPath ?? string.Empty,
                overrideEvidence?.ArtifactPath ?? string.Empty,
                BuilderReadinessGateHistoryPathForRepo(repoRoot)
            }
            .Concat(reconfirmationSnapshots.SelectMany(snapshot => new[]
            {
                snapshot.Launch?.ArtifactPath ?? string.Empty,
                snapshot.Result?.ArtifactPath ?? string.Empty
            }))
            .Concat(contradictionEvidence.Select(item => item.ArtifactPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = contradictionEvidence.Length == 0
            ? $"{prep.SelectedRoute} has no active contradiction record and does not require route reconfirmation."
            : $"{prep.SelectedRoute} for {intake.NormalizedTaskClass} is {reconfirmationState}. Attribution={attributionState}. Recovery progress={freshPreparedLaunchConfirmations}/{threshold.RequiredFreshPreparedLaunchConfirmations} launch confirmation(s) and {freshProofRunCount}/{threshold.RequiredFreshProofRunCount} proof run(s) after contradiction.";

        return new BuilderRouteReconfirmation(
            intake.SourceProofRunId,
            intake.RequestId,
            intake.NormalizedTaskClass,
            defaultRouteDecision?.ChosenDefaultRoute ?? prep.SelectedRoute,
            attributionState,
            contradictionEvidence.Select(item => item.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            reconfirmationSnapshots.Select(snapshot => snapshot.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            threshold.RequiredFreshProofRunCount,
            threshold.RequiredFreshPreparedLaunchConfirmations,
            freshProofRunCount,
            freshPreparedLaunchConfirmations,
            reconfirmationState,
            linkedArtifactPaths,
            summary,
            BuilderRouteReconfirmationPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static BuilderDefaultRouteRecovery BuildBuilderDefaultRouteRecovery(
        string runFolder,
        BuilderRequestIntake intake,
        BuilderExecutionPrep prep,
        BuilderDefaultRouteDecision? defaultRouteDecision,
        BuilderReadinessGate? readinessGate,
        BuilderReadinessContradictions? contradictions,
        BuilderLaunchDefaultDecision? launchDecision,
        BuilderRouteOverrideEvidence? overrideEvidence,
        BuilderRouteReconfirmation reconfirmation)
    {
        var suspended = defaultRouteDecision?.DefaultRouteSuspended ?? false;
        var recoveryState = DetermineBuilderDefaultRouteRecoveryState(
            reconfirmation,
            readinessGate?.CurrentReadinessGateState ?? "not_recorded",
            suspended);
        var restored = string.Equals(recoveryState, "restored_default_route", StringComparison.Ordinal);
        var linkedArtifactPaths = new[]
            {
                intake.ArtifactPath,
                prep.ArtifactPath,
                defaultRouteDecision?.ArtifactPath ?? string.Empty,
                readinessGate?.ArtifactPath ?? string.Empty,
                contradictions?.ArtifactPath ?? string.Empty,
                launchDecision?.ArtifactPath ?? string.Empty,
                overrideEvidence?.ArtifactPath ?? string.Empty,
                reconfirmation.ArtifactPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = reconfirmation.ContradictionSourceRunIds.Count == 0
            ? $"{prep.SelectedRoute} does not require recovery because no contradiction has been recorded."
            : restored
                ? $"{prep.SelectedRoute} recovered after {reconfirmation.FreshPreparedLaunchConfirmationCount}/{reconfirmation.RequiredFreshPreparedLaunchConfirmations} fresh launch confirmation(s). Suspension cause={reconfirmation.ContradictionAttributionState}."
                : $"{prep.SelectedRoute} is still suspended. Suspension cause={reconfirmation.ContradictionAttributionState}. Recovery progress={reconfirmation.FreshPreparedLaunchConfirmationCount}/{reconfirmation.RequiredFreshPreparedLaunchConfirmations} launch confirmation(s).";

        return new BuilderDefaultRouteRecovery(
            intake.SourceProofRunId,
            intake.RequestId,
            intake.NormalizedTaskClass,
            defaultRouteDecision?.ChosenDefaultRoute ?? prep.SelectedRoute,
            BuildBuilderSuspensionCauseState(reconfirmation.ContradictionAttributionState),
            reconfirmation.ContradictionAttributionState,
            reconfirmation.ReconfirmationRunIds,
            reconfirmation.RequiredFreshProofRunCount,
            reconfirmation.RequiredFreshPreparedLaunchConfirmations,
            reconfirmation.FreshProofRunCount,
            reconfirmation.FreshPreparedLaunchConfirmationCount,
            recoveryState,
            restored,
            linkedArtifactPaths,
            summary,
            BuilderDefaultRouteRecoveryPath(runFolder),
            DateTimeOffset.UtcNow);
    }

    private static string DetermineBuilderRouteReconfirmationState(
        int contradictionCount,
        string readinessState,
        bool defaultRouteSuspended,
        int freshProofRunCount,
        int freshPreparedLaunchConfirmations,
        int requiredFreshProofRunCount,
        int requiredFreshPreparedLaunchConfirmations)
    {
        if (contradictionCount == 0)
        {
            return "not_required";
        }

        if (string.Equals(readinessState, "contradicted", StringComparison.Ordinal))
        {
            return "reconfirmation_failed";
        }

        if (!defaultRouteSuspended &&
            freshProofRunCount >= requiredFreshProofRunCount &&
            freshPreparedLaunchConfirmations >= requiredFreshPreparedLaunchConfirmations)
        {
            return "reconfirmed_default_route";
        }

        if (freshProofRunCount > 0 || freshPreparedLaunchConfirmations > 0)
        {
            return "reconfirmation_in_progress";
        }

        return defaultRouteSuspended
            ? "default_still_suspended"
            : "reconfirmation_required";
    }

    private static string DetermineBuilderDefaultRouteRecoveryState(
        BuilderRouteReconfirmation reconfirmation,
        string readinessState,
        bool defaultRouteSuspended)
    {
        if (reconfirmation.ContradictionSourceRunIds.Count == 0)
        {
            return "recovery_not_required";
        }

        if (string.Equals(readinessState, "contradicted", StringComparison.Ordinal))
        {
            return "recovery_failed";
        }

        if (!defaultRouteSuspended &&
            string.Equals(reconfirmation.CurrentReconfirmationState, "reconfirmed_default_route", StringComparison.Ordinal))
        {
            return "restored_default_route";
        }

        return "default_still_suspended";
    }

    private static string BuildBuilderSuspensionCauseState(string contradictionAttributionState)
        => contradictionAttributionState switch
        {
            "override_route_failure" => "override_route_regressed",
            "default_route_failure" => "default_route_regressed",
            "mixed_or_ambiguous" => "mixed_contradiction_evidence",
            _ => "no_active_suspension"
        };

    private static string DetermineBuilderDefaultPolicyState(
        BuilderModelTrustBandEntry entry,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan,
        BuilderComparativeProofRun? comparativeRun,
        BuilderRoutingPolicyEvidence? routingPolicyEvidence,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome)
    {
        var isFeaturedTarget =
            (string.Equals(entry.ProofScope, routingRecommendation.FeaturedProofScope, StringComparison.Ordinal) &&
             string.Equals(entry.TargetId, routingRecommendation.FeaturedTargetId, StringComparison.Ordinal)) ||
            (string.Equals(entry.ProofScope, escalationDecision.ProofScope, StringComparison.Ordinal) &&
             string.Equals(entry.TargetId, escalationDecision.TargetId, StringComparison.Ordinal)) ||
            (string.Equals(entry.ProofScope, tieredRoutingPolicy?.ProofScope, StringComparison.Ordinal) &&
             string.Equals(entry.TargetId, tieredRoutingPolicy?.TargetId, StringComparison.Ordinal));
        if (isFeaturedTarget)
        {
            if (string.Equals(splitOutcome?.ClosureClassification, "split_equal_to_stronger_tier", StringComparison.Ordinal) ||
                string.Equals(splitOutcome?.ClosureClassification, "split_closed_gap", StringComparison.Ordinal) ||
                string.Equals(tieredRoutingPolicy?.PrimaryRoutingState, "split_first_keep_low_floor", StringComparison.Ordinal) ||
                string.Equals(splitPlan?.SplitRecommendationState, "split_first_keep_low_floor", StringComparison.Ordinal) ||
                string.Equals(routingPolicyEvidence?.RoutingPolicyState, "split_first_keep_low_floor", StringComparison.Ordinal))
            {
                return "split_first_low_floor";
            }

            if (string.Equals(tieredRoutingPolicy?.StrongerTierRecommendationState, "cleaner_not_required", StringComparison.Ordinal) &&
                string.Equals(comparativeRun?.ComparativeClassification, "cleaner_success", StringComparison.Ordinal))
            {
                return "stronger_tier_optional";
            }

            if (string.Equals(tieredRoutingPolicy?.PrimaryRoutingState, "escalate_for_cleaner_success", StringComparison.Ordinal) ||
                string.Equals(escalationDecision.EscalationRequirementState, "stronger_model_recommended", StringComparison.Ordinal) ||
                string.Equals(entry.RecommendationState, "stronger_model_recommended", StringComparison.Ordinal))
            {
                return "stronger_tier_recommended";
            }

            if (string.Equals(tieredRoutingPolicy?.PrimaryRoutingState, "escalate_because_low_floor_out_of_scope", StringComparison.Ordinal) ||
                string.Equals(escalationDecision.EscalationRequirementState, "stronger_model_required", StringComparison.Ordinal))
            {
                return "stronger_tier_required";
            }
        }

        return entry.TrustBand switch
        {
            "repair_loop_band" => "low_floor_with_repair_loop_expected",
            "escalation_recommended_band" => "stronger_tier_recommended",
            "reject_band" => "stronger_tier_required",
            _ => "direct_low_floor"
        };
    }

    private static string ResolveBuilderDefaultPolicyWeakSpot(
        string targetId,
        BuilderModelTrustBands trustBands,
        BuilderModelEscalationDecision escalationDecision)
    {
        if (string.Equals(targetId, escalationDecision.TargetId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(escalationDecision.PrimaryWeakSpot))
        {
            return escalationDecision.PrimaryWeakSpot;
        }

        return trustBands.WeakSpots.FirstOrDefault(weakSpot => weakSpot.TargetIds.Contains(targetId, StringComparer.Ordinal))?.WeakSpot ?? string.Empty;
    }

    private static IReadOnlyList<string> BuildBuilderDefaultPolicyReasons(
        BuilderModelTrustBandEntry entry,
        string policyState,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan,
        BuilderComparativeProofRun? comparativeRun,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome,
        string weakSpot)
    {
        var reasons = new List<string>(entry.Reasons)
        {
            BuildBuilderRequestComplexitySummary(entry.ComplexityDimensions)
        };

        if (!string.IsNullOrWhiteSpace(weakSpot))
        {
            reasons.Add($"{BuildWeakSpotLabel(weakSpot)} is the linked weak-spot for this bounded task.");
        }

        switch (policyState)
        {
            case "split_first_low_floor":
                var splitClosure = splitOutcome?.ClosureClassification ?? string.Empty;
                reasons.Add(string.Equals(splitClosure, "split_equal_to_stronger_tier", StringComparison.Ordinal) ||
                            string.Equals(splitClosure, "split_closed_gap", StringComparison.Ordinal)
                    ? $"Split execution closed the gap with outcome {splitClosure}."
                    : $"Comparative routing evidence keeps {entry.TargetLabel} on the split-first low-floor path.");
                break;
            case "low_floor_with_repair_loop_expected":
                reasons.Add("Recorded proof evidence required bounded repair-loop help before the task passed.");
                break;
            case "stronger_tier_optional":
                reasons.Add($"Comparative proof showed {comparativeRun?.ComparativeClassification ?? "cleaner_success"}, so the stronger tier is optional cleaner speed rather than a requirement.");
                break;
            case "stronger_tier_recommended":
                reasons.Add($"Routing guidance recommends the stronger tier because {TrimSentenceForReason(routingPlan.ReasonForEscalation)}.");
                break;
            case "stronger_tier_required":
                reasons.Add($"Escalation remains required because {TrimSentenceForReason(escalationDecision.ReasonForEscalation)}.");
                break;
            default:
                reasons.Add($"Proof evidence keeps {entry.TargetLabel} inside the direct low-floor lane.");
                break;
        }

        if (string.Equals(entry.ProofScope, routingRecommendation.FeaturedProofScope, StringComparison.Ordinal) &&
            string.Equals(entry.TargetId, routingRecommendation.FeaturedTargetId, StringComparison.Ordinal) &&
            tieredRoutingPolicy is not null)
        {
            reasons.Add($"Primary routing state recorded: {tieredRoutingPolicy.PrimaryRoutingState}.");
        }

        return reasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildBuilderDefaultPolicyEvidencePaths(
        string runFolder,
        BuilderModelTrustBandEntry entry,
        BuilderModelRoutingRecommendation routingRecommendation,
        BuilderModelEscalationDecision escalationDecision,
        BuilderModelRoutingPlan routingPlan,
        BuilderComparativeProofRun? comparativeRun,
        BuilderRoutingPolicyEvidence? routingPolicyEvidence,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome)
    {
        var paths = new List<string>(entry.EvidencePaths)
        {
            BuilderProofRunArtifactPath(runFolder),
            BuilderModelTrustBandsPath(runFolder)
        };

        if (string.Equals(entry.ProofScope, routingRecommendation.FeaturedProofScope, StringComparison.Ordinal) &&
            string.Equals(entry.TargetId, routingRecommendation.FeaturedTargetId, StringComparison.Ordinal))
        {
            paths.Add(BuilderModelRoutingRecommendationPath(runFolder));
            paths.Add(BuilderModelEscalationDecisionPath(runFolder));
            paths.Add(BuilderModelRoutingPlanPath(runFolder));
            if (comparativeRun is not null)
            {
                paths.Add(comparativeRun.ArtifactPath);
                paths.Add(comparativeRun.SummaryArtifactPath);
            }

            if (routingPolicyEvidence is not null)
            {
                paths.Add(routingPolicyEvidence.ArtifactPath);
            }

            if (splitPlan is not null)
            {
                paths.Add(splitPlan.ArtifactPath);
            }

            if (tieredRoutingPolicy is not null)
            {
                paths.Add(tieredRoutingPolicy.ArtifactPath);
            }

            if (splitOutcome is not null)
            {
                paths.Add(splitOutcome.ArtifactPath);
            }
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildBuilderDefaultPolicyEntrySummary(
        BuilderModelTrustBandEntry entry,
        string policyState,
        string weakSpot,
        IReadOnlyList<string> reasons)
    {
        var weakSpotText = string.IsNullOrWhiteSpace(weakSpot)
            ? string.Empty
            : $" Weak-spot focus: {BuildWeakSpotLabel(weakSpot)}.";
        var lead = policyState switch
        {
            "split_first_low_floor" => $"{entry.TargetLabel} should default to split-first low-floor execution.",
            "low_floor_with_repair_loop_expected" => $"{entry.TargetLabel} stays on the low-floor model with bounded repair-loop help expected.",
            "stronger_tier_optional" => $"{entry.TargetLabel} can stay on the low-floor path, but the stronger tier is optional for cleaner success.",
            "stronger_tier_recommended" => $"{entry.TargetLabel} should surface a stronger-tier recommendation before spending more low-floor effort.",
            "stronger_tier_required" => $"{entry.TargetLabel} is beyond the low-floor default and should route upward.",
            _ => $"{entry.TargetLabel} remains in the direct low-floor default lane."
        };
        var evidence = reasons.FirstOrDefault() ?? "No additional evidence was recorded.";
        return $"{lead} {evidence}{weakSpotText}";
    }

    private static string[] CollectBuilderDefaultPolicyTaskClasses(
        IReadOnlyList<BuilderDefaultPolicyTaskClassEntry> entries,
        string state)
        => entries
            .Where(entry => string.Equals(entry.PolicyState, state, StringComparison.Ordinal))
            .Select(entry => entry.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string BuildBuilderDefaultPolicySummary(
        string modelId,
        IReadOnlyList<string> inBandTaskClasses,
        IReadOnlyList<string> splitFirstTaskClasses,
        IReadOnlyList<string> repairLoopTaskClasses,
        IReadOnlyList<string> strongerTierOptionalTaskClasses,
        IReadOnlyList<string> strongerTierRecommendedTaskClasses,
        IReadOnlyList<string> strongerTierRequiredTaskClasses,
        BuilderSplitFirstOutcome? splitOutcome)
    {
        var segments = new List<string>
        {
            $"{modelId} remains the default builder model for {inBandTaskClasses.Count} direct bounded task class(es)."
        };

        if (splitFirstTaskClasses.Count > 0)
        {
            var splitSummary = string.Equals(splitOutcome?.ClosureClassification, "split_equal_to_stronger_tier", StringComparison.Ordinal)
                ? $"Split-first is now the default for {string.Join(", ", splitFirstTaskClasses)} because split execution matched the stronger tier."
                : $"Split-first is the default for {string.Join(", ", splitFirstTaskClasses)} based on comparative proof evidence.";
            segments.Add(splitSummary);
        }

        if (repairLoopTaskClasses.Count > 0)
        {
            segments.Add($"Bounded repair-loop help is still expected for {string.Join(", ", repairLoopTaskClasses)}.");
        }

        if (strongerTierOptionalTaskClasses.Count > 0)
        {
            segments.Add($"The stronger tier is optional cleaner speed for {string.Join(", ", strongerTierOptionalTaskClasses)}.");
        }

        if (strongerTierRecommendedTaskClasses.Count > 0)
        {
            segments.Add($"The stronger tier should be recommended for {string.Join(", ", strongerTierRecommendedTaskClasses)}.");
        }

        if (strongerTierRequiredTaskClasses.Count > 0)
        {
            segments.Add($"The stronger tier is still required for {string.Join(", ", strongerTierRequiredTaskClasses)}.");
        }

        return string.Join(" ", segments);
    }

    private static string DetermineBuilderRequestStrongerTierDisposition(
        string policyState,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome)
        => policyState switch
        {
            "stronger_tier_required" => "required",
            "stronger_tier_recommended" => "recommended",
            "stronger_tier_optional" => "optional_for_cleaner_success",
            "split_first_low_floor" when string.Equals(tieredRoutingPolicy?.StrongerTierRecommendationState, "cleaner_not_required", StringComparison.Ordinal) ||
                                          string.Equals(splitOutcome?.ClosureClassification, "split_equal_to_stronger_tier", StringComparison.Ordinal) =>
                "optional_for_cleaner_success",
            _ => "not_needed"
        };

    private static string BuildBuilderRequestStrongerTierSummary(
        string strongerTierDisposition,
        BuilderModelRoutingPlan routingPlan,
        BuilderSplitFirstOutcome? splitOutcome)
        => strongerTierDisposition switch
        {
            "required" => $"Stronger-tier escalation remains required because {TrimSentenceForReason(routingPlan.ReasonForEscalation)}.",
            "recommended" => $"Stronger-tier escalation is recommended because {TrimSentenceForReason(routingPlan.ReasonForEscalation)}.",
            "optional_for_cleaner_success" when string.Equals(splitOutcome?.ClosureClassification, "split_equal_to_stronger_tier", StringComparison.Ordinal) =>
                "The stronger tier is optional cleaner speed only; split-first low-floor execution already matched it in proof.",
            "optional_for_cleaner_success" => "The stronger tier is optional cleaner speed only; current proof does not require it.",
            _ => "The current proof does not require stronger-tier escalation for this bounded request."
        };

    private static BuilderModelRoutingStability BuildBuilderModelRoutingStability(
        string repoRoot,
        BuilderDefaultPolicy currentPolicy)
    {
        var history = LoadBuilderDefaultPolicyHistory(repoRoot);
        var entries = currentPolicy.TaskClassEntries
            .OrderBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .Select(entry =>
            {
                var matchingEntries = history.Entries
                    .SelectMany(historyEntry => historyEntry.TaskClassEntries.Select(taskEntry => new
                    {
                        historyEntry.ArtifactPath,
                        historyEntry.ObservedUtc,
                        TaskEntry = taskEntry
                    }))
                    .Where(candidate =>
                        string.Equals(candidate.TaskEntry.ProofScope, entry.ProofScope, StringComparison.Ordinal) &&
                        string.Equals(candidate.TaskEntry.TaskClass, entry.TaskClass, StringComparison.Ordinal))
                    .OrderByDescending(candidate => candidate.ObservedUtc)
                    .ThenByDescending(candidate => candidate.ArtifactPath, StringComparer.Ordinal)
                    .ToArray();
                var supporting = matchingEntries
                    .Where(candidate => string.Equals(candidate.TaskEntry.PolicyState, entry.PolicyState, StringComparison.Ordinal))
                    .ToArray();
                var contradictions = matchingEntries
                    .Where(candidate => !string.Equals(candidate.TaskEntry.PolicyState, entry.PolicyState, StringComparison.Ordinal))
                    .ToArray();
                var stabilityState = contradictions.Length > 0
                    ? "contradicted"
                    : supporting.Length >= 3
                        ? "stable"
                        : supporting.Length >= 2
                            ? "corroborated"
                            : "provisional";
                var corroboratingArtifacts = supporting
                    .Select(candidate => candidate.ArtifactPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .Take(3)
                    .ToArray();
                var summary = $"{entry.TaskClass} is {stabilityState} for {entry.PolicyState} across {supporting.Length} supporting policy snapshot(s) with {contradictions.Length} contradiction(s).";
                return new BuilderModelRoutingStabilityEntry(
                    entry.TaskClass,
                    entry.PolicyState,
                    stabilityState,
                    supporting.Length,
                    contradictions.Length,
                    corroboratingArtifacts,
                    summary);
            })
            .ToArray();
        var stableCount = entries.Count(entry => string.Equals(entry.StabilityState, "stable", StringComparison.Ordinal));
        var corroboratedCount = entries.Count(entry => string.Equals(entry.StabilityState, "corroborated", StringComparison.Ordinal));
        var provisionalCount = entries.Count(entry => string.Equals(entry.StabilityState, "provisional", StringComparison.Ordinal));
        var contradictedCount = entries.Count(entry => string.Equals(entry.StabilityState, "contradicted", StringComparison.Ordinal));
        var summary = $"Model routing stability: stable={stableCount}, corroborated={corroboratedCount}, provisional={provisionalCount}, contradicted={contradictedCount}.";
        return new BuilderModelRoutingStability(
            currentPolicy.SourceProofRunId,
            currentPolicy.CurrentModelId,
            entries,
            summary,
            BuilderModelRoutingStabilityPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelCapabilityMatrix BuildBuilderModelCapabilityMatrix(
        string repoRoot,
        BuilderDefaultPolicy defaultPolicy,
        BuilderModelRoutingStability stability)
    {
        var stabilityByTaskClass = stability.Entries
            .GroupBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var entries = defaultPolicy.TaskClassEntries
            .OrderBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .Select(entry =>
            {
                stabilityByTaskClass.TryGetValue(entry.TaskClass, out var stabilityEntry);
                var capabilityState = DetermineBuilderModelCapabilityState(entry.PolicyState);
                var routeClass = DetermineBuilderModelRouteClass(entry.PolicyState);
                var strongerTierRecommendationState = DetermineBuilderModelRecommendationState(entry.PolicyState);
                var strongerTierRequirementState = DetermineBuilderModelRequirementState(entry.PolicyState);
                var evidenceSupportLevel = stabilityEntry?.StabilityState ?? "provisional";
                var linkedArtifacts = entry.LinkedEvidencePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var summary = BuildBuilderModelCapabilityMatrixEntrySummary(
                    entry.TargetLabel,
                    capabilityState,
                    strongerTierRecommendationState,
                    strongerTierRequirementState,
                    evidenceSupportLevel);
                return new BuilderModelCapabilityMatrixEntry(
                    entry.TaskClass,
                    entry.ProofScope,
                    entry.TargetId,
                    entry.TargetLabel,
                    entry.PolicyState,
                    routeClass,
                    capabilityState,
                    DetermineBuilderSupportedLowFloorState(entry.PolicyState),
                    DetermineBuilderRepairLoopAcceptability(entry.PolicyState),
                    string.Equals(entry.PolicyState, "split_first_low_floor", StringComparison.Ordinal),
                    string.Equals(entry.PolicyState, "split_first_low_floor", StringComparison.Ordinal) ? "required" : "not_required",
                    strongerTierRecommendationState,
                    strongerTierRequirementState,
                    evidenceSupportLevel,
                    linkedArtifacts,
                    summary);
            })
            .ToArray();
        return new BuilderModelCapabilityMatrix(
            defaultPolicy.SourceProofRunId,
            defaultPolicy.CurrentModelId,
            "low_floor_model_tier",
            "stronger_builder_tier",
            entries,
            BuildBuilderModelCapabilityMatrixSummary(entries),
            BuilderModelCapabilityMatrixPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelRoutingPolicy BuildBuilderModelRoutingPolicyArtifact(
        string repoRoot,
        BuilderDefaultPolicy defaultPolicy,
        BuilderModelCapabilityMatrix matrix)
    {
        var strongerTierAvailability = LoadLatestBuilderStrongerTierAvailability(repoRoot);
        var preferredStrongerModelId = FirstNonEmpty(
            strongerTierAvailability?.ConfiguredStrongerTierId,
            strongerTierAvailability?.PreferredStrongerModelId,
            "stronger_builder_tier");
        var entries = matrix.Entries
            .OrderBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProofScope, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetId, StringComparer.Ordinal)
            .Select(entry =>
            {
                var preferredModelTier = DetermineBuilderPreferredModelTier(entry.PolicyState);
                var summary = BuildBuilderModelRoutingPolicyEntrySummary(entry);
                return new BuilderModelRoutingPolicyEntry(
                    entry.TaskClass,
                    entry.ProofScope,
                    entry.TargetId,
                    entry.TargetLabel,
                    entry.PolicyState,
                    entry.RouteClass,
                    preferredModelTier,
                    DetermineBuilderAllowedModelTiers(entry.PolicyState),
                    DetermineBuilderFallbackPath(entry.PolicyState),
                    entry.SplitFirstRequired,
                    entry.RepairLoopAcceptability,
                    entry.StrongerTierRecommendationState,
                    entry.StrongerTierRequirementState,
                    BuildBuilderModelEscalationTrigger(entry),
                    !string.Equals(entry.PolicyState, "stronger_tier_required", StringComparison.Ordinal),
                    entry.EvidenceSupportLevel,
                    entry.LinkedProofArtifactPaths,
                    summary);
            })
            .ToArray();
        return new BuilderModelRoutingPolicy(
            defaultPolicy.SourceProofRunId,
            defaultPolicy.CurrentModelId,
            "low_floor_model_tier",
            preferredStrongerModelId,
            entries,
            BuildBuilderModelRoutingPolicySummary(entries, strongerTierAvailability),
            BuilderModelRoutingPolicyPathForRepo(repoRoot),
            BuilderModelRoutingPolicySummaryPathForRepo(repoRoot),
            DateTimeOffset.UtcNow);
    }

    private static BuilderModelRoutingPolicyHistory BuildBuilderModelRoutingPolicyHistory(
        BuilderModelRoutingPolicyHistory priorHistory,
        BuilderModelRoutingPolicy priorPolicy,
        BuilderModelRoutingPolicy currentPolicy)
    {
        var retentionCount = Math.Max(priorHistory.RetentionCount, 20);
        var priorEntries = priorPolicy.Entries
            .GroupBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var newEntries = currentPolicy.Entries
            .Select(entry =>
            {
                priorEntries.TryGetValue(entry.TaskClass, out var priorEntry);
                var priorPolicyState = priorEntry?.PolicyState ?? "not_recorded";
                var changed = priorEntry is null ||
                              !string.Equals(priorEntry.PolicyState, entry.PolicyState, StringComparison.Ordinal) ||
                              !string.Equals(priorEntry.PreferredModelTier, entry.PreferredModelTier, StringComparison.Ordinal) ||
                              !string.Equals(priorEntry.EvidenceSupportLevel, entry.EvidenceSupportLevel, StringComparison.Ordinal);
                if (!changed)
                {
                    return null;
                }

                var evidenceChangeSummary = priorEntry is null
                    ? $"Initialized {entry.TaskClass} at {entry.PolicyState} with support {entry.EvidenceSupportLevel}."
                    : $"{entry.TaskClass} moved from {priorEntry.PolicyState} to {entry.PolicyState}; support {priorEntry.EvidenceSupportLevel} -> {entry.EvidenceSupportLevel}.";
                return new BuilderModelRoutingPolicyHistoryEntry(
                    entry.TaskClass,
                    priorPolicyState,
                    entry.PolicyState,
                    evidenceChangeSummary,
                    currentPolicy.ArtifactPath,
                    currentPolicy.ObservedUtc);
            })
            .Where(entry => entry is not null)
            .Cast<BuilderModelRoutingPolicyHistoryEntry>()
            .ToArray();
        var entries = priorHistory.Entries
            .Where(existing => !newEntries.Any(candidate =>
                string.Equals(candidate.TaskClass, existing.TaskClass, StringComparison.Ordinal) &&
                candidate.ObservedUtc.Equals(existing.ObservedUtc)))
            .Concat(newEntries)
            .OrderByDescending(entry => entry.ObservedUtc)
            .ThenBy(entry => entry.TaskClass, StringComparer.Ordinal)
            .Take(retentionCount)
            .ToArray();
        var summary = newEntries.Length == 0
            ? "No builder model routing policy changes were recorded."
            : $"Recorded {newEntries.Length} builder model routing policy change(s).";
        return new BuilderModelRoutingPolicyHistory(
            retentionCount,
            entries,
            summary,
            FirstNonEmpty(
                priorHistory.ArtifactPath,
                Path.Combine(Path.GetDirectoryName(currentPolicy.ArtifactPath) ?? string.Empty, "builder_model_routing_policy_history.json")),
            currentPolicy.ObservedUtc);
    }

    private static string BuildBuilderModelRoutingPolicySummaryMarkdown(BuilderModelRoutingPolicy policy)
    {
        var direct = policy.Entries
            .Where(entry => string.Equals(entry.PolicyState, "direct_low_floor", StringComparison.Ordinal))
            .Select(entry => entry.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(taskClass => taskClass, StringComparer.Ordinal)
            .ToArray();
        var splitFirst = policy.Entries
            .Where(entry => string.Equals(entry.PolicyState, "split_first_low_floor", StringComparison.Ordinal))
            .Select(entry => entry.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(taskClass => taskClass, StringComparer.Ordinal)
            .ToArray();
        var repairLoop = policy.Entries
            .Where(entry => string.Equals(entry.PolicyState, "low_floor_with_repair_loop_expected", StringComparison.Ordinal))
            .Select(entry => entry.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(taskClass => taskClass, StringComparer.Ordinal)
            .ToArray();
        var recommended = policy.Entries
            .Where(entry => string.Equals(entry.PolicyState, "stronger_tier_recommended", StringComparison.Ordinal) ||
                            string.Equals(entry.PolicyState, "stronger_tier_optional", StringComparison.Ordinal))
            .Select(entry => entry.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(taskClass => taskClass, StringComparer.Ordinal)
            .ToArray();
        var required = policy.Entries
            .Where(entry => string.Equals(entry.PolicyState, "stronger_tier_required", StringComparison.Ordinal))
            .Select(entry => entry.TaskClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(taskClass => taskClass, StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# Builder Model Routing Policy");
        builder.AppendLine();
        builder.AppendLine($"- Low-floor direct: {(direct.Length == 0 ? "none recorded" : string.Join(", ", direct))}");
        builder.AppendLine($"- Low-floor split-first: {(splitFirst.Length == 0 ? "none recorded" : string.Join(", ", splitFirst))}");
        builder.AppendLine($"- Low-floor with repair loop: {(repairLoop.Length == 0 ? "none recorded" : string.Join(", ", repairLoop))}");
        builder.AppendLine($"- Stronger tier recommended or optional: {(recommended.Length == 0 ? "none recorded" : string.Join(", ", recommended))}");
        builder.AppendLine($"- Stronger tier required: {(required.Length == 0 ? "none recorded" : string.Join(", ", required))}");
        builder.AppendLine();
        builder.AppendLine(policy.Summary);
        return builder.ToString().TrimEnd();
    }

    private static string DetermineBuilderModelCapabilityState(string policyState)
        => policyState switch
        {
            "direct_low_floor" or "stronger_tier_optional" => "low_floor_direct_supported",
            "split_first_low_floor" => "low_floor_split_first_supported",
            "low_floor_with_repair_loop_expected" => "low_floor_supported_with_repair_loop",
            "stronger_tier_recommended" => "stronger_tier_recommended",
            "stronger_tier_required" => "stronger_tier_required",
            _ => "not_yet_proven"
        };

    private static string DetermineBuilderSupportedLowFloorState(string policyState)
        => policyState switch
        {
            "direct_low_floor" => "supported",
            "split_first_low_floor" => "supported_after_split_first",
            "low_floor_with_repair_loop_expected" => "supported_with_repair_loop",
            "stronger_tier_optional" => "supported_optional_cleaner_stronger_tier",
            "stronger_tier_recommended" => "supported_but_not_preferred",
            "stronger_tier_required" => "not_supported",
            _ => "not_yet_proven"
        };

    private static string DetermineBuilderRepairLoopAcceptability(string policyState)
        => policyState switch
        {
            "low_floor_with_repair_loop_expected" => "accepted_if_bounded",
            "stronger_tier_recommended" => "possible_but_costly",
            "stronger_tier_required" => "not_acceptable",
            _ => "not_expected"
        };

    private static string DetermineBuilderModelRecommendationState(string policyState)
        => policyState switch
        {
            "stronger_tier_optional" => "optional_for_cleaner_success",
            "stronger_tier_recommended" => "recommended",
            "stronger_tier_required" => "required",
            _ => "not_needed"
        };

    private static string DetermineBuilderModelRequirementState(string policyState)
        => string.Equals(policyState, "stronger_tier_required", StringComparison.Ordinal)
            ? "required"
            : string.Equals(policyState, "stronger_tier_recommended", StringComparison.Ordinal)
                ? "recommended"
                : "not_required";

    private static string DetermineBuilderModelRouteClass(string policyState)
        => policyState switch
        {
            "direct_low_floor" => "direct_low_floor_route",
            "split_first_low_floor" => "split_first_low_floor_route",
            "low_floor_with_repair_loop_expected" => "low_floor_with_repair_loop_route",
            "stronger_tier_optional" => "current_model_with_optional_stronger_tier_route",
            "stronger_tier_recommended" => "stronger_tier_recommended_route",
            "stronger_tier_required" => "task_out_of_scope_route",
            _ => "task_out_of_scope_route"
        };

    private static string DetermineBuilderPreferredModelTier(string policyState)
        => policyState switch
        {
            "stronger_tier_recommended" or "stronger_tier_required" => "stronger_builder_tier",
            _ => "low_floor_model_tier"
        };

    private static IReadOnlyList<string> DetermineBuilderAllowedModelTiers(string policyState)
        => policyState switch
        {
            "stronger_tier_required" => new[] { "stronger_builder_tier" },
            "stronger_tier_recommended" or "stronger_tier_optional" => new[] { "low_floor_model_tier", "stronger_builder_tier" },
            _ => new[] { "low_floor_model_tier" }
        };

    private static string DetermineBuilderFallbackPath(string policyState)
        => policyState switch
        {
            "split_first_low_floor" => "split_first_low_floor_route",
            "low_floor_with_repair_loop_expected" => "low_floor_with_repair_loop_route",
            "stronger_tier_optional" => "current_model_with_optional_stronger_tier_route",
            "stronger_tier_recommended" => "direct_low_floor_route_with_operator_acknowledged_repair_burden",
            "stronger_tier_required" => "blocked_until_supported_stronger_tier_is_available",
            _ => "direct_low_floor_route"
        };

    private static string BuildBuilderModelEscalationTrigger(BuilderModelCapabilityMatrixEntry entry)
        => entry.PolicyState switch
        {
            "split_first_low_floor" => "split-first proof kept the low-floor path viable",
            "low_floor_with_repair_loop_expected" => "proof required repair-loop recovery inside the bounded scope",
            "stronger_tier_optional" => "comparative proof showed the stronger tier as optional cleaner speed",
            "stronger_tier_recommended" => "comparative proof showed a cleaner stronger-tier path",
            "stronger_tier_required" => "proof recorded the task outside the low-floor envelope",
            _ => "proof kept the task inside the direct low-floor lane"
        };

    private static string BuildBuilderModelCapabilityMatrixEntrySummary(
        string targetLabel,
        string capabilityState,
        string strongerTierRecommendationState,
        string strongerTierRequirementState,
        string evidenceSupportLevel)
        => $"{targetLabel} is {capabilityState}. Stronger tier recommendation={strongerTierRecommendationState}. Requirement={strongerTierRequirementState}. Evidence={evidenceSupportLevel}.";

    private static string BuildBuilderModelCapabilityMatrixSummary(
        IReadOnlyList<BuilderModelCapabilityMatrixEntry> entries)
    {
        var direct = entries.Count(entry => string.Equals(entry.CapabilityState, "low_floor_direct_supported", StringComparison.Ordinal));
        var split = entries.Count(entry => string.Equals(entry.CapabilityState, "low_floor_split_first_supported", StringComparison.Ordinal));
        var repair = entries.Count(entry => string.Equals(entry.CapabilityState, "low_floor_supported_with_repair_loop", StringComparison.Ordinal));
        var recommended = entries.Count(entry => string.Equals(entry.CapabilityState, "stronger_tier_recommended", StringComparison.Ordinal));
        var required = entries.Count(entry => string.Equals(entry.CapabilityState, "stronger_tier_required", StringComparison.Ordinal));
        return $"Builder model capability matrix: direct={direct}, split-first={split}, repair-loop={repair}, stronger-tier recommended={recommended}, stronger-tier required={required}.";
    }

    private static string BuildBuilderModelRoutingPolicyEntrySummary(BuilderModelCapabilityMatrixEntry entry)
        => $"{entry.TaskClass} prefers {DetermineBuilderPreferredModelTier(entry.PolicyState)} on route {entry.RouteClass}. Fallback={DetermineBuilderFallbackPath(entry.PolicyState)}. Evidence={entry.EvidenceSupportLevel}.";

    private static string BuildBuilderModelRoutingPolicySummary(
        IReadOnlyList<BuilderModelRoutingPolicyEntry> entries,
        BuilderStrongerTierAvailability? strongerTierAvailability)
    {
        var direct = entries.Count(entry => string.Equals(entry.PolicyState, "direct_low_floor", StringComparison.Ordinal));
        var splitFirst = entries.Count(entry => string.Equals(entry.PolicyState, "split_first_low_floor", StringComparison.Ordinal));
        var repairLoop = entries.Count(entry => string.Equals(entry.PolicyState, "low_floor_with_repair_loop_expected", StringComparison.Ordinal));
        var recommended = entries.Count(entry => string.Equals(entry.PolicyState, "stronger_tier_optional", StringComparison.Ordinal) ||
                                                 string.Equals(entry.PolicyState, "stronger_tier_recommended", StringComparison.Ordinal));
        var required = entries.Count(entry => string.Equals(entry.PolicyState, "stronger_tier_required", StringComparison.Ordinal));
        var strongerTierState = strongerTierAvailability?.AvailabilityState ?? "unknown";
        return $"Builder model routing policy keeps {direct} task class(es) on direct low floor, {splitFirst} on split-first low floor, {repairLoop} with repair-loop expectation, {recommended} with stronger-tier recommendation, and {required} requiring stronger tier. Stronger-tier availability={strongerTierState}.";
    }

    private static string BuildBuilderRequestComplexitySummary(BuilderProofComplexityDimensions dimensions)
        => $"Complexity stays bounded at files={dimensions.FileCountTouched}, projects={dimensions.ProjectCountTouched}, dependency_changes={dimensions.DependencyReferenceChangeCount}, test_changes={dimensions.TestChangesRequired.ToString().ToLowerInvariant()}, new_files={dimensions.NewFileCreationCount}, prompt_ambiguity={dimensions.PromptAmbiguity}.";

    private static string TrimSentenceForReason(string value)
        => (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static string SanitizeBuilderProofToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static T TryLoadBuilderProofArtifact<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            return JsonSerializer.Deserialize<T>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? fallback;
        }
        catch
        {
            return fallback;
        }
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

    private static string ResolveBuilderGitDirectory(string repoRoot)
    {
        var gitPath = Path.Combine(repoRoot, ".git");
        if (Directory.Exists(gitPath))
        {
            return gitPath;
        }

        if (!File.Exists(gitPath))
        {
            return string.Empty;
        }

        var contents = File.ReadAllText(gitPath).Trim();
        if (!contents.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var gitDirectory = contents["gitdir:".Length..].Trim();
        return Path.IsPathRooted(gitDirectory)
            ? gitDirectory
            : Path.GetFullPath(Path.Combine(repoRoot, gitDirectory));
    }

    private static string ReadBuilderGitBranchName(string gitDirectory)
    {
        if (string.IsNullOrWhiteSpace(gitDirectory))
        {
            return string.Empty;
        }

        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            return string.Empty;
        }

        var head = File.ReadAllText(headPath).Trim();
        if (string.IsNullOrWhiteSpace(head))
        {
            return string.Empty;
        }

        if (!head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
        {
            return "detached_head";
        }

        var reference = head["ref:".Length..].Trim();
        var branchName = reference.Replace('\\', '/');
        const string headsPrefix = "refs/heads/";
        return branchName.StartsWith(headsPrefix, StringComparison.OrdinalIgnoreCase)
            ? branchName[headsPrefix.Length..]
            : branchName;
    }

    private sealed class DefaultBuilderGitReadinessProbe : IBuilderGitReadinessProbe
    {
        public BuilderGitReadinessObservation Probe(string repoRoot)
        {
            var observedUtc = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            {
                return new BuilderGitReadinessObservation(
                    false,
                    string.Empty,
                    false,
                    false,
                    "unknown",
                    "blocked_git_missing_repo",
                    new[] { "No Git repository was detected for the approved patch handoff." },
                    observedUtc);
            }

            try
            {
                var normalizedRepoRoot = Path.GetFullPath(repoRoot);
                var gitDirectory = ResolveBuilderGitDirectory(normalizedRepoRoot);
                if (string.IsNullOrWhiteSpace(gitDirectory) || !Directory.Exists(gitDirectory))
                {
                    return new BuilderGitReadinessObservation(
                        false,
                        string.Empty,
                        false,
                        false,
                        "unknown",
                        "blocked_git_missing_repo",
                        new[] { "No Git repository was detected for the approved patch handoff." },
                        observedUtc);
                }

                var fallbackBranchName = ReadBuilderGitBranchName(gitDirectory);
                return new BuilderGitReadinessObservation(
                    true,
                    fallbackBranchName,
                    false,
                    false,
                    "unknown",
                    "blocked_git_unknown_state",
                    new[] { "Git repository detected, but working tree cleanliness could not be verified safely from repo files alone." },
                    observedUtc);
            }
            catch (Exception ex)
            {
                return new BuilderGitReadinessObservation(
                    true,
                    string.Empty,
                    false,
                    false,
                    "unknown",
                    "blocked_git_unknown_state",
                    new[] { FirstNonEmpty(ex.Message, "Git readiness probe failed while reading repository state.") },
                    observedUtc);
            }
        }
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

    private sealed record BuilderRouteContinuitySnapshot(
        string SourceProofRunId,
        string SourceRunFolder,
        string TaskClass,
        string DefaultRoute,
        string PreparedRoute,
        string RouteSourceState,
        string CurrentReadinessState,
        string ContradictionAttributionState,
        string ReconfirmationState,
        bool DefaultRouteSuspended,
        string ContinuityState,
        IReadOnlyList<BuilderRouteArtifactReference> AvailableArtifacts,
        string Summary,
        DateTimeOffset ObservedUtc);

    private sealed record BuilderRepoToolchainPolicySnapshot(
        bool HasDotNetProjects,
        bool HasWpfProjects,
        string PreferredStackId,
        string PreferredStackLabel,
        string Summary);

    private sealed record BuilderLanguageStackDefinition(
        string StackId,
        string StackLabel,
        IReadOnlyList<string> RequiredToolIds,
        string ToolchainRequirementSummary);

    private sealed record BuilderParsedRepoProject(
        string ProjectId,
        string ProjectName,
        string AbsolutePath,
        string RelativePath,
        string ProjectType,
        string InferredStackLabel,
        IReadOnlyList<string> TargetFrameworks,
        bool UseWpf,
        bool IsTestProject,
        IReadOnlyList<string> ProjectReferencePaths);

    private sealed record BuilderReviewableFileSnapshotEntry(
        string RelativePath,
        string ContentHash,
        long FileLength,
        string SnapshotPath);
}

public interface IBuilderProofCommandRunner
{
    Task<BuilderProofCommandExecutionResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string logPath,
        CancellationToken ct);
}

public interface IBuilderStrongerTierResolver
{
    Task<BuilderStrongerTierAvailability> ResolveAsync(
        string currentModelId,
        string recommendedModelClass,
        string? preferredStrongerModelId,
        string provider,
        CancellationToken ct);
}

public sealed class NullBuilderStrongerTierResolver : IBuilderStrongerTierResolver
{
    public Task<BuilderStrongerTierAvailability> ResolveAsync(
        string currentModelId,
        string recommendedModelClass,
        string? preferredStrongerModelId,
        string provider,
        CancellationToken ct)
        => Task.FromResult(new BuilderStrongerTierAvailability(
            currentModelId,
            recommendedModelClass,
            preferredStrongerModelId ?? string.Empty,
            string.Empty,
            "unconfigured",
            "No stronger-tier resolver is configured for bounded builder proof escalation.",
            provider,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            "No stronger-tier resolver is configured for bounded builder proof escalation.",
            Array.Empty<string>(),
            "No stronger-tier resolver is configured for bounded builder proof escalation.",
            string.Empty,
            DateTimeOffset.UtcNow));
}

public sealed class OllamaBuilderStrongerTierResolver : IBuilderStrongerTierResolver
{
    private static readonly string[] PreferredCandidates =
    {
        "qwen2.5:7b-instruct",
        "qwen2.5:14b-instruct",
        "qwen2.5:32b-instruct",
        "qwen3:8b-instruct",
        "llama3.1:8b-instruct",
        "llama3.2:3b-instruct",
        "mistral:7b-instruct"
    };

    private readonly IOllamaClient _ollamaClient;
    private readonly string _endpoint;

    public OllamaBuilderStrongerTierResolver(IOllamaClient ollamaClient, string? endpoint = null)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? EndpointResolver.ResolveOllamaEndpoint() : endpoint.Trim();
    }

    public async Task<BuilderStrongerTierAvailability> ResolveAsync(
        string currentModelId,
        string recommendedModelClass,
        string? preferredStrongerModelId,
        string provider,
        CancellationToken ct)
    {
        if (!string.Equals(recommendedModelClass, "stronger_builder_tier", StringComparison.Ordinal))
        {
            return new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                string.Empty,
                "not_needed",
                "The latest routing state does not require a stronger-tier comparison path.",
                provider,
                _endpoint,
                string.Empty,
                Array.Empty<string>(),
                "No stronger-tier resolution was required for the latest routing state.",
                Array.Empty<string>(),
                "No stronger-tier resolution was required for the latest routing state.",
                string.Empty,
                DateTimeOffset.UtcNow);
        }

        if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                string.Empty,
                "unconfigured",
                $"Stronger-tier resolution is only implemented for the Ollama-backed builder path in this phase. Current provider: {provider}.",
                provider,
                _endpoint,
                string.Empty,
                Array.Empty<string>(),
                "No supported stronger-tier resolver was available for the current builder provider.",
                Array.Empty<string>(),
                "No supported stronger-tier resolver was available for the current builder provider.",
                string.Empty,
                DateTimeOffset.UtcNow);
        }

        var tags = await _ollamaClient.GetTagsAsync(ct).ConfigureAwait(false);
        var availableModelIds = tags.ModelNames
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
            .OrderBy(modelId => modelId, StringComparer.Ordinal)
            .ToArray();
        if (!tags.IsSuccess)
        {
            return new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                string.Empty,
                "unavailable",
                tags.Summary ?? "Could not resolve the stronger-tier Ollama model.",
                provider,
                _endpoint,
                tags.ErrorCode ?? string.Empty,
                availableModelIds,
                tags.Summary ?? "Could not resolve the stronger-tier Ollama model.",
                Array.Empty<string>(),
                tags.Summary ?? "Could not resolve the stronger-tier Ollama model.",
                string.Empty,
                DateTimeOffset.UtcNow);
        }

        if (TrySelectCandidateModel(availableModelIds, currentModelId, preferredStrongerModelId, out var selectedModelId))
        {
            return new BuilderStrongerTierAvailability(
                currentModelId,
                recommendedModelClass,
                preferredStrongerModelId ?? string.Empty,
                selectedModelId,
                "available",
                $"{selectedModelId} is available for bounded stronger-tier builder proof comparisons.",
                provider,
                _endpoint,
                string.Empty,
                availableModelIds,
                $"Resolved {selectedModelId} from the bounded stronger-tier candidate set.",
                Array.Empty<string>(),
                $"{selectedModelId} is available for bounded stronger-tier builder proof comparisons.",
                string.Empty,
                DateTimeOffset.UtcNow);
        }

        return new BuilderStrongerTierAvailability(
            currentModelId,
            recommendedModelClass,
            preferredStrongerModelId ?? string.Empty,
            preferredStrongerModelId?.Trim() ?? string.Empty,
            "unavailable",
            "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            provider,
            _endpoint,
            string.Empty,
            availableModelIds,
            "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            Array.Empty<string>(),
            "No stronger-tier model matching the bounded builder candidate list is currently available in Ollama.",
            string.Empty,
            DateTimeOffset.UtcNow);
    }

    private static bool TrySelectCandidateModel(
        IReadOnlyList<string> availableModelIds,
        string currentModelId,
        string? preferredStrongerModelId,
        out string selectedModelId)
    {
        selectedModelId = string.Empty;
        if (!string.IsNullOrWhiteSpace(preferredStrongerModelId))
        {
            var preferred = preferredStrongerModelId.Trim();
            if (!string.Equals(preferred, currentModelId, StringComparison.Ordinal) &&
                availableModelIds.Contains(preferred, StringComparer.Ordinal))
            {
                selectedModelId = preferred;
                return true;
            }
        }

        foreach (var candidate in PreferredCandidates)
        {
            if (!string.Equals(candidate, currentModelId, StringComparison.Ordinal) &&
                availableModelIds.Contains(candidate, StringComparer.Ordinal))
            {
                selectedModelId = candidate;
                return true;
            }
        }

        var fallback = availableModelIds
            .Where(modelId => !string.Equals(modelId, currentModelId, StringComparison.Ordinal))
            .Where(modelId => !modelId.Contains("embed", StringComparison.OrdinalIgnoreCase))
            .Where(modelId => modelId.Contains("instruct", StringComparison.OrdinalIgnoreCase) || modelId.Contains("chat", StringComparison.OrdinalIgnoreCase))
            .OrderBy(modelId => modelId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            selectedModelId = fallback;
            return true;
        }

        return false;
    }
}

public sealed record BuilderProofCommandExecutionResult(
    int ExitCode,
    IReadOnlyList<string> OutputLines);

public sealed class ValidationCommandBuilderProofRunner : IBuilderProofCommandRunner
{
    private readonly IValidationCommandExecutor _executor;

    public ValidationCommandBuilderProofRunner(IValidationCommandExecutor? executor = null)
    {
        _executor = executor ?? new ValidationCommandExecutor();
    }

    public async Task<BuilderProofCommandExecutionResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string logPath,
        CancellationToken ct)
    {
        var stageId = Path.GetFileNameWithoutExtension(logPath)
            .Replace(".", "-", StringComparison.Ordinal)
            .Replace(" ", "-", StringComparison.Ordinal);
        var stageLabel = Path.GetFileName(logPath);
        var command = new ValidationCommandSpec(
            stageId,
            stageLabel,
            fileName,
            arguments,
            Path.GetFileName(logPath),
            CanRunIndependently: true,
            TouchesBuildOutputs: true,
            RewritesArtifacts: true,
            ReadsOnly: false,
            SupportsIsolatedWorkspace: false,
            IsolationSupportStatus: "not_supported",
            IsolationSupportReason: "Builder proof commands execute directly in the bounded proof target folder.");

        var result = await _executor.ExecuteAsync(command, workingDirectory, logPath, _ => { }, ct).ConfigureAwait(false);
        return new BuilderProofCommandExecutionResult(result.ExitCode, result.OutputLines);
    }
}

public sealed record BuilderProofCapabilityClass(
    string TaskClass,
    string ExpectationSummary,
    bool InScope,
    string Notes,
    string PromptTighteningSummary = "");

public sealed record BuilderProofComplexityDimensions(
    int FileCountTouched = 1,
    int ProjectCountTouched = 1,
    int DependencyReferenceChangeCount = 0,
    bool TestChangesRequired = false,
    int NewFileCreationCount = 0,
    string PromptAmbiguity = "low");

public sealed record BuilderProofTargetDefinition(
    string TargetId,
    string TargetType,
    string TaskClass,
    string TargetLabel,
    string TargetScopeSummary,
    string ScopeConfidence,
    bool HasTests,
    string PromptScaffold = "",
    string TemplateVariant = "standard",
    IReadOnlyList<string>? AllowedAssistRules = null,
    BuilderProofComplexityDimensions? ComplexityDimensions = null);

public sealed record BuilderProofGenerationRecord(
    string GenerationOutcome,
    string GenerationSummary);

public sealed record BuilderProofStageRecord(
    string StageId,
    string StageLabel,
    string Status,
    string Summary,
    string LogPath,
    int ExitCode);

public sealed record BuilderProofFailure(
    string StageId,
    string StageLabel,
    string ProjectOrFile,
    string ErrorExcerpt,
    string LogPath,
    string Summary,
    int ExitCode);

public sealed record BuilderProofFollowupArtifacts(
    string FollowupState,
    string ValidationOutputFolder,
    string ValidationResultPath,
    string ValidationStabilityPath,
    string FollowupIntakePath,
    string FollowupPromptPath,
    string FollowupPlanPath,
    string RepairPrepBundlePath,
    string RepairBundlePath);

public sealed record BuilderProofRecoveryRecord(
    string RecoveryState,
    string RecoveryBuildLogPath,
    string RecoveryTestLogPath,
    string RecoveryValidationResultPath,
    string RecoveryValidationStabilityPath,
    string FollowupExecutionOutcomePath,
    string Summary);

public sealed record BuilderProofCaseResult(
    string TargetId,
    string TargetType,
    string TaskClass,
    string TargetLabel,
    string ModelId,
    string Provider,
    string TargetFolder,
    string GenerationOutcome,
    string GenerationSummary,
    IReadOnlyList<BuilderProofStageRecord> StageResults,
    string BuildResult,
    string TestResult,
    string FollowupState,
    string RecoveryState,
    string FinalClassification,
    string RepeatedFailureClassification,
    bool RecoveryRequired,
    string TargetScope,
    string ScopeConfidence,
    string ValidationResultPath,
    string FollowupIntakePath,
    string FollowupPlanPath,
    string RepairPrepBundlePath,
    string RepairBundlePath,
    string RecoveryValidationResultPath,
    string FollowupExecutionOutcomePath,
    string FinalSummary,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string ProofScope,
    string TrustBand = "",
    string RoutingRecommendationState = "",
    string StarterStateManifestPath = "");

public sealed record BuilderProofMatrixDefinition(
    string ProofRunId,
    string RunFolder,
    string ModelId,
    IReadOnlyList<BuilderProofCapabilityClass> CapabilityClasses,
    IReadOnlyList<string> OutOfScopeItems,
    IReadOnlyList<BuilderProofTargetDefinition> Targets,
    string ProofScope = "repo_local",
    IReadOnlyList<string>? PromptTighteningRules = null,
    IReadOnlyList<string>? AllowedAssistRules = null);

public sealed record BuilderModelFloorTaskResult(
    string TaskClass,
    string TargetId,
    string Outcome,
    string Summary);

public sealed record BuilderModelFloorVerdict(
    string ModelId,
    string Verdict,
    IReadOnlyList<BuilderModelFloorTaskResult> TaskResults,
    IReadOnlyList<string> Reasons,
    string RunFolder,
    string ProofRunArtifactPath,
    string ProofSummaryArtifactPath,
    string VerdictArtifactPath,
    string VerdictSummaryPath,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderProofRun(
    string ProofRunId,
    string RepoRoot,
    string RunFolder,
    string ModelId,
    string Provider,
    IReadOnlyList<string> TargetLabels,
    IReadOnlyList<BuilderProofCaseResult> CaseResults,
    int BuildPassCount,
    int TestPassCount,
    int RecoveryRequiredCount,
    string FinalClassification,
    string ModelFloorVerdict,
    string VerdictSummary,
    string MatrixArtifactPath,
    string RunArtifactPath,
    string SummaryArtifactPath,
    string VerdictArtifactPath,
    string VerdictSummaryArtifactPath,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public sealed record BuilderProofHistoryEntry(
    string RunId,
    string RunFolder,
    string ModelId,
    string TargetId,
    string TaskClass,
    string FinalClassification,
    string RepeatedFailureClassification,
    string Summary,
    DateTimeOffset CompletedUtc);

public sealed record BuilderProofHistory(
    int RetentionCount,
    IReadOnlyList<BuilderProofHistoryEntry> Entries);

public sealed record BuilderProofFailurePatternAggregate(
    string Category,
    int Count,
    IReadOnlyList<string> TargetIds,
    string Summary);

public sealed record BuilderProofFailurePatternEntry(
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string FailureCategory,
    string FailureReason,
    string RecoveryBurdenClassification,
    int RepairAttempts,
    int RerunAttempts,
    string RecoveryStageId,
    string ErrorExcerpt,
    string LogPath,
    string ValidationResultPath,
    string FollowupPlanPath);

public sealed record BuilderProofFailurePatternSummary(
    string ModelId,
    IReadOnlyList<BuilderProofFailurePatternAggregate> Categories,
    IReadOnlyList<BuilderProofFailurePatternEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExternalProofRun(
    string ProofRunId,
    string RepoRoot,
    string RunFolder,
    string PackFolder,
    string ModelId,
    string Provider,
    BuilderProofMatrixDefinition TargetPack,
    IReadOnlyList<BuilderProofCaseResult> CaseResults,
    int CleanSuccessCount,
    int RecoveryRequiredCount,
    int TooFragileCount,
    string FinalClassification,
    string Verdict,
    string Summary,
    string RunArtifactPath,
    string SummaryArtifactPath,
    string VerdictArtifactPath,
    string VerdictSummaryArtifactPath,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public sealed record BuilderExternalFloorVerdict(
    string ModelId,
    string Verdict,
    IReadOnlyList<BuilderModelFloorTaskResult> TaskResults,
    IReadOnlyList<string> Reasons,
    int CleanSuccessCount,
    int RecoveryRequiredCount,
    int TooFragileCount,
    string Summary,
    string RunFolder,
    string RunArtifactPath,
    string SummaryArtifactPath,
    string VerdictArtifactPath,
    string VerdictSummaryPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelFloorPolicy(
    string ModelId,
    string RepoLocalVerdict,
    string ExternalVerdict,
    string Summary,
    IReadOnlyList<string> Guidance,
    IReadOnlyList<string> AllowedAssistRules,
    IReadOnlyList<string> EvidencePaths,
    string ArtifactPath,
    string SummaryArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderDefaultPolicyTaskClassEntry(
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string PolicyState,
    string PrimaryWeakSpot,
    string Summary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> LinkedEvidencePaths);

public sealed record BuilderDefaultPolicy(
    string SourceProofRunId,
    string CurrentModelId,
    IReadOnlyList<string> InBandTaskClasses,
    IReadOnlyList<string> SplitFirstRequiredTaskClasses,
    IReadOnlyList<string> LowFloorRepairLoopExpectedTaskClasses,
    IReadOnlyList<string> StrongerTierOptionalTaskClasses,
    IReadOnlyList<string> StrongerTierRecommendedTaskClasses,
    IReadOnlyList<string> StrongerTierRequiredTaskClasses,
    IReadOnlyList<BuilderDefaultPolicyTaskClassEntry> TaskClassEntries,
    IReadOnlyList<string> LinkedProofEvidencePaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderDefaultPolicyHistoryEntry(
    string SourceProofRunId,
    string CurrentModelId,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc,
    IReadOnlyList<BuilderDefaultPolicyTaskClassEntry> TaskClassEntries);

public sealed record BuilderDefaultPolicyHistory(
    int RetentionCount,
    IReadOnlyList<BuilderDefaultPolicyHistoryEntry> Entries);

public sealed record BuilderModelWeakSpotSummary(
    string WeakSpot,
    int OccurrenceCount,
    string Classification,
    IReadOnlyList<string> TargetIds,
    string Summary);

public sealed record BuilderModelTrustBandEntry(
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string FinalClassification,
    string RecoveryBurdenClassification,
    string TrustBand,
    string RecommendationState,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> EvidencePaths);

public sealed record BuilderModelTrustBands(
    string ModelId,
    string RepoLocalVerdict,
    string ExternalVerdict,
    IReadOnlyList<BuilderModelTrustBandEntry> Entries,
    IReadOnlyList<BuilderModelWeakSpotSummary> WeakSpots,
    int CleanBuildBandCount,
    int RepairLoopBandCount,
    int EscalationRecommendedBandCount,
    int RejectBandCount,
    string Summary,
    string ArtifactPath,
    string ScopeSummaryPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelRoutingRecommendationEntry(
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string TrustBand,
    string RecommendationState,
    IReadOnlyList<string> Reasons,
    string ProofRunArtifactPath,
    string ValidationResultPath,
    string FollowupPlanPath);

public sealed record BuilderModelRoutingRecommendation(
    string ModelId,
    string RecommendationState,
    string FeaturedProofScope,
    string FeaturedTargetId,
    string FeaturedTargetLabel,
    string FeaturedTaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string TrustBand,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<BuilderModelRoutingRecommendationEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelComparativeProofHook(
    string ComparisonKey,
    string TaskClass,
    string CurrentModelId,
    string CurrentTrustBand,
    string RecommendedModelClass,
    string ProofScope,
    string TargetId,
    string CurrentProofRunArtifactPath,
    string FutureComparisonArtifactPath,
    string Summary);

public sealed record BuilderModelEscalationDecision(
    string ModelId,
    string CurrentModelId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string TrustBand,
    string RoutingRecommendationState,
    string EscalationRequirementState,
    string SplitTaskRecommendationState,
    string RecommendedModelClass,
    string ReasonForEscalation,
    string PrimaryWeakSpot,
    string PrimaryWeakSpotSummary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> SplitTaskGuidance,
    IReadOnlyList<string> LinkedProofEvidencePaths,
    BuilderModelComparativeProofHook ComparativeProofHook,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelRoutingPlan(
    string ModelId,
    string CurrentModelId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string TrustBand,
    string RoutingRecommendationState,
    string EscalationRequirementState,
    string SelectedCurrentModel,
    string RecommendedModelClass,
    string SplitTaskRecommendationState,
    string ReasonForEscalation,
    IReadOnlyList<string> SplitTaskGuidance,
    string PrimaryWeakSpot,
    string PrimaryWeakSpotSummary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> LinkedProofEvidencePaths,
    BuilderModelComparativeProofHook ComparativeProofHook,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderStrongerTierAvailability(
    string CurrentModelId,
    string RecommendedModelClass,
    string PreferredStrongerModelId,
    string ConfiguredStrongerTierId,
    string AvailabilityState,
    string Reason,
    string Provider,
    string Endpoint,
    string ErrorCode,
    IReadOnlyList<string> AvailableModelIds,
    string ProviderEvidenceSummary,
    IReadOnlyList<string> LinkedEvidencePaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWeakSpotComparativeOutcome(
    string WeakSpot,
    string LowFloorState,
    string StrongerTierState,
    string Summary);

public sealed record BuilderComparativeProofRun(
    string SourceProofRunId,
    string RepoRoot,
    string RunFolder,
    string ComparativeFolder,
    string CurrentModelId,
    string StrongerTierModelId,
    string RecommendedModelClass,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    BuilderProofCaseResult LowFloorCase,
    BuilderProofCaseResult StrongerTierCase,
    BuilderProofCaseResult? SplitLowFloorCase,
    string SplitThenEscalateEvidenceState,
    string SplitThenEscalateSummary,
    string RepairBurdenDifferenceSummary,
    IReadOnlyList<BuilderWeakSpotComparativeOutcome> WeakSpotOutcomes,
    string ComparativeClassification,
    string Summary,
    string ArtifactPath,
    string SummaryArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRoutingPolicyEvidence(
    string SourceProofRunId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string CurrentModelId,
    string StrongerTierModelId,
    string RecommendedModelClass,
    string StrongerTierAvailabilityState,
    string ComparativeClassification,
    string SplitThenEscalateEvidenceState,
    string RoutingPolicyState,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<BuilderWeakSpotComparativeOutcome> WeakSpotOutcomes,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderSplitExecutionHook(
    string SourceProofRunId,
    string SourceTargetId,
    string SourceTaskClass,
    string ProofScope,
    string CurrentModelId,
    string StrongerTierModelId,
    string ComparisonKey,
    string FutureExecutionArtifactPath,
    string Summary);

public sealed record BuilderSplitFirstPlanStep(
    int StepNumber,
    string StepId,
    string StepLabel,
    string SplitStrategy,
    string ScopeSummary,
    string ScopeClassification,
    string WeakSpotMitigation,
    IReadOnlyList<string> LinkedArtifactPaths,
    BuilderSplitExecutionHook ExecutionHook);

public sealed record BuilderSplitFirstPlan(
    string SourceProofRunId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string CurrentModelId,
    string StrongerTierModelId,
    string ComparisonKey,
    string StrongerTierComparisonResult,
    string StrongerTierComparisonSummary,
    string SplitRecommendationState,
    string PrimaryWeakSpot,
    string PrimaryWeakSpotSummary,
    IReadOnlyList<BuilderSplitFirstPlanStep> Steps,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderTieredRoutingPolicy(
    string SourceProofRunId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string CurrentModelId,
    string StrongerTierModelId,
    string ComparisonKey,
    string LowFloorRecommendationState,
    string SplitFirstRecommendationState,
    string StrongerTierRecommendationState,
    string PrimaryRoutingState,
    string PrimaryRecommendationSummary,
    string StrongerTierRoleSummary,
    string PrimaryWeakSpot,
    string PrimaryWeakSpotSummary,
    string WeakSpotMitigationSummary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<BuilderWeakSpotComparativeOutcome> WeakSpotOutcomes,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderSplitStepExecutionStepState(
    int StepNumber,
    string StepId,
    string StepLabel,
    string StepType,
    string ExecutionMode,
    string ScopeClassification,
    string EligibilityState,
    string BlockReason,
    string ExecutionState,
    string Detail,
    string EvidencePath,
    IReadOnlyList<string> LinkedArtifactPaths,
    string LinkedFollowupPlanPath,
    string LinkedRepairPrepBundlePath,
    string LastActionKind,
    DateTimeOffset UpdatedUtc);

public sealed record BuilderSplitStepExecution(
    string SourceProofRunId,
    string RunFolder,
    string ComplexityBand,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string CurrentModelId,
    string StrongerTierModelId,
    string SplitPlanPath,
    string ComparativeProofArtifactPath,
    string SourceFollowupPlanPath,
    string SourceRepairPrepBundlePath,
    string FreshnessState,
    IReadOnlyList<BuilderSplitStepExecutionStepState> Steps,
    string Summary,
    string ArtifactPath,
    DateTimeOffset RecordedUtc);

public sealed record BuilderSplitFirstOutcome(
    string SourceProofRunId,
    string RunFolder,
    string ComparisonKey,
    string SplitPlanPath,
    string SplitStepExecutionPath,
    string ComparativeProofArtifactPath,
    string ExecutedStepId,
    string ExecutedStepLabel,
    string SplitResultFinalClassification,
    string SplitResultSummary,
    string SplitBuildResult,
    string SplitTestResult,
    bool SplitRecoveryRequired,
    string SplitRecoveryBurden,
    string SplitValidationResultPath,
    string SplitFollowupPlanPath,
    string SplitRepairPrepBundlePath,
    string SplitFollowupExecutionOutcomePath,
    string UnsplitLowFloorClassification,
    string UnsplitLowFloorBurden,
    string StrongerTierClassification,
    string StrongerTierBurden,
    string ComparisonToUnsplit,
    string ComparisonToStrongerTier,
    string ClosureClassification,
    string PracticalRouteSummary,
    string FreshnessState,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset RecordedUtc,
    string SplitTargetFolderPath = "",
    string SplitStarterStateManifestPath = "");

public sealed record BuilderRequestPolicyDecision(
    string SourceProofRunId,
    string DecisionSource,
    string CurrentModelId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string ChosenPolicyState,
    string StrongerTierDisposition,
    bool SplitFirstIsDefault,
    string KnownWeakSpotLikelihood,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> LinkedEvidencePaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPolicyStability(
    string SourceProofRunId,
    string CurrentModelId,
    string TaskClass,
    string PolicyState,
    string SupportLevel,
    int SupportingRunCount,
    int ContradictionCount,
    IReadOnlyList<string> LatestCorroboratingArtifacts,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderConfirmedTaskClassEntry(
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string PolicyState,
    string CurrentRoute,
    int SupportingProofRunCount,
    int SupportingPreparedLaunchCount,
    int ConfirmationCount,
    int ContradictionCount,
    string CurrentReadinessState,
    string SummaryClassification,
    bool BuilderReadyForBoundedUse,
    int RequiredSupportingProofRuns,
    int RequiredPreparedLaunchConfirmations,
    int FreshProofRunCountAfterLatestContradiction,
    int FreshPreparedLaunchConfirmationCountAfterLatestContradiction,
    bool ReconfirmationRequired,
    bool DefaultRouteSuspended,
    string ReconfirmationStatus,
    string ContradictionAttributionState,
    string LatestContradictionNote,
    IReadOnlyList<string> ContradictoryRunIds,
    IReadOnlyList<string> LinkedEvidencePaths,
    string Summary);

public sealed record BuilderConfirmedTaskClasses(
    string SourceProofRunId,
    string CurrentModelId,
    IReadOnlyList<BuilderConfirmedTaskClassEntry> Entries,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderDefaultRouteDecision(
    string SourceProofRunId,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string ChosenDefaultRoute,
    string RouteSourceState,
    string OperatorOverrideState,
    bool DefaultRouteSuspended,
    int ConfirmationEvidenceCount,
    int ContradictionCount,
    string ReasonSummary,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReadinessContradictionEntry(
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string PriorConfirmedRoute,
    string ContradictionAttributionState,
    string ContradictoryRoute,
    IReadOnlyList<string> ContradictoryRunIds,
    string ContradictionReason,
    string ResultingDowngradedState,
    bool DefaultRouteSuspended,
    int RequiredFreshProofRunCount,
    int RequiredFreshPreparedLaunchConfirmations,
    int FreshProofRunCountAfterLatestContradiction,
    int FreshPreparedLaunchConfirmationCountAfterLatestContradiction,
    string ReconfirmationStatus,
    IReadOnlyList<string> LinkedArtifactPaths);

public sealed record BuilderReadinessContradictions(
    string SourceProofRunId,
    IReadOnlyList<BuilderReadinessContradictionEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderLaunchDefaultDecision(
    string SourceProofRunId,
    string SourceIntakeId,
    string SourceExecutionPrepId,
    string TaskClass,
    string ConfirmedDefaultRoute,
    string ActualLaunchRoute,
    string RouteSourceState,
    string OperatorDecisionState,
    string OperatorOverrideState,
    string OverrideReason,
    bool RepairLoopExpectedDefault,
    string CurrentReadinessState,
    bool DefaultRouteSuspended,
    string ReconfirmationStatus,
    string LaunchEligibilityState,
    string BlockReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteOverrideEvidence(
    string SourceProofRunId,
    string SourceIntakeId,
    string SourceExecutionPrepId,
    string SourceLaunchId,
    string TaskClass,
    string DefaultRoute,
    string SelectedRoute,
    string OverrideState,
    string OverrideReason,
    string LaunchOutcomeClassification,
    string BaselineDefaultOutcomeClassification,
    string OverrideOutcomeComparisonState,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteReviewCandidateEntry(
    string TaskClass,
    string CandidateState,
    string CurrentRoute,
    int EvidenceCount,
    string Summary,
    IReadOnlyList<string> LinkedArtifactPaths);

public sealed record BuilderRouteReviewCandidates(
    string SourceProofRunId,
    IReadOnlyList<BuilderRouteReviewCandidateEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteReconfirmation(
    string SourceProofRunId,
    string SourceIntakeId,
    string TaskClass,
    string PriorConfirmedDefaultRoute,
    string ContradictionAttributionState,
    IReadOnlyList<string> ContradictionSourceRunIds,
    IReadOnlyList<string> ReconfirmationRunIds,
    int RequiredFreshProofRunCount,
    int RequiredFreshPreparedLaunchConfirmations,
    int FreshProofRunCount,
    int FreshPreparedLaunchConfirmationCount,
    string CurrentReconfirmationState,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderDefaultRouteRecovery(
    string SourceProofRunId,
    string SourceIntakeId,
    string TaskClass,
    string SuspendedRoute,
    string SuspensionCauseState,
    string ContradictionAttributionState,
    IReadOnlyList<string> RecoveryEvidenceRunIds,
    int RequiredFreshProofRunCount,
    int RequiredFreshPreparedLaunchConfirmations,
    int FreshProofRunCount,
    int FreshPreparedLaunchConfirmationCount,
    string RecoveryState,
    bool DefaultRouteRestored,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteArtifactReference(
    string ArtifactKind,
    string SourceProofRunId,
    string SourceRunFolder,
    string ArtifactPath);

public sealed record BuilderRouteStateContinuityEntry(
    string SourceProofRunId,
    string SourceRunFolder,
    string TaskClass,
    string DefaultRoute,
    string PreparedRoute,
    string RouteSourceState,
    string CurrentReadinessState,
    string ContradictionAttributionState,
    string ReconfirmationState,
    bool DefaultRouteSuspended,
    string ContinuityState,
    int OverrideContradictionCycleCount,
    int ReconfirmationCycleCount,
    IReadOnlyList<BuilderRouteArtifactReference> AvailableArtifacts,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteStateContinuity(
    string LatestProofRunId,
    string LatestRunFolder,
    string CurrentTaskClass,
    string CurrentDefaultRoute,
    string CurrentPreparedRoute,
    string CurrentContinuityState,
    bool DefaultRouteSuspended,
    int OverrideContradictionCycleCount,
    int ReconfirmationCycleCount,
    IReadOnlyList<BuilderRouteStateContinuityEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteCurrentStateArtifactIndexEntry(
    string ArtifactKind,
    string SourceProofRunId,
    string SourceRunFolder,
    string ArtifactPath,
    string ResolutionState);

public sealed record BuilderRouteCurrentStateIndex(
    string LatestProofRunId,
    string LatestRunFolder,
    string CurrentTaskClass,
    string CurrentDefaultRoute,
    string CurrentPreparedRoute,
    string CurrentReadinessState,
    string CurrentReconfirmationState,
    bool DefaultRouteSuspended,
    int OverrideContradictionCycleCount,
    int ReconfirmationCycleCount,
    IReadOnlyList<BuilderRouteCurrentStateArtifactIndexEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelCapabilityMatrixEntry(
    string TaskClass,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string PolicyState,
    string RouteClass,
    string CapabilityState,
    string SupportedLowFloorState,
    string RepairLoopAcceptability,
    bool SplitFirstRequired,
    string SplitFirstRequirementState,
    string StrongerTierRecommendationState,
    string StrongerTierRequirementState,
    string EvidenceSupportLevel,
    IReadOnlyList<string> LinkedProofArtifactPaths,
    string Summary);

public sealed record BuilderModelCapabilityMatrix(
    string SourceProofRunId,
    string CurrentModelId,
    string PreferredLowFloorModelTier,
    string PreferredStrongerModelTier,
    IReadOnlyList<BuilderModelCapabilityMatrixEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelRoutingPolicyEntry(
    string TaskClass,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string PolicyState,
    string RouteClass,
    string PreferredModelTier,
    IReadOnlyList<string> AllowedModelTiers,
    string FallbackPath,
    bool SplitFirstRequired,
    string RepairLoopExpectation,
    string StrongerTierRecommendationState,
    string StrongerTierRequirementState,
    string EscalationTrigger,
    bool OperatorOverrideAllowed,
    string EvidenceSupportLevel,
    IReadOnlyList<string> LinkedEvidencePaths,
    string Summary);

public sealed record BuilderModelRoutingPolicy(
    string SourceProofRunId,
    string CurrentModelId,
    string PreferredLowFloorModelTier,
    string PreferredStrongerModelId,
    IReadOnlyList<BuilderModelRoutingPolicyEntry> Entries,
    string Summary,
    string ArtifactPath,
    string SummaryArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelRoutingPolicyHistoryEntry(
    string TaskClass,
    string PriorPolicyState,
    string NewPolicyState,
    string EvidenceChangeSummary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelRoutingPolicyHistory(
    int RetentionCount,
    IReadOnlyList<BuilderModelRoutingPolicyHistoryEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelRoutingStabilityEntry(
    string TaskClass,
    string CurrentPolicyState,
    string StabilityState,
    int SupportingRunCount,
    int ContradictionCount,
    IReadOnlyList<string> CorroboratingArtifacts,
    string Summary);

public sealed record BuilderModelRoutingStability(
    string SourceProofRunId,
    string CurrentModelId,
    IReadOnlyList<BuilderModelRoutingStabilityEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelDecision(
    string RequestId,
    string RawRequestText,
    string NormalizedTaskClass,
    string SelectedModelTier,
    string SelectedModelId,
    string CapabilityState,
    string StrongerTierRecommendationState,
    string StrongerTierRequirementState,
    bool SplitFirstKeepsLowFloorViable,
    string EvidenceSupportLevel,
    string DecisionReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelEscalationPolicyDecision(
    string RequestId,
    string TaskClass,
    string LowFloorSuitabilityState,
    string StrongerTierRecommendationState,
    string SplitFirstViabilityState,
    string StrongerTierAvailabilityState,
    string FinalDecisionState,
    string BlockReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRouteExplanation(
    string RequestId,
    string TaskClass,
    string SelectedRoute,
    IReadOnlyList<string> AlternateRoutesConsidered,
    string RouteSelectionReason,
    IReadOnlyList<string> LinkedCapabilityMatrixEntries,
    IReadOnlyList<string> LinkedProofArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderModelDecisionExplanation(
    string RequestId,
    string ModelTierSelected,
    string ModelIdSelected,
    string CapabilityMatrixEntrySummary,
    string RoutingRulesEntrySummary,
    string EscalationState,
    string SplitFirstReasoning,
    string StrongerTierRecommendationState,
    string EvidenceSupportLevel,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderFailureAnalysis(
    string ExecutionSessionId,
    string FailureStageId,
    string FailureStageLabel,
    string FailureClassification,
    string FailureReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    string PossibleRemediationPath,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderToolchainCapabilityRegistryEntry(
    string ToolId,
    string ToolCategory,
    string DiscoveredPath,
    string Version,
    bool Installed,
    bool Callable,
    bool SupportedByRepo,
    bool PreferredByRepo,
    string RepoSupportState,
    string UsabilityState,
    string BlockedReason,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderToolchainCapabilityRegistry(
    string PreferredStackId,
    string PreferredStackLabel,
    IReadOnlyList<BuilderToolchainCapabilityRegistryEntry> Entries,
    string RefreshState,
    string DriftState,
    IReadOnlyList<string> ChangedToolIds,
    IReadOnlyList<string> ChangeSummaries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderToolchainCapabilityHistoryEntry(
    string PreferredStackId,
    string PreferredStackLabel,
    string RefreshState,
    string DriftState,
    IReadOnlyList<string> ChangedToolIds,
    IReadOnlyList<string> ChangeSummaries,
    string Summary,
    string RegistryArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderToolchainCapabilityHistory(
    int RetentionCount,
    IReadOnlyList<BuilderToolchainCapabilityHistoryEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderLanguageEligibilityEntry(
    string StackId,
    string StackLabel,
    string EligibilityState,
    bool SupportedByRepo,
    bool PreferredByRepo,
    IReadOnlyList<string> RequiredToolIds,
    string ToolchainRequirementSummary,
    string BlockedReason,
    string Summary);

public sealed record BuilderLanguageEligibility(
    string PreferredStackId,
    string PreferredStackLabel,
    IReadOnlyList<BuilderLanguageEligibilityEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRepoKnowledgeLinkedItem(
    string RelativePath,
    string LinkageState,
    string Summary);

public sealed record BuilderRepoKnowledgeProjectEntry(
    string ProjectId,
    string ProjectName,
    string RelativePath,
    string ProjectType,
    IReadOnlyList<string> TargetFrameworks,
    string InferredStackLabel,
    string PrimaryLanguage,
    IReadOnlyList<string> FeatureAreaLabels,
    IReadOnlyList<BuilderRepoKnowledgeLinkedItem> RelatedTests,
    IReadOnlyList<BuilderRepoKnowledgeLinkedItem> RelatedUiSurfaces,
    IReadOnlyList<BuilderRepoKnowledgeLinkedItem> RelatedServices,
    IReadOnlyList<BuilderRepoKnowledgeLinkedItem> RelatedViewModels,
    IReadOnlyList<BuilderRepoKnowledgeLinkedItem> RelatedBuilderFiles,
    IReadOnlyList<string> RelatedProjectIds,
    string FeatureSummary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRepoKnowledgeOwnershipSummary(
    string RelativePath,
    string OwnerProjectId,
    string OwnerProjectName,
    string OwnershipState,
    string Summary);

public sealed record BuilderRepoKnowledgeIndex(
    string PreferredStackId,
    string PreferredStackLabel,
    IReadOnlyList<string> SolutionPaths,
    IReadOnlyList<string> KeyDirectories,
    IReadOnlyList<BuilderRepoKnowledgeProjectEntry> ProjectEntries,
    IReadOnlyList<BuilderRepoKnowledgeOwnershipSummary> FileOwnershipSummaries,
    IReadOnlyList<string> LinkedAuthoritativeArtifactPaths,
    string RefreshState,
    string DriftState,
    IReadOnlyList<string> ChangedProjectIds,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRepoKnowledgeHistoryEntry(
    string PreferredStackId,
    string DriftState,
    IReadOnlyList<string> ChangedProjectIds,
    int ProjectCount,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRepoKnowledgeHistory(
    int RetentionCount,
    IReadOnlyList<BuilderRepoKnowledgeHistoryEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRepoKnowledgeDrift(
    IReadOnlyList<string> AddedProjectIds,
    IReadOnlyList<string> RemovedProjectIds,
    IReadOnlyList<string> ChangedProjectIds,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRepoRetrievalContext(
    string RawRequestText,
    string NormalizedTaskClass,
    string ImpliedStackId,
    string ImpliedStackLabel,
    string RetrievalConfidenceState,
    IReadOnlyList<string> MatchedProjectIds,
    IReadOnlyList<string> MatchedFiles,
    IReadOnlyList<string> MatchedTests,
    IReadOnlyList<string> MatchedUiSurfaces,
    IReadOnlyList<string> MatchedServices,
    IReadOnlyList<string> MatchedViewModels,
    string StackMatchState,
    IReadOnlyList<string> AuthoritativeArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderConversationIntake(
    string RawRequestText,
    string NormalizedTaskClass,
    string ImpliedStackId,
    string ImpliedStackLabel,
    string RetrievalConfidenceState,
    string RetrievalSummary,
    string CapabilityRoutingState,
    string CapabilitySummary,
    string SelectedRoute,
    string RouteSourceState,
    bool SplitFirstRequired,
    string StrongerTierDisposition,
    string OperatorDecisionState,
    string LaunchReadinessState,
    string BlockReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderConversationHandoff(
    string RawRequestText,
    string NormalizedTaskClass,
    string RetrievalConfidenceState,
    string CapabilityRoutingState,
    string SelectedRoute,
    string RouteSourceState,
    string OperatorDecisionState,
    string LaunchReadinessState,
    string BlockReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchReviewChangedFile(
    string Path,
    string FileCategory,
    string ChangeKind,
    string ChangeSummary,
    bool ReviewReady);

public sealed record BuilderConversationExecutionStage(
    string StageId,
    string StageLabel,
    string StageState,
    string Detail,
    IReadOnlyList<string> LinkedArtifactPaths);

public sealed record BuilderConversationExecutionSession(
    string SessionId,
    string SourceConversationIntakeId,
    string SourceConversationHandoffId,
    string RawRequestText,
    string NormalizedTaskClass,
    string SelectedRoute,
    string StackId,
    string StackLabel,
    string ToolchainContextSummary,
    string SessionState,
    string CurrentStageId,
    string CurrentStageLabel,
    string ReviewState,
    string ValidationSummary,
    string SourceExecutionPrepPath,
    string LaunchArtifactPath,
    string ResultArtifactPath,
    string PatchReviewPath,
    string PatchReviewOutcomePath,
    IReadOnlyList<BuilderPatchReviewChangedFile> ChangedFiles,
    IReadOnlyList<BuilderConversationExecutionStage> Stages,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchReview(
    string SessionId,
    string SourceConversationIntakeId,
    string SourceConversationHandoffId,
    string RouteUsed,
    string StackId,
    string StackLabel,
    string ValidationSummary,
    string ReviewReadinessState,
    IReadOnlyList<BuilderPatchReviewChangedFile> ChangedFiles,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchReviewOutcome(
    string SessionId,
    string ReviewDecisionState,
    string SessionState,
    string ReviewState,
    string ReviewNote,
    string RerouteRoute,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchDiffReviewFileEntry(
    string RelativePath,
    string FileCategory,
    string ChangeKind,
    string DiffSummary,
    string PatchPreviewText,
    string ApprovalState,
    string RejectionReason,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchDiffReview(
    string SessionId,
    string SourcePatchReviewId,
    string SourcePatchReviewPath,
    string OverallFileReviewState,
    string ReviewReadinessState,
    IReadOnlyList<BuilderPatchDiffReviewFileEntry> FileEntries,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderFileReviewDecisionEntry(
    string RelativePath,
    string ApprovalState,
    string OperatorDecisionSource,
    string RejectionReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    DateTimeOffset ObservedUtc);

public sealed record BuilderFileReviewDecision(
    string SessionId,
    string SourcePatchDiffReviewId,
    string OverallFileReviewState,
    IReadOnlyList<BuilderFileReviewDecisionEntry> Entries,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchApplyDecision(
    string SessionId,
    string OverallFileApprovalState,
    string ApplyEligibilityState,
    IReadOnlyList<string> BlockReasons,
    string FinalizationState,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchSnapshotFileEntry(
    string RelativePath,
    string ChangeType,
    string Checksum,
    string ApprovalState,
    DateTimeOffset ApprovedUtc);

public sealed record BuilderPatchSnapshot(
    string SnapshotId,
    string ExecutionSessionId,
    string PatchDiffReviewId,
    string RouteId,
    string StackId,
    string OperatorApprovalState,
    IReadOnlyList<BuilderPatchSnapshotFileEntry> ApprovedFiles,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset ObservedUtc);

public sealed record BuilderCommitProposal(
    string SnapshotId,
    string ExecutionSessionId,
    string ProposedCommitMessage,
    IReadOnlyList<string> ChangedFiles,
    string DiffSummary,
    string RepoPath,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchExport(
    string SnapshotId,
    string BundleFilePath,
    DateTimeOffset ExportedUtc,
    int FileCount,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchSnapshotHistoryEntry(
    string SnapshotId,
    string SessionId,
    string ApprovalResult,
    string ExportBundlePath,
    string ArtifactPath,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderPatchSnapshotHistory(
    int RetentionCount,
    IReadOnlyList<BuilderPatchSnapshotHistoryEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderGitReadinessObservation(
    bool RepoDetected,
    string BranchName,
    bool WorkingTreeStateKnown,
    bool WorkingTreeClean,
    string AheadBehindState,
    string ReadinessClassification,
    IReadOnlyList<string> BlockReasons,
    DateTimeOffset ObservedUtc);

public sealed record BuilderGitHandoffReadiness(
    bool RepoDetected,
    string BranchName,
    bool WorkingTreeStateKnown,
    bool WorkingTreeClean,
    string AheadBehindState,
    string ReadinessClassification,
    IReadOnlyList<string> BlockReasons,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderManualApplyGuidance(
    string SnapshotId,
    string PatchBundlePath,
    IReadOnlyList<string> ApprovedFiles,
    IReadOnlyList<string> ApplySteps,
    IReadOnlyList<string> Warnings,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderGitCommitHandoff(
    string SnapshotId,
    string ProposedCommitMessage,
    IReadOnlyList<string> ApprovedFiles,
    string BranchName,
    string ReadinessClassification,
    IReadOnlyList<string> BlockReasons,
    IReadOnlyList<string> NextStepGuidance,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderOutputHandoff(
    string SnapshotId,
    string ExecutionSessionId,
    IReadOnlyList<string> ApprovedFiles,
    string PatchBundlePath,
    string CommitProposalPath,
    string ExportMetadataPath,
    string ManualApplyGuidancePath,
    string GitReadinessArtifactPath,
    string GitCommitHandoffPath,
    string HandoffReadinessState,
    string OptionalGitReadinessState,
    IReadOnlyList<string> BlockReasons,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderOutputHandoffHistoryEntry(
    string SnapshotId,
    string SessionId,
    string ExportState,
    string ManualApplyGuidancePath,
    string GitReadinessState,
    string GitCommitHandoffPath,
    string ArtifactPath,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderOutputHandoffHistory(
    int RetentionCount,
    IReadOnlyList<BuilderOutputHandoffHistoryEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderConversationExecutionHistoryEntry(
    string SessionId,
    string RawRequestText,
    string SelectedRoute,
    string SessionState,
    string ReviewState,
    string ArtifactPath,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderConversationExecutionHistory(
    int RetentionCount,
    IReadOnlyList<BuilderConversationExecutionHistoryEntry> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public interface IBuilderGitReadinessProbe
{
    BuilderGitReadinessObservation Probe(string repoRoot);
}

public sealed record BuilderCapabilityBlockDecision(
    string SourceProofRunId,
    string TaskClass,
    string ProofScope,
    string RequestedStackId,
    string RequestedStackLabel,
    string EffectiveStackId,
    string EffectiveStackLabel,
    string ToolchainRequirement,
    string CapabilityState,
    string EligibilityState,
    string RoutingDecisionState,
    string RecommendedRoute,
    string RecommendedStackId,
    string BlockReason,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRequestIntake(
    string RequestId,
    string SourceProofRunId,
    string SourceRequestDecisionPath,
    string DecisionSource,
    string CurrentModelId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string NormalizedTaskClass,
    BuilderProofComplexityDimensions ComplexityDimensions,
    string DefaultPolicyState,
    string StrongerTierDisposition,
    string SupportLevel,
    string NormalizationState,
    string IntakeClassificationState,
    string KnownWeakSpotLikelihood,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> LinkedEvidencePaths,
    string FreshnessState,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc,
    string RouteSourceState = "suggested",
    string OperatorOverrideState = "override_available_no_override",
    string DefaultRouteReason = "",
    string RequestedStackId = "",
    string EffectiveStackId = "",
    string EffectiveStackLabel = "",
    string LanguageEligibilityState = "not_recorded",
    string CapabilityRoutingState = "route_allowed",
    string CapabilityBlockReason = "",
    string CapabilitySummary = "",
    string CapabilityBlockDecisionPath = "");

public sealed record BuilderRequestIntakeHistoryEntry(
    string RequestId,
    string SourceProofRunId,
    string CurrentModelId,
    string NormalizedTaskClass,
    string IntakeClassificationState,
    string FreshnessState,
    string ArtifactPath,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderRequestIntakeHistory(
    int RetentionCount,
    IReadOnlyList<BuilderRequestIntakeHistoryEntry> Entries);

public sealed record BuilderExecutionPrep(
    string SourceIntakeId,
    string SourceProofRunId,
    string CurrentModelId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string NormalizedTaskClass,
    string IntakeClassificationState,
    string SelectedRoute,
    string StrongerTierRole,
    string SupportLevel,
    string RerunRepairExpectationLevel,
    bool SplitPlanRequired,
    string SplitPlanPath,
    string TieredRoutingPath,
    string WeakSpotMitigationSummary,
    IReadOnlyList<string> RequiredEvidencePaths,
    IReadOnlyList<string> NextActions,
    IReadOnlyList<string> FutureExecutionHookPaths,
    IReadOnlyList<string> LinkedArtifactPaths,
    string FreshnessState,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc,
    string RouteSourceState = "suggested",
    string OperatorOverrideState = "override_available_no_override",
    string DefaultRouteReason = "",
    string RequestedStackId = "",
    string EffectiveStackId = "",
    string EffectiveStackLabel = "",
    string LanguageEligibilityState = "not_recorded",
    string CapabilityRoutingState = "route_allowed",
    string CapabilityBlockReason = "",
    string CapabilitySummary = "",
    string CapabilityBlockDecisionPath = "");

public sealed record BuilderExecutionPrepHistoryEntry(
    string SourceIntakeId,
    string SourceProofRunId,
    string NormalizedTaskClass,
    string SelectedRoute,
    string FreshnessState,
    string ArtifactPath,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderExecutionPrepHistory(
    int RetentionCount,
    IReadOnlyList<BuilderExecutionPrepHistoryEntry> Entries);

public sealed record PreparedBuilderExecutionLaunch(
    string LaunchId,
    string SourceProofRunId,
    string SourceIntakeId,
    string SourceExecutionPrepId,
    string SourceExecutionPrepPath,
    string SelectedRoute,
    string SelectedModelTier,
    string SelectedModelId,
    string LaunchEligibilityState,
    string BlockReason,
    string FreshnessState,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset LaunchedUtc);

public sealed record PreparedBuilderExecutionResult(
    string SourceLaunchId,
    string SourceProofRunId,
    string SourceIntakeId,
    string SourceExecutionPrepId,
    string ActualRouteUsed,
    string ModelTierUsed,
    string ModelUsed,
    string GeneratedScopeSummary,
    string BuildResult,
    string TestResult,
    string FollowupState,
    string FollowupIntakePath,
    string FollowupPlanPath,
    string RepairPrepBundlePath,
    string RepairBundlePath,
    string FollowupExecutionOutcomePath,
    string FinalRouteOutcomeClassification,
    string PreparedRouteComparisonState,
    string FreshnessState,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset RecordedUtc,
    string SourceWorkingFolderPath = "",
    string StarterStateManifestPath = "");

public sealed record BuilderReadinessGate(
    string SourceProofRunId,
    string SourceIntakeId,
    string SourceExecutionPrepId,
    string CurrentModelId,
    string ProofScope,
    string TargetId,
    string TargetLabel,
    string TaskClass,
    string CurrentRoute,
    int RequiredSupportingProofRuns,
    int RequiredPreparedLaunchConfirmations,
    int SupportingProofRunCount,
    int SupportingPreparedLaunchCount,
    int ConfirmationCount,
    int ContradictionCount,
    string LatestPolicyResultComparison,
    string CurrentReadinessGateState,
    bool BuilderReadyForBoundedUse,
    string CurrentRecommendation,
    IReadOnlyList<string> ContradictionNotes,
    IReadOnlyList<string> LatestSupportingArtifactPaths,
    IReadOnlyList<string> LinkedArtifactPaths,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc,
    int FreshProofRunCountAfterLatestContradiction = 0,
    int FreshPreparedLaunchConfirmationCountAfterLatestContradiction = 0,
    bool ReconfirmationRequired = false,
    bool DefaultRouteSuspended = false,
    string ReconfirmationStatus = "not_required",
    string ContradictionAttributionState = "none",
    int RequiredFreshProofRunCountForReconfirmation = 0,
    int RequiredFreshPreparedLaunchConfirmationsForReconfirmation = 0);

public sealed record BuilderReadinessGateHistoryEntry(
    string SourceProofRunId,
    string SourceIntakeId,
    string TaskClass,
    string CurrentRoute,
    string ReadinessGateState,
    int ConfirmationCount,
    int ContradictionCount,
    string ChangeReason,
    string ArtifactPath,
    string Summary,
    DateTimeOffset ObservedUtc);

public sealed record BuilderReadinessGateHistory(
    int RetentionCount,
    IReadOnlyList<BuilderReadinessGateHistoryEntry> Entries);

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
    ReplayDiffResult Diff,
    PersistedRunMetadata Metadata,
    RunModel Run);

public sealed record ReplayStageDiffRecord(
    string StageName,
    string DiffKind,
    string Summary,
    long OriginalDurationMs,
    long ReplayDurationMs,
    long DriftMs,
    bool MajorDeviation);

public sealed record ReplayDiffResult(
    string SourceRunPath,
    bool IsMatch,
    string Summary,
    IReadOnlyList<ReplayStageDiffRecord> StageDiffs,
    IReadOnlyList<string> Mismatches);

public sealed record ReplayErrorRecord(
    string Code,
    string Message,
    string SourceRunPath,
    DateTimeOffset ObservedUtc);

public static class RunReplayService
{
    public const string MetadataFileName = "run_metadata.json";
    public const string TimelineFileName = "timeline.json";
    public const string FailureFingerprintFileName = "failure-fingerprint.json";
    public const string ReplayDiffFileName = "replay_diff.json";
    public const string ReplayErrorFileName = "replay_error.json";

    public static string MetadataPath(string runPath) => Path.Combine(runPath, MetadataFileName);

    public static string TimelinePath(string runPath) => Path.Combine(runPath, TimelineFileName);

    public static string FailureFingerprintPath(string runPath) => Path.Combine(runPath, FailureFingerprintFileName);

    public static string ReplayDiffPath(string runPath) => Path.Combine(runPath, ReplayDiffFileName);

    public static string ReplayErrorPath(string runPath) => Path.Combine(runPath, ReplayErrorFileName);

    public static ReplayInspectionResult ReplayFromRunPath(string runPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(runPath))
                throw new ArgumentException("run path is required", nameof(runPath));
            if (!Directory.Exists(runPath))
                throw new DirectoryNotFoundException($"Run path not found: {runPath}");

            var metadataPath = MetadataPath(runPath);
            var timelinePath = TimelinePath(runPath);
            var runJsonPath = Path.Combine(runPath, "run.json");

            if (!File.Exists(metadataPath))
                throw new FileNotFoundException("Run metadata file is missing.", metadataPath);
            if (!File.Exists(timelinePath))
                throw new FileNotFoundException("timeline.json is missing.", timelinePath);
            if (!File.Exists(runJsonPath))
                throw new FileNotFoundException("run.json is missing.", runJsonPath);

            var metadata = JsonSerializer.Deserialize<PersistedRunMetadata>(File.ReadAllText(metadataPath));
            var timeline = JsonSerializer.Deserialize<IReadOnlyList<RunStageRecord>>(File.ReadAllText(timelinePath));
            var run = JsonSerializer.Deserialize<RunModel>(File.ReadAllText(runJsonPath));

            ValidateReplayArtifacts(runPath, metadata, timeline, run);

            var mismatches = new List<string>();

            if (!string.Equals(metadata!.RunId, run!.RunId, StringComparison.Ordinal))
                mismatches.Add($"run_id mismatch: metadata={metadata.RunId}; run={run.RunId}");

            if (!string.Equals(metadata.Provider, run.Provider, StringComparison.Ordinal))
                mismatches.Add($"provider mismatch: metadata={metadata.Provider}; run={run.Provider}");

            if (!string.Equals(metadata.TerminalStatus, run.Status, StringComparison.Ordinal))
                mismatches.Add($"terminal status mismatch: metadata={metadata.TerminalStatus}; run={run.Status}");

            var metadataStages = metadata.StageFlow.Select(stage => $"{stage.StageName}:{stage.Status}").ToArray();
            var runStages = run.Steps.Select(step => $"{step.StepId}:{step.Status}").ToArray();

            if (!ContainsOrderedRunSteps(metadataStages, runStages))
            {
                mismatches.Add(
                    $"stage flow diverged: metadata={string.Join(",", metadataStages)}; run={string.Join(",", runStages)}");
            }

            foreach (var artifact in metadata.ArtifactPaths.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(artifact.Value) && !File.Exists(artifact.Value) && !Directory.Exists(artifact.Value))
                    mismatches.Add($"artifact missing: {artifact.Key}={artifact.Value}");
            }

            var diff = BuildReplayDiff(runPath, timeline!, metadata.StageFlow, mismatches);
            File.WriteAllText(ReplayDiffPath(runPath), JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true }));
            ClearReplayError(runPath);

            var summary = mismatches.Count == 0
                ? "Replay matches saved run metadata."
                : $"Replay diverged from saved run metadata ({mismatches.Count} mismatch{(mismatches.Count == 1 ? string.Empty : "es")}).";

            return new ReplayInspectionResult(runPath, mismatches.Count == 0, summary, mismatches, diff, metadata, run);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or JsonException or ArgumentException or DirectoryNotFoundException)
        {
            var error = new ReplayErrorRecord(
                "replay.artifacts.invalid",
                ex.Message,
                runPath,
                DateTimeOffset.UtcNow);
            var parent = string.IsNullOrWhiteSpace(runPath) ? "." : runPath;
            Directory.CreateDirectory(parent);
            File.WriteAllText(ReplayErrorPath(runPath), JsonSerializer.Serialize(error, new JsonSerializerOptions { WriteIndented = true }));
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static void ValidateReplayArtifacts(
        string runPath,
        PersistedRunMetadata? metadata,
        IReadOnlyList<RunStageRecord>? timeline,
        RunModel? run)
    {
        if (metadata is null)
            throw new InvalidDataException("Run metadata could not be parsed.");
        if (timeline is null)
            throw new InvalidDataException("timeline.json could not be parsed.");
        if (run is null)
            throw new InvalidDataException("run.json could not be parsed.");
        if (string.IsNullOrWhiteSpace(metadata.RunId) || string.IsNullOrWhiteSpace(metadata.Provider))
            throw new InvalidDataException("run_metadata.json is incomplete.");
        if (metadata.StageFlow is null || metadata.StageFlow.Count == 0)
            throw new InvalidDataException("run_metadata.json is missing stage flow.");
        if (timeline.Count == 0)
            throw new InvalidDataException("timeline.json is empty.");

        foreach (var stage in timeline.Concat(metadata.StageFlow))
        {
            if (string.IsNullOrWhiteSpace(stage.StageName) || string.IsNullOrWhiteSpace(stage.Status))
                throw new InvalidDataException("Replay timeline contains a stage with missing name or status.");
            if (stage.EndedUtc < stage.StartedUtc)
                throw new InvalidDataException($"Replay timeline contains invalid timestamps for stage '{stage.StageName}'.");
        }

        if (!string.Equals(metadata.RunPath, runPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"run_metadata.json run path mismatch: metadata={metadata.RunPath}; actual={runPath}");
    }

    private static ReplayDiffResult BuildReplayDiff(
        string runPath,
        IReadOnlyList<RunStageRecord> originalTimeline,
        IReadOnlyList<RunStageRecord> replayTimeline,
        IReadOnlyList<string> mismatches)
    {
        var diffs = new List<ReplayStageDiffRecord>();
        var remainingReplay = replayTimeline.ToDictionary(stage => stage.StageName, stage => stage, StringComparer.Ordinal);

        foreach (var original in originalTimeline)
        {
            if (!remainingReplay.TryGetValue(original.StageName, out var replay))
            {
                diffs.Add(new ReplayStageDiffRecord(
                    original.StageName,
                    "missing_step",
                    $"Stage '{original.StageName}' is missing from replay timeline.",
                    DurationMs(original),
                    0,
                    DurationMs(original),
                    true));
                continue;
            }

            remainingReplay.Remove(original.StageName);
            var originalDuration = DurationMs(original);
            var replayDuration = DurationMs(replay);
            var drift = Math.Abs(originalDuration - replayDuration);
            var statusMatch = string.Equals(original.Status, replay.Status, StringComparison.Ordinal);
            var kind = statusMatch ? (drift >= 500 ? "timing_drift" : "match") : "stage_mismatch";
            var majorDeviation = !statusMatch || drift >= 500;
            var stageSummary = !statusMatch
                ? $"Stage '{original.StageName}' status changed: original={original.Status}; replay={replay.Status}."
                : drift >= 500
                    ? $"Stage '{original.StageName}' timing drifted by {drift} ms."
                    : $"Stage '{original.StageName}' matched.";

            diffs.Add(new ReplayStageDiffRecord(
                original.StageName,
                kind,
                stageSummary,
                originalDuration,
                replayDuration,
                drift,
                majorDeviation));
        }

        foreach (var extra in remainingReplay.Values.OrderBy(stage => stage.StageName, StringComparer.Ordinal))
        {
            diffs.Add(new ReplayStageDiffRecord(
                extra.StageName,
                "extra_step",
                $"Replay produced unexpected stage '{extra.StageName}'.",
                0,
                DurationMs(extra),
                DurationMs(extra),
                true));
        }

        var summary = mismatches.Count == 0 && diffs.All(diff => !diff.MajorDeviation)
            ? "Replay diff matched the original timeline."
            : $"Replay diff found {diffs.Count(diff => diff.MajorDeviation)} major deviation(s).";

        return new ReplayDiffResult(runPath, mismatches.Count == 0 && diffs.All(diff => !diff.MajorDeviation), summary, diffs, mismatches.ToArray());
    }

    private static void ClearReplayError(string runPath)
    {
        var replayErrorPath = ReplayErrorPath(runPath);
        if (File.Exists(replayErrorPath))
            File.Delete(replayErrorPath);
    }

    private static long DurationMs(RunStageRecord stage)
        => Math.Max(0L, (long)(stage.EndedUtc - stage.StartedUtc).TotalMilliseconds);

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
