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

    public BuilderExecutionService(
        IRuntimeBridge runtimeBridge,
        ArtifactManager artifactManager,
        ToolRegistry toolRegistry,
        IBuilderProofCommandRunner? builderProofCommandRunner = null,
        IBuilderStrongerTierResolver? builderStrongerTierResolver = null)
    {
        _runtimeBridge = runtimeBridge;
        _artifactManager = artifactManager;
        _toolRegistry = toolRegistry;
        _builderProofCommandRunner = builderProofCommandRunner ?? new ValidationCommandBuilderProofRunner();
        _builderStrongerTierResolver = builderStrongerTierResolver ?? new NullBuilderStrongerTierResolver();
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
        => Path.Combine(repoRoot, ".codex", "validation-ui", "builder-proof");

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

    public static BuilderProofHistory LoadBuilderProofHistory(string repoRoot)
        => TryLoadBuilderProofArtifact(
            BuilderProofHistoryPathForRepo(repoRoot),
            new BuilderProofHistory(20, Array.Empty<BuilderProofHistoryEntry>()));

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
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderDefaultRouteDecision(runFolder)
            : null;

    public static BuilderReadinessContradictions? LoadBuilderReadinessContradictions(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderReadinessContradictions?>(BuilderReadinessContradictionsPath(runFolder), null);

    public static BuilderReadinessContradictions? LoadLatestBuilderReadinessContradictions(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderReadinessContradictions(runFolder)
            : null;

    public static BuilderLaunchDefaultDecision? LoadBuilderLaunchDefaultDecision(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderLaunchDefaultDecision?>(BuilderLaunchDefaultDecisionPath(runFolder), null);

    public static BuilderLaunchDefaultDecision? LoadLatestBuilderLaunchDefaultDecision(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderLaunchDefaultDecision(runFolder)
            : null;

    public static BuilderRouteOverrideEvidence? LoadBuilderRouteOverrideEvidence(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRouteOverrideEvidence?>(BuilderRouteOverrideEvidencePath(runFolder), null);

    public static BuilderRouteOverrideEvidence? LoadLatestBuilderRouteOverrideEvidence(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderRouteOverrideEvidence(runFolder)
            : null;

    public static BuilderRouteReviewCandidates? LoadBuilderPolicyReviewCandidates(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRouteReviewCandidates?>(BuilderPolicyReviewCandidatesPath(runFolder), null);

    public static BuilderRouteReviewCandidates? LoadLatestBuilderPolicyReviewCandidates(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderPolicyReviewCandidates(runFolder)
            : null;

    public static BuilderRouteReconfirmation? LoadBuilderRouteReconfirmation(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderRouteReconfirmation?>(BuilderRouteReconfirmationPath(runFolder), null);

    public static BuilderRouteReconfirmation? LoadLatestBuilderRouteReconfirmation(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderRouteReconfirmation(runFolder)
            : null;

    public static BuilderDefaultRouteRecovery? LoadBuilderDefaultRouteRecovery(string runFolder)
        => TryLoadBuilderProofArtifact<BuilderDefaultRouteRecovery?>(BuilderDefaultRouteRecoveryPath(runFolder), null);

    public static BuilderDefaultRouteRecovery? LoadLatestBuilderDefaultRouteRecovery(string repoRoot)
        => LoadLatestBuilderProofRun(repoRoot) is { RunFolder: { } runFolder }
            ? LoadBuilderDefaultRouteRecovery(runFolder)
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
            DateTimeOffset.UtcNow);
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
            proofScope);
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

            var intake = BuildBuilderRequestIntake(
                runFolder,
                requestDecision,
                stability,
                routingPlan,
                splitPlan,
                tieredRoutingPolicy,
                splitOutcome,
                defaultRouteDecision);
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
                defaultRouteDecision);
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
            defaultRouteSuspended);
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

    private static BuilderRequestIntake BuildBuilderRequestIntake(
        string runFolder,
        BuilderRequestPolicyDecision requestDecision,
        BuilderPolicyStability stability,
        BuilderModelRoutingPlan routingPlan,
        BuilderSplitFirstPlan? splitPlan,
        BuilderTieredRoutingPolicy? tieredRoutingPolicy,
        BuilderSplitFirstOutcome? splitOutcome,
        BuilderDefaultRouteDecision defaultRouteDecision)
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
                splitPlan?.ArtifactPath ?? string.Empty,
                tieredRoutingPolicy?.ArtifactPath ?? string.Empty,
                splitOutcome?.ArtifactPath ?? string.Empty
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"{requestDecision.TargetLabel} is {intakeState}. {normalizationState} Route source={defaultRouteDecision.RouteSourceState}. Support={stability.SupportLevel}. {strongerTierRole}";

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
            defaultRouteDecision.ReasonSummary);
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
        BuilderDefaultRouteDecision defaultRouteDecision)
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
                splitPlan?.ArtifactPath ?? string.Empty,
                tieredRoutingPolicy?.ArtifactPath ?? string.Empty,
                splitOutcome?.ArtifactPath ?? string.Empty
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = BuildBuilderExecutionPrepSummary(intake, selectedRoute, rerunRepairExpectationLevel, routingPlan, splitPlanRequired, defaultRouteDecision);

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
            defaultRouteDecision.ReasonSummary);
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
        BuilderDefaultRouteDecision defaultRouteDecision)
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
        return $"{intake.TargetLabel} is prepared on route {selectedRoute}. Route source={defaultRouteDecision.RouteSourceState}. Repair/rerun expectation={rerunRepairExpectationLevel}. {splitText} {escalationText}";
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
        bool defaultRouteSuspended)
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
            DateTimeOffset.UtcNow);
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
            DateTimeOffset.UtcNow);
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
            DateTimeOffset.UtcNow);
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
    string RoutingRecommendationState = "");

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
    DateTimeOffset RecordedUtc);

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
    string DefaultRouteReason = "");

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
    string DefaultRouteReason = "");

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
    DateTimeOffset RecordedUtc);

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
