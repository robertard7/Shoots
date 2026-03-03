using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI.Narration;
using Shoots.Runtime.Loader;
using Shoots.Runtime.Runner;

return await MainAsync(args).ConfigureAwait(false);

static class ExitCodes
{
    public const int Ok = 0;
    public const int PlanInvalid = 10;
    public const int EnvMissing = 20;
    public const int ProviderUnavailable = 30;
    public const int ToolExecFailed = 40;
    public const int HashMismatch = 50;
    public const int ReplayDiverged = 60;
    public const int IoDenied = 70;
    public const int Unknown = 99;
}

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: shoots-runtime --plan <path> | run --scenario builder_smoke --project <path> --out <path> | replay --run <runDir>");
        return ExitCodes.PlanInvalid;
    }

    if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
    {
        return await RunScenarioAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
    }

    if (string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
    {
        return await ReplayAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
    }

    return ValidatePlan(args);
}

static int ValidatePlan(string[] args)
{
    string? planPath = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--plan", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("missing value for --plan");
                return ExitCodes.PlanInvalid;
            }

            planPath = args[++i];
            continue;
        }

        Console.Error.WriteLine($"unknown argument: {arg}");
        return ExitCodes.PlanInvalid;
    }

    if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath))
    {
        Console.Error.WriteLine("--plan is required and must exist");
        return ExitCodes.PlanInvalid;
    }

    var json = File.ReadAllText(planPath, Encoding.UTF8);
    var plan = JsonSerializer.Deserialize<BuildPlan>(json);

    if (plan is null)
    {
        Console.Error.WriteLine("invalid plan payload");
        return ExitCodes.PlanInvalid;
    }

    if (!AiStepValidator.TryValidate(plan, out var aiError))
    {
        Console.Error.WriteLine(aiError?.ToString() ?? "invalid ai step");
        return ExitCodes.PlanInvalid;
    }

    var authorityError = ValidateAuthority(plan.Authority);
    if (authorityError is not null)
    {
        Console.Error.WriteLine(authorityError);
        return ExitCodes.PlanInvalid;
    }

    string computedHash;
    try
    {
        computedHash = BuildPlanHasher.ComputePlanId(plan.Request, plan.Authority, plan.Steps, plan.Artifacts);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return ExitCodes.HashMismatch;
    }

    if (!string.Equals(plan.PlanId, computedHash, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("plan hash mismatch");
        return ExitCodes.HashMismatch;
    }

    Console.WriteLine("plan_validated");
    return ExitCodes.Ok;
}

static async Task<int> RunScenarioAsync(string[] args)
{
    string? scenario = null;
    string? projectPath = null;
    string outRoot = Path.Combine("artifacts", "run");

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--scenario":
                if (!TryNextValue(args, ref i, "--scenario", out scenario)) return ExitCodes.PlanInvalid;
                break;
            case "--project":
                if (!TryNextValue(args, ref i, "--project", out projectPath)) return ExitCodes.PlanInvalid;
                break;
            case "--out":
                if (!TryNextValue(args, ref i, "--out", out outRoot)) return ExitCodes.PlanInvalid;
                break;
            default:
                Console.Error.WriteLine($"unknown argument: {arg}");
                return ExitCodes.PlanInvalid;
        }
    }

    if (!string.Equals(scenario, "builder_smoke", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("scenario must be builder_smoke");
        return ExitCodes.PlanInvalid;
    }

    if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
    {
        Console.Error.WriteLine("project directory is required and must exist");
        return ExitCodes.EnvMissing;
    }

    var project = Path.GetFullPath(projectPath);
    var planPath = Path.Combine(project, "plan", "plan.json");
    var providerPath = Path.Combine(project, "provider.json");
    var envSelectedPath = Path.Combine(project, "env", "selected.json");
    var envDescriptorPath = Path.Combine(project, "env", "descriptor.json");

    if (!File.Exists(planPath) || !File.Exists(providerPath) || !File.Exists(envSelectedPath) || !File.Exists(envDescriptorPath))
    {
        Console.Error.WriteLine("project scaffold is incomplete (expected plan/provider/env files)");
        return ExitCodes.PlanInvalid;
    }

    var planDoc = JsonDocument.Parse(await File.ReadAllTextAsync(planPath).ConfigureAwait(false));
    var providerDoc = JsonDocument.Parse(await File.ReadAllTextAsync(providerPath).ConfigureAwait(false));
    var envSelectedDoc = JsonDocument.Parse(await File.ReadAllTextAsync(envSelectedPath).ConfigureAwait(false));
    var envDescriptorDoc = JsonDocument.Parse(await File.ReadAllTextAsync(envDescriptorPath).ConfigureAwait(false));

    var planHash = planDoc.RootElement.GetProperty("planHash").GetString() ?? string.Empty;
    var projectId = planDoc.RootElement.GetProperty("projectId").GetString() ?? string.Empty;
    var providerKind = providerDoc.RootElement.GetProperty("kind").GetString() ?? string.Empty;
    var providerEndpoint = providerDoc.RootElement.TryGetProperty("endpoint", out var ep) ? ep.GetString() ?? string.Empty : string.Empty;
    var providerHash = providerDoc.RootElement.TryGetProperty("configHash", out var cfg) && !string.IsNullOrWhiteSpace(cfg.GetString())
        ? cfg.GetString()!
        : ComputeHashHex($"{providerKind}|{providerEndpoint}");
    var envId = envSelectedDoc.RootElement.GetProperty("environmentId").GetString() ?? string.Empty;
    var envHash = envDescriptorDoc.RootElement.TryGetProperty("descriptorHash", out var eh) && !string.IsNullOrWhiteSpace(eh.GetString())
        ? eh.GetString()!
        : ComputeHashHex(envId);

    var versionStamp = "builder_smoke.v2";
    var runId = ComputeHashHex($"{planHash}|{providerHash}|{envHash}|{versionStamp}")[..16];

    var runDir = Path.Combine(outRoot, runId);
    var traceDir = Path.Combine(runDir, "trace");
    Directory.CreateDirectory(traceDir);

    using var narrator = new TextNarrator(runDir);
    var refs = BaseArtifactRefs();
    Emit(narrator, "startup", "startup.begin", "Starting builder smoke scenario", new Dictionary<string, string>
    {
        ["runId"] = runId,
        ["planHash"] = planHash,
        ["providerHash"] = providerHash,
        ["envHash"] = envHash,
        ["artifactRefs"] = refs
    });

    try
    {
        if (string.IsNullOrWhiteSpace(providerKind))
        {
            EmitFailure(narrator, "provider", "provider.unavailable", "Provider kind missing", runId, planHash, providerHash, envHash, refs);
            return ExitCodes.ProviderUnavailable;
        }

        if (string.IsNullOrWhiteSpace(envId))
        {
            EmitFailure(narrator, "env", "env.missing", "Environment id missing", runId, planHash, providerHash, envHash, refs);
            return ExitCodes.EnvMissing;
        }

        if (envDescriptorDoc.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
        {
            var capList = caps.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (capList.Length == 0)
            {
                EmitFailure(narrator, "env", "env.capability.missing", "Environment capabilities are empty", runId, planHash, providerHash, envHash, refs);
                return ExitCodes.EnvMissing;
            }
        }

        Emit(narrator, "plan", "plan.materialize.start", "Materializing plan", IdentityData(runId, planHash, providerHash, envHash, refs));
        Emit(narrator, "plan", "plan.read", "Reading plan scaffold", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["path"] = RelativePath(project, planPath) }));
        Emit(narrator, "plan", "plan.hash", "Plan hash loaded", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["planHash"] = planHash }));
        Emit(narrator, "provider", "provider.read", "Reading provider scaffold", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["path"] = RelativePath(project, providerPath) }));
        Emit(narrator, "provider", "provider.hash", "Provider hash loaded", IdentityData(runId, planHash, providerHash, envHash, refs));
        Emit(narrator, "env", "env.read", "Reading environment scaffold", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["path"] = RelativePath(project, envSelectedPath) }));
        Emit(narrator, "env", "env.hash", "Environment hash loaded", IdentityData(runId, planHash, providerHash, envHash, refs));
        Emit(narrator, "execute", "execute.begin", "Executing deterministic builder steps", IdentityData(runId, planHash, providerHash, envHash, refs));

        var planNetworkDisabled = planDoc.RootElement.TryGetProperty("tool.network_disabled", out var nd) && nd.ValueKind == JsonValueKind.True;
        var stepEvents = new List<object>();
        var stepSummaries = new List<Dictionary<string, string>>();
        var planSteps = ResolveSteps(planDoc.RootElement, planHash);

        if (!ValidateResolvedSteps(planDoc.RootElement, envDescriptorDoc.RootElement, planSteps, out var validationErrorCode, out var validationSummary, out var validationDetails))
        {
            EmitFailure(narrator, "plan", validationErrorCode, validationSummary, runId, planHash, providerHash, envHash, refs, details: validationDetails);
            Console.Error.WriteLine(validationSummary);
            return ExitCodes.PlanInvalid;
        }

        foreach (var step in planSteps)
        {
            Emit(narrator, "execute", "execute.step.begin", "Running step", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["stepKind"] = step.Kind }));

            if (step.Kind == "RunTool")
            {
                var argsHash = ComputeHashHex(JsonSerializer.Serialize(step.Args));
                Emit(narrator, "tool", "tool.start", "Starting tool execution", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["toolId"] = step.ToolId, ["argsHash"] = argsHash }));

                if (planNetworkDisabled && step.RequiresNetwork)
                {
                    EmitFailure(narrator, "tool", "tool.network.blocked", "Tool blocked by network policy", runId, planHash, providerHash, envHash, refs, step.StepId);
                    return ExitCodes.ToolExecFailed;
                }

                var toolRoot = Path.Combine(runDir, "tool", step.StepId);
                Directory.CreateDirectory(toolRoot);
                var requestPath = Path.Combine(toolRoot, "request.json");
                var stdoutPath = Path.Combine(toolRoot, "stdout.txt");
                var stderrPath = Path.Combine(toolRoot, "stderr.txt");
                var resultPath = Path.Combine(toolRoot, "result.json");
                var exitPath = Path.Combine(toolRoot, "exit.json");
                var hashesPath = Path.Combine(toolRoot, "hashes.json");

                await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { step.StepId, step.ToolId, step.Args }, JsonOptions())).ConfigureAwait(false);
                await File.WriteAllTextAsync(stdoutPath, TruncateDeterministic("noop\n", 2048, 64)).ConfigureAwait(false);
                await File.WriteAllTextAsync(stderrPath, string.Empty).ConfigureAwait(false);
                await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { step.StepId, step.ToolId, status = "success" }, JsonOptions())).ConfigureAwait(false);
                await File.WriteAllTextAsync(exitPath, JsonSerializer.Serialize(new { exitCode = 0 }, JsonOptions())).ConfigureAwait(false);

                var stepHashes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exit.json"] = ComputeHashHex(await File.ReadAllTextAsync(exitPath).ConfigureAwait(false)),
                    ["request.json"] = ComputeHashHex(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false)),
                    ["result.json"] = ComputeHashHex(await File.ReadAllTextAsync(resultPath).ConfigureAwait(false)),
                    ["stderr.txt"] = ComputeHashHex(await File.ReadAllTextAsync(stderrPath).ConfigureAwait(false)),
                    ["stdout.txt"] = ComputeHashHex(await File.ReadAllTextAsync(stdoutPath).ConfigureAwait(false))
                };
                await File.WriteAllTextAsync(hashesPath, JsonSerializer.Serialize(stepHashes, JsonOptions())).ConfigureAwait(false);

                var outputHash = stepHashes["result.json"];
                Emit(narrator, "tool", "tool.complete", "Tool execution completed", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["toolId"] = step.ToolId, ["outputHash"] = outputHash }));
            }

            Emit(narrator, "execute", "execute.step.end", "Step completed", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["stepKind"] = step.Kind }));
            stepEvents.Add(new { step.StepId, kind = step.Kind, toolId = step.ToolId, status = "completed" });
            stepSummaries.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["durationBucket"] = "lt_1s",
                ["errorCode"] = string.Empty,
                ["expects"] = "status=completed",
                ["inputs"] = "plan/provider/env",
                ["kind"] = step.Kind,
                ["outputs"] = $"tool/{step.StepId}",
                ["status"] = "completed",
                ["stepId"] = step.StepId,
                ["toolId"] = step.ToolId
            });
        }

        Emit(narrator, "execute", "execute.end", "Execution completed", IdentityData(runId, planHash, providerHash, envHash, refs));

        var inputFiles = Directory.GetFiles(project, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(project, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var runRecord = new
        {
            runId,
            scenario = "builder_smoke",
            projectId,
            planHash,
            providerHash,
            envHash,
            commandList = new[] { "validate.scaffold", "materialize.plan", "simulate.execution", "write.results" },
            inputFileList = inputFiles,
            status = "completed"
        };

        await File.WriteAllTextAsync(Path.Combine(runDir, "run.json"), JsonSerializer.Serialize(runRecord, JsonOptions())).ConfigureAwait(false);

        var traceEvents = new[]
        {
            new { index = 0, type = "run.started", runId, scenario = "builder_smoke" },
            new { index = 1, type = "plan.validated", planHash },
            new { index = 2, type = "provider.validated", providerHash },
            new { index = 3, type = "environment.validated", envHash },
            new { index = 4, type = "run.completed", status = "completed" }
        };

        var tracePath = Path.Combine(traceDir, "events.ndjson");
        await File.WriteAllLinesAsync(tracePath, traceEvents.Select(e => JsonSerializer.Serialize(e))).ConfigureAwait(false);

        var traceHash = ComputeHashHex(await File.ReadAllTextAsync(tracePath).ConfigureAwait(false));
        var outputManifest = new[] { "run.json", "hashes.json", "result.json", "trace/events.ndjson", "narration/events.ndjson" };
        var outputManifestHash = ComputeHashHex(string.Join("\n", outputManifest));

        var hashes = new { runId, planHash, providerHash, envHash, traceHash, outputManifestHash };
        await File.WriteAllTextAsync(Path.Combine(runDir, "hashes.json"), JsonSerializer.Serialize(hashes, JsonOptions())).ConfigureAwait(false);

        var result = new { status = "completed", outputs = outputManifest, steps = stepEvents };
        await File.WriteAllTextAsync(Path.Combine(runDir, "result.json"), JsonSerializer.Serialize(result, JsonOptions())).ConfigureAwait(false);

        var stepsDir = Path.Combine(runDir, "steps");
        Directory.CreateDirectory(stepsDir);
        var sortedStepSummaries = stepSummaries.OrderBy(s => s["stepId"], StringComparer.Ordinal).ToArray();
        var stepSummaryPath = Path.Combine(stepsDir, "summary.ndjson");
        await File.WriteAllLinesAsync(stepSummaryPath, sortedStepSummaries.Select(s => JsonSerializer.Serialize(s, JsonOptions()))).ConfigureAwait(false);

        var runSummaryPath = Path.Combine(runDir, "run_summary.md");
        await File.WriteAllTextAsync(runSummaryPath, BuildRunSummary(runId, planHash, providerHash, envHash, sortedStepSummaries)).ConfigureAwait(false);

        Emit(narrator, "finalize", "finalize.write_artifacts", "Wrote run artifacts", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["runDir"] = runDir }));
        Console.WriteLine(runDir);
        return ExitCodes.Ok;
    }
    catch (UnauthorizedAccessException ex)
    {
        EmitFailure(narrator, "finalize", "io.denied", "Access denied while writing artifacts", runId, planHash, providerHash, envHash, refs, details: ex.Message);
        Console.Error.WriteLine(ex.Message);
        return ExitCodes.IoDenied;
    }
    catch (Exception ex)
    {
        EmitFailure(narrator, "finalize", "tool.exec.failed", "Scenario execution failed", runId, planHash, providerHash, envHash, refs, details: ex.Message);
        Console.Error.WriteLine(ex.Message);
        return ExitCodes.Unknown;
    }
}

static async Task<int> ReplayAsync(string[] args)
{
    string? runDir = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--run", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryNextValue(args, ref i, "--run", out runDir)) return ExitCodes.PlanInvalid;
        }
        else
        {
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return ExitCodes.PlanInvalid;
        }
    }

    if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
    {
        Console.Error.WriteLine("--run directory is required");
        return ExitCodes.EnvMissing;
    }

    var resolved = Path.GetFullPath(runDir);
    using var narrator = new TextNarrator(resolved);
    var refs = BaseArtifactRefs();
    Emit(narrator, "replay", "replay.begin", "Starting replay", new Dictionary<string, string> { ["artifactRefs"] = refs, ["runId"] = Path.GetFileName(resolved) });

    var runJsonPath = Path.Combine(resolved, "run.json");
    var hashesPath = Path.Combine(resolved, "hashes.json");
    var tracePath = Path.Combine(resolved, "trace", "events.ndjson");

    if (!File.Exists(runJsonPath) || !File.Exists(hashesPath) || !File.Exists(tracePath))
    {
        EmitFailure(narrator, "replay", "replay.diverged", "Replay failed due to missing files", Path.GetFileName(resolved), "", "", "", refs);
        Console.Error.WriteLine("run directory missing required files");
        return ExitCodes.EnvMissing;
    }

    var runDoc = JsonDocument.Parse(await File.ReadAllTextAsync(runJsonPath).ConfigureAwait(false));
    var hashesDoc = JsonDocument.Parse(await File.ReadAllTextAsync(hashesPath).ConfigureAwait(false));

    var planHash = runDoc.RootElement.GetProperty("planHash").GetString() ?? string.Empty;
    var providerHash = runDoc.RootElement.GetProperty("providerHash").GetString() ?? string.Empty;
    var envHash = runDoc.RootElement.GetProperty("envHash").GetString() ?? string.Empty;
    var runId = runDoc.RootElement.GetProperty("runId").GetString() ?? string.Empty;

    Emit(narrator, "replay", "replay.inputs", "Loaded replay inputs", IdentityData(runId, planHash, providerHash, envHash, refs));

    var expectedRunId = ComputeHashHex($"{planHash}|{providerHash}|{envHash}|builder_smoke.v2")[..16];
    var expectedTraceHash = ComputeHashHex(await File.ReadAllTextAsync(tracePath).ConfigureAwait(false));
    var expectedManifestHash = ComputeHashHex(string.Join("\n", new[] { "run.json", "hashes.json", "result.json", "trace/events.ndjson", "narration/events.ndjson" }));

    var actualTraceHash = hashesDoc.RootElement.GetProperty("traceHash").GetString() ?? string.Empty;
    var actualManifestHash = hashesDoc.RootElement.GetProperty("outputManifestHash").GetString() ?? string.Empty;

    var pass = string.Equals(runId, expectedRunId, StringComparison.Ordinal)
        && string.Equals(actualTraceHash, expectedTraceHash, StringComparison.Ordinal)
        && string.Equals(actualManifestHash, expectedManifestHash, StringComparison.Ordinal);

    Emit(narrator, "replay", "replay.hash.compare", "Compared replay hashes", new Dictionary<string, string>
    {
        ["runId"] = runId,
        ["expectedRunId"] = expectedRunId,
        ["actualRunId"] = runId,
        ["artifactRefs"] = refs,
        ["result"] = pass ? "pass" : "fail"
    });

    if (!pass)
    {
        var divergedKey = runId != expectedRunId ? "runId" : actualTraceHash != expectedTraceHash ? "traceHash" : "outputManifestHash";
        var diagnostics = new
        {
            errorCode = "replay.diverged",
            stepId = "replay.hash.compare",
            firstDifferingArtifact = divergedKey,
            expected = new { runId = expectedRunId, traceHash = expectedTraceHash, outputManifestHash = expectedManifestHash },
            actual = new { runId, traceHash = actualTraceHash, outputManifestHash = actualManifestHash }
        };
        await File.WriteAllTextAsync(Path.Combine(resolved, "replay_diagnostics.json"), JsonSerializer.Serialize(diagnostics, JsonOptions())).ConfigureAwait(false);

        EmitFailure(narrator, "replay", "replay.diverged", "Replay hash invariants failed", runId, planHash, providerHash, envHash, refs, "replay.hash.compare", details: divergedKey);
    }

    var replay = new
    {
        runId,
        pass,
        errorCode = pass ? string.Empty : "replay.diverged",
        summary = pass ? "Replay matched deterministic hash invariants." : "Replay hash invariants failed.",
        expectedRunId,
        actualRunId = runId,
        expectedTraceHash,
        actualTraceHash,
        expectedManifestHash,
        actualManifestHash
    };

    await File.WriteAllTextAsync(Path.Combine(resolved, "replay.json"), JsonSerializer.Serialize(replay, JsonOptions())).ConfigureAwait(false);
    Emit(narrator, "replay", "replay.result", "Replay completed", new Dictionary<string, string> { ["runId"] = runId, ["result"] = pass ? "pass" : "fail", ["artifactRefs"] = refs });
    Console.WriteLine(pass ? "replay_pass" : "replay_fail");
    return pass ? ExitCodes.Ok : ExitCodes.ReplayDiverged;
}

static bool TryNextValue(string[] args, ref int index, string argName, out string value)
{
    if (index + 1 >= args.Length)
    {
        Console.Error.WriteLine($"missing value for {argName}");
        value = string.Empty;
        return false;
    }

    value = args[++index];
    return true;
}

static string ComputeHashHex(string value)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };

static string? ValidateAuthority(DelegationAuthority authority)
{
    if (authority is null)
        return "missing authority";

    if (string.IsNullOrWhiteSpace(authority.ProviderId.Value))
        return "authority provider id is required";

    if (string.IsNullOrWhiteSpace(authority.PolicyId))
        return "delegation policy id is required";

    if (authority.Kind == ProviderKind.Delegated && !authority.AllowsDelegation)
        return "delegation authority rejected";

    return null;
}

static void Emit(INarrator narrator, string phase, string code, string message, IDictionary<string, string> data)
{
    narrator.Emit(new NarrationEvent(phase, code, message, data));
}

static void EmitFailure(INarrator narrator, string phase, string errorCode, string summary, string runId, string planHash, string providerHash, string envHash, string artifactRefs, string stepId = "", string details = "")
{
    var data = IdentityData(runId, planHash, providerHash, envHash, artifactRefs);
    if (!string.IsNullOrWhiteSpace(stepId)) data["stepId"] = stepId;
    narrator.Emit(new NarrationEvent(phase, "error", "Failure", data, errorCode, summary, details));
}

static string RelativePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

static string BaseArtifactRefs() => "json:run.json,json:hashes.json,json:result.json,ndjson:trace/events.ndjson,ndjson:narration/events.ndjson";

static Dictionary<string, string> IdentityData(string runId, string planHash, string providerHash, string envHash, string artifactRefs) => new(StringComparer.Ordinal)
{
    ["artifactRefs"] = artifactRefs,
    ["envHash"] = envHash,
    ["planHash"] = planHash,
    ["providerHash"] = providerHash,
    ["runId"] = runId
};

static Dictionary<string, string> Merge(Dictionary<string, string> left, Dictionary<string, string> right)
{
    foreach (var pair in right.OrderBy(p => p.Key, StringComparer.Ordinal))
    {
        left[pair.Key] = pair.Value;
    }

    return left;
}

static IReadOnlyList<(string StepId, string Kind, string ToolId, bool RequiresNetwork, Dictionary<string, object?> Args)> ResolveSteps(JsonElement plan, string planHash)
{
    if (plan.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
    {
        var parsed = new List<(string StepId, string Kind, string ToolId, bool RequiresNetwork, Dictionary<string, object?> Args)>();
        foreach (var s in stepsEl.EnumerateArray())
        {
            var kind = s.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "RunTool" : "RunTool";
            var toolId = s.TryGetProperty("toolId", out var toolEl) ? toolEl.GetString() ?? "linux.noop.v1" : "linux.noop.v1";
            var args = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (s.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argsEl.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    args[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    };
                }
            }

            var stepId = s.TryGetProperty("stepId", out var sid) ? sid.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(stepId))
            {
                stepId = ComputeHashHex($"{planHash}|{kind}|{toolId}|{JsonSerializer.Serialize(args)}")[..12];
            }

            var requiresNetwork = args.TryGetValue("requiresNetwork", out var rn) && rn is true;
            parsed.Add((stepId, kind, toolId, requiresNetwork, args));
        }

        return parsed;
    }

    return new[]
    {
        (ComputeHashHex($"{planHash}|SelectTool")[..12], "SelectTool", "linux.noop.v1", false, new Dictionary<string, object?>()),
        (ComputeHashHex($"{planHash}|RunTool")[..12], "RunTool", "linux.noop.v1", false, new Dictionary<string, object?>()),
        (ComputeHashHex($"{planHash}|Complete")[..12], "EmitArtifact", "linux.noop.v1", false, new Dictionary<string, object?>())
    };
}

static bool ValidateResolvedSteps(
    JsonElement plan,
    JsonElement envDescriptor,
    IReadOnlyList<(string StepId, string Kind, string ToolId, bool RequiresNetwork, Dictionary<string, object?> Args)> steps,
    out string errorCode,
    out string summary,
    out string details)
{
    errorCode = string.Empty;
    summary = string.Empty;
    details = string.Empty;

    var supportedKinds = StepKinds.Registry.Keys;
    var envCapabilities = LoadEnvironmentCapabilities(envDescriptor, out var hasExplicitCapabilities);

    if (plan.TryGetProperty("steps", out var rawSteps) && rawSteps.ValueKind == JsonValueKind.Array)
    {
        var i = 0;
        foreach (var s in rawSteps.EnumerateArray())
        {
            var kind = s.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? string.Empty : string.Empty;
            if (!supportedKinds.Contains(kind))
            {
                errorCode = "plan.step.kind.unknown";
                summary = "Plan contains unknown step kind.";
                details = $"index={i};kind={kind}";
                return false;
            }

            if (hasExplicitCapabilities && !StepKinds.IsSupportedInEnvironment(kind, envCapabilities))
            {
                errorCode = "plan.step.kind.unsupported_in_env";
                summary = "Plan step kind is not supported in selected environment.";
                details = $"index={i};kind={kind};capabilities={string.Join(',', envCapabilities.OrderBy(c => c, StringComparer.Ordinal))}";
                return false;
            }

            if (string.Equals(kind, "RunTool", StringComparison.Ordinal))
            {
                var toolId = s.TryGetProperty("toolId", out var toolEl) ? toolEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    errorCode = "plan.step.toolid.missing";
                    summary = "RunTool step is missing toolId.";
                    details = $"index={i}";
                    return false;
                }
            }

            if (s.TryGetProperty("args", out var argsEl) && argsEl.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            {
                errorCode = "plan.step.args.invalid";
                summary = "Plan step args must be an object.";
                details = $"index={i};kind={kind};valueKind={argsEl.ValueKind}";
                return false;
            }

            i++;
        }
    }

    if (steps.Count == 0)
    {
        errorCode = "plan.step.output.missing";
        summary = "Plan has no executable steps.";
        details = "steps.count=0";
        return false;
    }

    return true;
}

static HashSet<string> LoadEnvironmentCapabilities(JsonElement envDescriptor, out bool hasExplicitCapabilities)
{
    var capabilities = new HashSet<string>(StringComparer.Ordinal);
    hasExplicitCapabilities = envDescriptor.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array;
    if (!hasExplicitCapabilities)
    {
        return capabilities;
    }

    foreach (var cap in caps.EnumerateArray())
    {
        var v = cap.GetString();
        if (!string.IsNullOrWhiteSpace(v))
        {
            capabilities.Add(v!);
        }
    }

    return capabilities;
}

static string BuildRunSummary(string runId, string planHash, string providerHash, string envHash, IReadOnlyList<Dictionary<string, string>> stepSummaries)
{
    var lines = new List<string>
    {
        "# Run Summary",
        string.Empty,
        $"- runId: `{runId}`",
        $"- planHash: `{planHash}`",
        $"- providerHash: `{providerHash}`",
        $"- envHash: `{envHash}`",
        string.Empty,
        "## Steps",
        "| stepId | kind | toolId | status | errorCode | outputs |",
        "|---|---|---|---|---|---|"
    };

    foreach (var step in stepSummaries)
    {
        var stepId = step.TryGetValue("stepId", out var sid) ? sid : string.Empty;
        var kind = step.TryGetValue("kind", out var k) ? k : string.Empty;
        var toolId = step.TryGetValue("toolId", out var tid) ? tid : string.Empty;
        var status = step.TryGetValue("status", out var st) ? st : string.Empty;
        var errorCode = step.TryGetValue("errorCode", out var ec) ? ec : string.Empty;
        var outputs = step.TryGetValue("outputs", out var o) ? o : string.Empty;
        lines.Add($"| {EscapeTable(stepId)} | {EscapeTable(kind)} | {EscapeTable(toolId)} | {EscapeTable(status)} | {EscapeTable(errorCode)} | {EscapeTable(outputs)} |");
    }

    return string.Join("\n", lines) + "\n";
}

static string EscapeTable(string value) => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);

static string TruncateDeterministic(string value, int maxChars, int maxLines)
{
    var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
    var lines = normalized.Split('\n');
    if (lines.Length > maxLines)
    {
        lines = lines[..maxLines];
    }

    var joined = string.Join("\n", lines);
    return joined.Length <= maxChars ? joined : joined[..maxChars];
}

static class StepKinds
{
    public static IReadOnlyDictionary<string, string[]> Registry { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["SelectTool"] = new[] { "tool.select" },
        ["RunTool"] = new[] { "process" },
        ["Verify"] = new[] { "verify" },
        ["EmitArtifact"] = new[] { "filesystem" }
    };

    public static bool IsSupportedInEnvironment(string kind, HashSet<string> capabilities)
    {
        if (!Registry.TryGetValue(kind, out var required))
        {
            return false;
        }

        if (required.Length == 0)
        {
            return true;
        }

        return required.Any(capabilities.Contains);
    }
}
