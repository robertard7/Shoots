using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI.Narration;
using Shoots.Runtime.Loader;
using Shoots.Runtime.Runner;

return await MainAsync(args).ConfigureAwait(false);

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: shoots-runtime --plan <path> | run --scenario builder_smoke --project <path> --out <path> | replay --run <runDir>");
        return 1;
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
                return 1;
            }

            planPath = args[++i];
            continue;
        }

        Console.Error.WriteLine($"unknown argument: {arg}");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(planPath))
    {
        Console.Error.WriteLine("--plan is required");
        return 1;
    }

    if (!File.Exists(planPath))
    {
        Console.Error.WriteLine($"plan file not found: {planPath}");
        return 1;
    }

    var json = File.ReadAllText(planPath, Encoding.UTF8);
    var plan = JsonSerializer.Deserialize<BuildPlan>(json);

    if (plan is null)
    {
        Console.Error.WriteLine("invalid plan payload");
        return 1;
    }

    if (!AiStepValidator.TryValidate(plan, out var aiError))
    {
        Console.Error.WriteLine(aiError?.ToString() ?? "invalid ai step");
        return 1;
    }

    var authorityError = ValidateAuthority(plan.Authority);
    if (authorityError is not null)
    {
        Console.Error.WriteLine(authorityError);
        return 1;
    }

    string computedHash;
    try
    {
        computedHash = BuildPlanHasher.ComputePlanId(plan.Request, plan.Authority, plan.Steps, plan.Artifacts);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (!string.Equals(plan.PlanId, computedHash, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("plan hash mismatch");
        return 1;
    }

    Console.WriteLine("plan_validated");
    return 0;
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
                if (!TryNextValue(args, ref i, "--scenario", out scenario)) return 1;
                break;
            case "--project":
                if (!TryNextValue(args, ref i, "--project", out projectPath)) return 1;
                break;
            case "--out":
                if (!TryNextValue(args, ref i, "--out", out outRoot)) return 1;
                break;
            default:
                Console.Error.WriteLine($"unknown argument: {arg}");
                return 1;
        }
    }

    if (!string.Equals(scenario, "builder_smoke", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("scenario must be builder_smoke");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
    {
        Console.Error.WriteLine("project directory is required and must exist");
        return 1;
    }

    var project = Path.GetFullPath(projectPath);
    var planPath = Path.Combine(project, "plan", "plan.json");
    var providerPath = Path.Combine(project, "provider.json");
    var envSelectedPath = Path.Combine(project, "env", "selected.json");
    var envDescriptorPath = Path.Combine(project, "env", "descriptor.json");

    if (!File.Exists(planPath) || !File.Exists(providerPath) || !File.Exists(envSelectedPath) || !File.Exists(envDescriptorPath))
    {
        Console.Error.WriteLine("project scaffold is incomplete (expected plan/provider/env files)");
        return 1;
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

    var versionStamp = "builder_smoke.v1";
    var runId = ComputeHashHex($"{planHash}|{providerHash}|{envHash}|{versionStamp}")[..16];

    var runDir = Path.Combine(outRoot, runId);
    var traceDir = Path.Combine(runDir, "trace");
    Directory.CreateDirectory(traceDir);

    using var narrator = new TextNarrator(runDir);
    Emit(narrator, "startup", "startup.begin", "Starting builder smoke scenario", new Dictionary<string, string>
    {
        ["projectId"] = projectId,
        ["scenario"] = "builder_smoke"
    });

    try
    {
        Emit(narrator, "plan", "plan.materialize.start", "Materializing plan", new Dictionary<string, string>());
        Emit(narrator, "plan", "plan.read", "Reading plan scaffold", new Dictionary<string, string> { ["path"] = RelativePath(project, planPath) });
        Emit(narrator, "plan", "plan.hash", "Plan hash loaded", new Dictionary<string, string> { ["planHash"] = planHash });
        Emit(narrator, "provider", "provider.read", "Reading provider scaffold", new Dictionary<string, string> { ["path"] = RelativePath(project, providerPath) });
        Emit(narrator, "provider", "provider.hash", "Provider hash loaded", new Dictionary<string, string> { ["providerHash"] = providerHash });
        Emit(narrator, "env", "env.read", "Reading environment scaffold", new Dictionary<string, string> { ["path"] = RelativePath(project, envSelectedPath) });
        Emit(narrator, "env", "env.hash", "Environment hash loaded", new Dictionary<string, string> { ["envHash"] = envHash });
        Emit(narrator, "execute", "execute.begin", "Executing deterministic builder steps", new Dictionary<string, string>());

        var steps = new[] { "SelectTool", "ApplyTool", "Complete" };
        var stepEvents = new List<object>();
        foreach (var step in steps)
        {
            var stepId = ComputeHashHex($"{planHash}|{step}")[..12];
            Emit(narrator, "execute", "execute.step.begin", "Running step", new Dictionary<string, string> { ["stepId"] = stepId, ["stepKind"] = step });
            Emit(narrator, "tool", "tool.invoke", "Invoking tool", new Dictionary<string, string> { ["toolId"] = "linux.noop.v1", ["stepId"] = stepId });
            Emit(narrator, "tool", "tool.stdout.tail", "Tool produced deterministic output", new Dictionary<string, string> { ["line"] = "noop" });
            Emit(narrator, "tool", "tool.exit", "Tool completed", new Dictionary<string, string> { ["exitCode"] = "0", ["stepId"] = stepId });
            Emit(narrator, "execute", "execute.step.end", "Step completed", new Dictionary<string, string> { ["stepId"] = stepId, ["stepKind"] = step });
            stepEvents.Add(new { stepId, kind = step, toolId = "linux.noop.v1", status = "completed" });
        }

        Emit(narrator, "execute", "execute.end", "Execution completed", new Dictionary<string, string>());

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

        Emit(narrator, "finalize", "finalize.write_artifacts", "Wrote run artifacts", new Dictionary<string, string> { ["runDir"] = runDir });
        Console.WriteLine(runDir);
        return 0;
    }
    catch (Exception ex)
    {
        Emit(narrator, "finalize", "finalize.failure", "Run failed", new Dictionary<string, string> { ["error"] = ex.GetType().Name, ["message"] = ex.Message });
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> ReplayAsync(string[] args)
{
    string? runDir = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--run", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryNextValue(args, ref i, "--run", out runDir)) return 1;
        }
        else
        {
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 1;
        }
    }

    if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
    {
        Console.Error.WriteLine("--run directory is required");
        return 1;
    }

    var resolved = Path.GetFullPath(runDir);
    using var narrator = new TextNarrator(resolved);
    Emit(narrator, "replay", "replay.begin", "Starting replay", new Dictionary<string, string> { ["runDir"] = resolved });

    var runJsonPath = Path.Combine(resolved, "run.json");
    var hashesPath = Path.Combine(resolved, "hashes.json");
    var tracePath = Path.Combine(resolved, "trace", "events.ndjson");

    if (!File.Exists(runJsonPath) || !File.Exists(hashesPath) || !File.Exists(tracePath))
    {
        Emit(narrator, "replay", "replay.result", "Replay failed due to missing files", new Dictionary<string, string> { ["result"] = "fail" });
        Console.Error.WriteLine("run directory missing required files");
        return 1;
    }

    var runDoc = JsonDocument.Parse(await File.ReadAllTextAsync(runJsonPath).ConfigureAwait(false));
    var hashesDoc = JsonDocument.Parse(await File.ReadAllTextAsync(hashesPath).ConfigureAwait(false));

    var planHash = runDoc.RootElement.GetProperty("planHash").GetString() ?? string.Empty;
    var providerHash = runDoc.RootElement.GetProperty("providerHash").GetString() ?? string.Empty;
    var envHash = runDoc.RootElement.GetProperty("envHash").GetString() ?? string.Empty;
    var runId = runDoc.RootElement.GetProperty("runId").GetString() ?? string.Empty;

    Emit(narrator, "replay", "replay.inputs", "Loaded replay inputs", new Dictionary<string, string>
    {
        ["planHash"] = planHash,
        ["providerHash"] = providerHash,
        ["envHash"] = envHash
    });

    var expectedRunId = ComputeHashHex($"{planHash}|{providerHash}|{envHash}|builder_smoke.v1")[..16];
    var expectedTraceHash = ComputeHashHex(await File.ReadAllTextAsync(tracePath).ConfigureAwait(false));
    var expectedManifestHash = ComputeHashHex(string.Join("\n", new[] { "run.json", "hashes.json", "result.json", "trace/events.ndjson", "narration/events.ndjson" }));

    var pass = string.Equals(runId, expectedRunId, StringComparison.Ordinal)
        && string.Equals(hashesDoc.RootElement.GetProperty("traceHash").GetString(), expectedTraceHash, StringComparison.Ordinal)
        && string.Equals(hashesDoc.RootElement.GetProperty("outputManifestHash").GetString(), expectedManifestHash, StringComparison.Ordinal);

    Emit(narrator, "replay", "replay.hash.compare", "Compared replay hashes", new Dictionary<string, string>
    {
        ["expectedRunId"] = expectedRunId,
        ["actualRunId"] = runId,
        ["result"] = pass ? "pass" : "fail"
    });

    var replay = new
    {
        runId,
        pass,
        summary = pass ? "Replay matched deterministic hash invariants." : "Replay hash invariants failed.",
        expectedRunId,
        actualRunId = runId,
        expectedTraceHash,
        actualTraceHash = hashesDoc.RootElement.GetProperty("traceHash").GetString(),
        expectedManifestHash,
        actualManifestHash = hashesDoc.RootElement.GetProperty("outputManifestHash").GetString()
    };

    await File.WriteAllTextAsync(Path.Combine(resolved, "replay.json"), JsonSerializer.Serialize(replay, JsonOptions())).ConfigureAwait(false);
    Emit(narrator, "replay", "replay.result", "Replay completed", new Dictionary<string, string> { ["result"] = pass ? "pass" : "fail" });
    Console.WriteLine(pass ? "replay_pass" : "replay_fail");
    return pass ? 0 : 1;
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

static string RelativePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
