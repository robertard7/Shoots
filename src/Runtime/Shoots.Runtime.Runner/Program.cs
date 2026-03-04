using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI.Narration;
using Shoots.Runtime.Language;
using Shoots.Runtime.Loader;
using Shoots.Runtime.Runner;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Runner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await MainAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return ExitCodes.Unknown;
        }
    }

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

            Emit(narrator, "provider", "provider.resolve.start", "Resolving provider adapter", IdentityData(runId, planHash, providerHash, envHash, refs));
            var providerRoot = Path.Combine(runDir, "provider");
            Directory.CreateDirectory(providerRoot);
            var providerRequest = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["providerKind"] = providerKind,
                ["endpoint"] = providerEndpoint,
                ["model"] = providerDoc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty,
                ["providerHash"] = providerHash,
                ["declaredCapabilities"] = envDescriptorDoc.RootElement.TryGetProperty("capabilities", out var providerCapsEl) && providerCapsEl.ValueKind == JsonValueKind.Array ? providerCapsEl.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>()
            };
            await File.WriteAllTextAsync(Path.Combine(providerRoot, "request.json"), JsonSerializer.Serialize(providerRequest, JsonOptions())).ConfigureAwait(false);
            Emit(narrator, "provider", "provider.resolve.end", "Resolved provider adapter", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["providerKind"] = providerKind }));
            Emit(narrator, "provider", "provider.invoke.start", "Preparing provider invocation", IdentityData(runId, planHash, providerHash, envHash, refs));
            var providerResult = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["providerKind"] = providerKind,
                ["endpoint"] = providerEndpoint,
                ["adapterVersion"] = versionStamp,
                ["providerHash"] = providerHash,
                ["status"] = "ready"
            };
            await File.WriteAllTextAsync(Path.Combine(providerRoot, "result.json"), JsonSerializer.Serialize(providerResult, JsonOptions())).ConfigureAwait(false);
            Emit(narrator, "provider", "provider.invoke.end", "Provider invocation prepared", IdentityData(runId, planHash, providerHash, envHash, refs));

            Emit(narrator, "plan", "plan.materialize.start", "Materializing plan", IdentityData(runId, planHash, providerHash, envHash, refs));
            Emit(narrator, "plan", "plan.read", "Reading plan scaffold", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["path"] = RelativePath(project, planPath) }));
            Emit(narrator, "plan", "plan.hash", "Plan hash loaded", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["planHash"] = planHash }));
            Emit(narrator, "provider", "provider.read", "Reading provider scaffold", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["path"] = RelativePath(project, providerPath) }));
            Emit(narrator, "provider", "provider.hash", "Provider hash loaded", IdentityData(runId, planHash, providerHash, envHash, refs));
            Emit(narrator, "env", "env.read", "Reading environment scaffold", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["path"] = RelativePath(project, envSelectedPath) }));
            Emit(narrator, "env", "env.hash", "Environment hash loaded", IdentityData(runId, planHash, providerHash, envHash, refs));
            Emit(narrator, "execute", "execute.begin", "Executing deterministic builder steps", IdentityData(runId, planHash, providerHash, envHash, refs));
            Emit(narrator, "builder", "builder.execute.start", "Starting builder execution", IdentityData(runId, planHash, providerHash, envHash, refs));

            var planNetworkDisabled = planDoc.RootElement.TryGetProperty("tool.network_disabled", out var nd) && nd.ValueKind == JsonValueKind.True;
            var stepEvents = new List<object>();
            var stepSummaries = new List<Dictionary<string, string>>();
            var retrievalHash = string.Empty;
            var synthesisEvidenceHash = string.Empty;
            RetrievalResult? retrievalResult = null;
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
                    Emit(narrator, "builder", "builder.execute.step.start", "Executing builder tool step", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["toolId"] = step.ToolId }));
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
                    Emit(narrator, "builder", "builder.execute.step.end", "Completed builder tool step", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["toolId"] = step.ToolId }));
                }

                if (step.Kind == "write_text.v1")
                {
                    var stepRoot = Path.Combine(runDir, "steps", step.StepId);
                    Directory.CreateDirectory(stepRoot);
                    var requestPath = Path.Combine(stepRoot, "request.json");
                    var stdoutPath = Path.Combine(stepRoot, "stdout.txt");
                    var stderrPath = Path.Combine(stepRoot, "stderr.txt");
                    var resultPath = Path.Combine(stepRoot, "result.json");
                    var exitPath = Path.Combine(stepRoot, "exit.json");
                    var hashesPath = Path.Combine(stepRoot, "hashes.json");

                    var targetPath = GetArgString(step.Args, "targetPath", string.Empty);
                    var text = GetArgString(step.Args, "text", string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
                    var absoluteTarget = ResolvePolicyPath(project, targetPath, out var policyError);
                    if (policyError is not null)
                    {
                        EmitFailure(narrator, "builder", policyError, "File policy rejected write target", runId, planHash, providerHash, envHash, refs, step.StepId, details: targetPath);
                        return ExitCodes.ToolExecFailed;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(absoluteTarget!)!);
                    await File.WriteAllTextAsync(absoluteTarget!, text + "\n").ConfigureAwait(false);
                    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { step.StepId, kind = step.Kind, targetPath, text }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(stdoutPath, TruncateDeterministic($"wrote:{targetPath}\n", 2048, 64)).ConfigureAwait(false);
                    await File.WriteAllTextAsync(stderrPath, string.Empty).ConfigureAwait(false);
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { status = "success", targetPath }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(exitPath, JsonSerializer.Serialize(new { exitCode = 0 }, JsonOptions())).ConfigureAwait(false);
                    await WriteHashesAsync(hashesPath, requestPath, stdoutPath, stderrPath, resultPath, exitPath);
                }

                if (step.Kind == "read_text.v1")
                {
                    var stepRoot = Path.Combine(runDir, "steps", step.StepId);
                    Directory.CreateDirectory(stepRoot);
                    var requestPath = Path.Combine(stepRoot, "request.json");
                    var stdoutPath = Path.Combine(stepRoot, "stdout.txt");
                    var stderrPath = Path.Combine(stepRoot, "stderr.txt");
                    var resultPath = Path.Combine(stepRoot, "result.json");
                    var exitPath = Path.Combine(stepRoot, "exit.json");
                    var hashesPath = Path.Combine(stepRoot, "hashes.json");

                    var targetPath = GetArgString(step.Args, "targetPath", string.Empty);
                    var maxChars = Math.Max(GetArgInt(step.Args, "maxChars", 8192), 1);
                    var absoluteTarget = ResolvePolicyPath(project, targetPath, out var policyError);
                    if (policyError is not null || !File.Exists(absoluteTarget))
                    {
                        EmitFailure(narrator, "builder", policyError ?? "runner.read.target_missing", "Read target missing", runId, planHash, providerHash, envHash, refs, step.StepId, details: targetPath);
                        return ExitCodes.ToolExecFailed;
                    }

                    var content = await File.ReadAllTextAsync(absoluteTarget!).ConfigureAwait(false);
                    var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
                    var truncated = normalized.Length > maxChars;
                    var excerpt = truncated ? normalized[..maxChars] + "[TRUNCATED_CHARS]" : normalized;

                    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { step.StepId, kind = step.Kind, targetPath, maxChars }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(stdoutPath, TruncateDeterministic(excerpt, 2048, 64)).ConfigureAwait(false);
                    await File.WriteAllTextAsync(stderrPath, string.Empty).ConfigureAwait(false);
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { status = "success", targetPath, truncated }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(exitPath, JsonSerializer.Serialize(new { exitCode = 0 }, JsonOptions())).ConfigureAwait(false);
                    await WriteHashesAsync(hashesPath, requestPath, stdoutPath, stderrPath, resultPath, exitPath);
                }

                if (step.Kind == "assert_contains.v1")
                {
                    var stepRoot = Path.Combine(runDir, "steps", step.StepId);
                    Directory.CreateDirectory(stepRoot);
                    var requestPath = Path.Combine(stepRoot, "request.json");
                    var stdoutPath = Path.Combine(stepRoot, "stdout.txt");
                    var stderrPath = Path.Combine(stepRoot, "stderr.txt");
                    var resultPath = Path.Combine(stepRoot, "result.json");
                    var exitPath = Path.Combine(stepRoot, "exit.json");
                    var hashesPath = Path.Combine(stepRoot, "hashes.json");

                    var targetPath = GetArgString(step.Args, "targetPath", string.Empty);
                    var needle = GetArgString(step.Args, "contains", string.Empty);
                    var absoluteTarget = ResolvePolicyPath(project, targetPath, out var policyError);
                    if (policyError is not null || !File.Exists(absoluteTarget))
                    {
                        EmitFailure(narrator, "builder", policyError ?? "runner.assert.target_missing", "Assert target missing", runId, planHash, providerHash, envHash, refs, step.StepId, details: targetPath);
                        return ExitCodes.ToolExecFailed;
                    }

                    var content = await File.ReadAllTextAsync(absoluteTarget!).ConfigureAwait(false);
                    var ok = content.Contains(needle, StringComparison.Ordinal);
                    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { step.StepId, kind = step.Kind, targetPath, contains = needle }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(stdoutPath, ok ? "assert:pass\n" : "assert:fail\n").ConfigureAwait(false);
                    await File.WriteAllTextAsync(stderrPath, ok ? string.Empty : $"missing:{needle}\n").ConfigureAwait(false);
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { status = ok ? "success" : "failed", targetPath }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(exitPath, JsonSerializer.Serialize(new { exitCode = ok ? 0 : 1 }, JsonOptions())).ConfigureAwait(false);
                    await WriteHashesAsync(hashesPath, requestPath, stdoutPath, stderrPath, resultPath, exitPath);
                    if (!ok)
                    {
                        EmitFailure(narrator, "builder", "builder.execute.assert_failed", "Assertion step failed", runId, planHash, providerHash, envHash, refs, step.StepId, details: targetPath);
                        return ExitCodes.ToolExecFailed;
                    }
                }

                if (step.Kind == "apply_unified_diff.v1")
                {
                    var stepRoot = Path.Combine(runDir, "steps", step.StepId);
                    Directory.CreateDirectory(stepRoot);
                    var requestPath = Path.Combine(stepRoot, "request.json");
                    var stdoutPath = Path.Combine(stepRoot, "stdout.txt");
                    var stderrPath = Path.Combine(stepRoot, "stderr.txt");
                    var resultPath = Path.Combine(stepRoot, "result.json");
                    var exitPath = Path.Combine(stepRoot, "exit.json");
                    var hashesPath = Path.Combine(stepRoot, "hashes.json");

                    var targetPath = GetArgString(step.Args, "targetPath", string.Empty);
                    var diffText = GetArgString(step.Args, "diffText", string.Empty);
                    var absoluteTarget = ResolvePolicyPath(project, targetPath, out var policyError);
                    if (policyError is not null || !File.Exists(absoluteTarget))
                    {
                        EmitFailure(narrator, "builder", policyError ?? "builder.patch.target_missing", "Patch target missing", runId, planHash, providerHash, envHash, refs, step.StepId, details: targetPath);
                        return ExitCodes.ToolExecFailed;
                    }

                    var content = await File.ReadAllTextAsync(absoluteTarget!).ConfigureAwait(false);
                    if (!TryApplySimpleUnifiedDiff(content, diffText, out var patched, out var patchError))
                    {
                        EmitFailure(narrator, "builder", patchError ?? "builder.patch.invalid", "Failed to apply patch", runId, planHash, providerHash, envHash, refs, step.StepId, details: targetPath);
                        return ExitCodes.ToolExecFailed;
                    }

                    await File.WriteAllTextAsync(absoluteTarget!, patched).ConfigureAwait(false);
                    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { step.StepId, kind = step.Kind, targetPath, diffText }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(stdoutPath, "patch:applied\n").ConfigureAwait(false);
                    await File.WriteAllTextAsync(stderrPath, string.Empty).ConfigureAwait(false);
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { status = "success", targetPath }, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(exitPath, JsonSerializer.Serialize(new { exitCode = 0 }, JsonOptions())).ConfigureAwait(false);
                    await WriteHashesAsync(hashesPath, requestPath, stdoutPath, stderrPath, resultPath, exitPath);
                }

                if (step.Kind == "retrieve_context.v1")
                {
                    Emit(narrator, "retrieval", "retrieval.start", "Starting retrieval step", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId }));
                    Emit(narrator, "retrieval", "retrieval.slice.start", "Building retrieval slice", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId }));

                    var retrievalRoot = Path.Combine(runDir, "retrieval");
                    Directory.CreateDirectory(retrievalRoot);

                    var maxFiles = Math.Max(GetArgInt(step.Args, "maxFiles", 8), 1);
                    var maxTotalBytes = Math.Max(GetArgInt(step.Args, "maxTotalBytes", 120000), 2048);
                    var maxFileBytes = Math.Max(GetArgInt(step.Args, "maxFileBytes", 12000), 1024);
                    var maxLines = Math.Max(GetArgInt(step.Args, "maxLines", 2000), 100);
                    var baseRequest = new RetrievalQueryRequest
                    {
                        Root = project,
                        QueryText = GetArgString(step.Args, "queryText", "build plan context"),
                        SliceRequest = new RepoSliceRequest
                        {
                            Root = project,
                            IncludeGlobs = GetArgStringList(step.Args, "includeGlobs", new[] { "src/**/*.cs", "docs/*.md" }),
                            ExcludeGlobs = GetArgStringList(step.Args, "excludeGlobs", new[] { "**/bin/**", "**/obj/**" }),
                            MaxFiles = maxFiles,
                            MaxTotalBytes = maxTotalBytes,
                            MaxBytesPerFile = maxFileBytes,
                            LineCap = 400,
                            NormalizeEol = true,
                            AllowBinary = false
                        },
                        MaxFiles = maxFiles,
                        MaxTotalBytes = maxTotalBytes,
                        MaxFileBytes = maxFileBytes,
                        MaxLinesPerFile = Math.Max(GetArgInt(step.Args, "maxLinesPerFile", 400), 1),
                        MaxContextBytes = maxTotalBytes,
                        Budget = new ContextBudget
                        {
                            MaxBytes = maxTotalBytes,
                            MaxLines = maxLines,
                            MaxFiles = maxFiles,
                            MaxTokensEstimate = GetArgInt(step.Args, "maxTokensEstimate", 0) > 0 ? GetArgInt(step.Args, "maxTokensEstimate", 0) : null
                        },
                        Scoring = RetrievalScoring.LexicalTfidfV1
                    };

                    var queries = GetArgStringList(step.Args, "queries", Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                    var queryFile = GetArgString(step.Args, "queryFile", string.Empty);
                    if (queries.Count == 0 && !string.IsNullOrWhiteSpace(queryFile))
                    {
                        var resolvedQueryFile = ResolvePolicyPath(project, queryFile, out var queryPolicyError);
                        if (queryPolicyError is not null || resolvedQueryFile is null || !File.Exists(resolvedQueryFile))
                        {
                            EmitFailure(narrator, "retrieval", queryPolicyError ?? "retrieval.query_file.missing", "Query file missing", runId, planHash, providerHash, envHash, refs, step.StepId, details: queryFile);
                            return ExitCodes.ToolExecFailed;
                        }

                        queries = (await File.ReadAllLinesAsync(resolvedQueryFile).ConfigureAwait(false)).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                    }

                    if (queries.Count == 0)
                    {
                        queries.Add(baseRequest.QueryText);
                    }

                    var retrievalService = new RetrievalService();
                    var perQueryResults = new List<RetrievalResult>();
                    var queryPackEntries = new List<Dictionary<string, object?>>();
                    foreach (var (query, index) in queries.Select((q, i) => (q, i)))
                    {
                        var req = baseRequest with { QueryText = query };
                        var queryResult = retrievalService.Retrieve(req).Normalize();
                        if (!string.IsNullOrWhiteSpace(queryResult.ErrorCode))
                        {
                            EmitFailure(narrator, "retrieval", "retrieval.error", "Retrieval step failed", runId, planHash, providerHash, envHash, refs, step.StepId, details: queryResult.ErrorCode + ";" + queryResult.ErrorMessage);
                            return ExitCodes.ToolExecFailed;
                        }

                        perQueryResults.Add(queryResult);
                        queryPackEntries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["index"] = index,
                            ["query"] = query,
                            ["requestHash"] = req.ComputeQueryHash(),
                            ["retrievalHash"] = RetrievalService.ComputeRetrievalHash(queryResult),
                            ["hitCount"] = queryResult.Hits.Count
                        });
                    }

                    var mergedHits = perQueryResults.SelectMany(x => x.Hits)
                        .GroupBy(x => x.Path, StringComparer.Ordinal)
                        .Select(g => g.OrderByDescending(h => h.Score).ThenByDescending(h => h.TokensMatched).ThenBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.FirstMatchOffset).First())
                        .OrderByDescending(h => h.Score).ThenByDescending(h => h.TokensMatched).ThenBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.FirstMatchOffset)
                        .Take(maxFiles)
                        .ToArray();

                    var mergedTruncationFlags = perQueryResults.SelectMany(x => x.Stats.TruncatedFlags).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    var mergedScoring = mergedHits.Select(h => new RetrievalScoringTrace { HitId = h.HitId, Path = h.Path, TokensMatched = h.TokensMatched, Score = h.Score, PathHash = h.PathHash, FirstMatchOffset = h.FirstMatchOffset }).ToArray();
                    var mergedBytes = mergedHits.Sum(h => Encoding.UTF8.GetByteCount(h.Excerpt));
                    var mergedLines = mergedHits.Sum(h => h.Excerpt.Length == 0 ? 0 : h.Excerpt.Count(c => c == '\n') + 1);
                    var retrieval = new RetrievalResult
                    {
                        QueryHash = ComputeHashHex(string.Join("\n", queryPackEntries.Select(x => x["requestHash"]?.ToString() ?? string.Empty))),
                        SliceHash = perQueryResults.FirstOrDefault()?.SliceHash ?? string.Empty,
                        Hits = mergedHits,
                        SliceDecisionTrace = perQueryResults.FirstOrDefault()?.SliceDecisionTrace ?? Array.Empty<RepoSliceDecision>(),
                        ScoringTrace = mergedScoring,
                        Stats = new RetrievalStats
                        {
                            CandidateFiles = perQueryResults.FirstOrDefault()?.Stats.CandidateFiles ?? 0,
                            ReturnedFiles = mergedHits.Length,
                            ReturnedBytes = mergedBytes,
                            BytesOut = mergedBytes,
                            LinesOut = mergedLines,
                            FilesOut = mergedHits.Length,
                            TruncatedFlags = mergedTruncationFlags
                        }
                    }.Normalize();

                    retrievalResult = retrieval;

                    var requestPath = Path.Combine(retrievalRoot, "request.json");
                    var queryPackPath = Path.Combine(retrievalRoot, "query_pack.json");
                    var resultPath = Path.Combine(retrievalRoot, "result.json");
                    var statsPath = Path.Combine(retrievalRoot, "stats.json");
                    var hitsPath = Path.Combine(retrievalRoot, "hits.ndjson");
                    var scoringPath = Path.Combine(retrievalRoot, "scoring.ndjson");
                    var decisionsPath = Path.Combine(runDir, "slice", "decisions.ndjson");
                    Directory.CreateDirectory(Path.GetDirectoryName(decisionsPath)!);
                    var packPath = Path.Combine(retrievalRoot, "context_pack.txt");
                    var hashPath = Path.Combine(retrievalRoot, "hashes.json");

                    var queryPack = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["queries"] = queryPackEntries.OrderBy(x => Convert.ToInt32(x["index"])).ToArray(),
                        ["sortedRequestHashes"] = queryPackEntries.Select(x => x["requestHash"]?.ToString() ?? string.Empty).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                    };
                    var contextPack = RetrievalService.BuildContextPack(runId, planHash, retrieval, baseRequest.Budget);
                    await File.WriteAllTextAsync(queryPackPath, JsonSerializer.Serialize(queryPack, JsonOptions())).ConfigureAwait(false);
                    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(baseRequest.Normalize(), RepoSliceJson.Options)).ConfigureAwait(false);
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(retrieval, RepoSliceJson.Options)).ConfigureAwait(false);
                    await File.WriteAllTextAsync(statsPath, JsonSerializer.Serialize(retrieval.Stats, RepoSliceJson.Options)).ConfigureAwait(false);
                    await File.WriteAllLinesAsync(hitsPath, retrieval.Hits.Select(h => JsonSerializer.Serialize(h, RepoSliceJson.Options))).ConfigureAwait(false);
                    await File.WriteAllLinesAsync(scoringPath, retrieval.ScoringTrace.Select(h => JsonSerializer.Serialize(h, RepoSliceJson.Options))).ConfigureAwait(false);
                    await File.WriteAllLinesAsync(decisionsPath, retrieval.SliceDecisionTrace.Select(d => JsonSerializer.Serialize(d, RepoSliceJson.Options))).ConfigureAwait(false);
                    await File.WriteAllTextAsync(packPath, contextPack).ConfigureAwait(false);

                    retrievalHash = RetrievalService.ComputeRetrievalHash(retrieval);
                    var retrievalHashes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["queryHash"] = retrieval.QueryHash,
                        ["sliceHash"] = retrieval.SliceHash,
                        ["requestHash"] = ComputeHashHex(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false)),
                        ["queryPackHash"] = ComputeHashHex(await File.ReadAllTextAsync(queryPackPath).ConfigureAwait(false)),
                        ["retrievalHash"] = retrievalHash,
                        ["contextHash"] = ComputeHashHex(contextPack),
                        ["scoringHash"] = ComputeHashHex(await File.ReadAllTextAsync(scoringPath).ConfigureAwait(false)),
                        ["sliceDecisionHash"] = ComputeHashHex(await File.ReadAllTextAsync(decisionsPath).ConfigureAwait(false))
                    };
                    await File.WriteAllTextAsync(hashPath, JsonSerializer.Serialize(retrievalHashes, JsonOptions())).ConfigureAwait(false);

                    Emit(narrator, "retrieval", "retrieval.slice.done", "Retrieval slice built", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["sliceHash"] = retrieval.SliceHash }));
                    Emit(narrator, "retrieval", "retrieval.rank.start", "Ranking retrieval hits", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["queryHash"] = retrieval.QueryHash }));
                    Emit(narrator, "retrieval", "retrieval.rank.done", "Ranked retrieval hits", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["hits"] = retrieval.Hits.Count.ToString() }));
                    foreach (var hit in retrieval.Hits.Take(10))
                    {
                        Emit(narrator, "retrieval", "retrieval.pack.start", "Retrieval hit summary", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["path"] = hit.Path, ["score"] = hit.Score.ToString(), ["reasonCodes"] = string.Join(',', hit.ReasonCodes) }));
                    }

                    Emit(narrator, "builder", "builder.decision.context_budget", "Applied context budgets", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["maxBytes"] = baseRequest.Budget.MaxBytes.ToString(), ["maxLines"] = baseRequest.Budget.MaxLines.ToString(), ["maxFiles"] = baseRequest.Budget.MaxFiles.ToString(), ["truncated"] = string.Join(',', retrieval.Stats.TruncatedFlags) }));
                    Emit(narrator, "retrieval", "retrieval.pack.done", "Built retrieval context pack", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["retrievalHash"] = retrievalHash }));
                }

                if (step.Kind == "synthesize_plan.v1")
                {
                    Emit(narrator, "builder", "builder.synthesis.start", "Starting plan synthesis", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["retrievalHash"] = retrievalHash }));

                    if (retrievalResult is null || string.IsNullOrWhiteSpace(retrievalHash))
                    {
                        EmitFailure(narrator, "builder", "builder.synthesis.failed", "Synthesis requires retrieval result", runId, planHash, providerHash, envHash, refs, step.StepId);
                        return ExitCodes.PlanInvalid;
                    }

                    var synthRoot = Path.Combine(runDir, "plan_synthesis");
                    var planRoot = Path.Combine(runDir, "plan");
                    Directory.CreateDirectory(synthRoot);
                    Directory.CreateDirectory(planRoot);

                    var request = new PlanSynthesisRequest
                    {
                        PlanKind = GetArgString(step.Args, "planKind", "builder_v1"),
                        RetrievalHash = retrievalHash,
                        ProviderKind = providerKind,
                        EnvironmentKind = envId,
                        Constraints = new[] { "deterministic=true", "network=off" },
                        ProjectRoot = RelativePath(project, project)
                    };

                    var synthesis = SynthesizePlanV1(request, retrievalResult.Hits);
                    if (!ValidateSynthesizedPlan(synthesis.PlanJson, envDescriptorDoc.RootElement, out var synthErrorCode, out var synthSummary, out var synthDetails))
                    {
                        EmitFailure(narrator, "builder", "builder.synthesis.failed", synthSummary, runId, planHash, providerHash, envHash, refs, step.StepId, details: synthErrorCode + ";" + synthDetails);
                        return ExitCodes.PlanInvalid;
                    }

                    await File.WriteAllTextAsync(Path.Combine(synthRoot, "request.json"), JsonSerializer.Serialize(request.Normalize(), RepoSliceJson.Options)).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(synthRoot, "result.json"), JsonSerializer.Serialize(synthesis, RepoSliceJson.Options)).ConfigureAwait(false);
                    await File.WriteAllLinesAsync(Path.Combine(synthRoot, "evidence.ndjson"), synthesis.Evidence.OrderBy(x => x.StepId, StringComparer.Ordinal).ThenBy(x => x.HitId, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x, RepoSliceJson.Options))).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(planRoot, "plan.json"), synthesis.PlanJson).ConfigureAwait(false);
                    var evidenceText = await File.ReadAllTextAsync(Path.Combine(synthRoot, "evidence.ndjson")).ConfigureAwait(false);
                    var planHashes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["requestHash"] = synthesis.RequestHash,
                        ["retrievalHash"] = retrievalHash,
                        ["evidenceHash"] = synthesis.EvidenceHash,
                        ["planHash"] = synthesis.PlanHash
                    };
                    await File.WriteAllTextAsync(Path.Combine(planRoot, "hashes.json"), JsonSerializer.Serialize(planHashes, JsonOptions())).ConfigureAwait(false);

                    synthesisEvidenceHash = synthesis.EvidenceHash;
                    var selectedSteps = string.Join(',', synthesis.Evidence.Select(x => x.StepId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
                    var skippedSteps = string.Join(',', retrievalResult.Hits.Select(x => x.HitId).Except(synthesis.Evidence.Select(x => x.HitId), StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
                    Emit(narrator, "builder", "builder.decision.selected_steps", "Selected executable steps", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["steps"] = selectedSteps, ["reason"] = "top_ranked_hits" }));
                    Emit(narrator, "builder", "builder.decision.skipped_steps", "Skipped retrieval hits", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["steps"] = skippedSteps, ["reasonCode"] = "outside_top3" }));
                    Emit(narrator, "builder", "builder.synthesis.end", "Plan synthesis completed", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["synthPlanHash"] = synthesis.PlanHash, ["evidenceHash"] = synthesis.EvidenceHash, ["evidenceBytes"] = Encoding.UTF8.GetByteCount(evidenceText).ToString() }));
                }

                await WriteInputsFingerprintAsync(runDir, project, step, retrievalHash, synthesisEvidenceHash).ConfigureAwait(false);
                Emit(narrator, "execute", "execute.step.end", "Step completed", Merge(IdentityData(runId, planHash, providerHash, envHash, refs), new() { ["stepId"] = step.StepId, ["stepKind"] = step.Kind }));
                stepEvents.Add(new { step.StepId, kind = step.Kind, toolId = step.ToolId, status = "completed" });
                stepSummaries.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["durationBucket"] = "lt_1s",
                    ["errorCode"] = string.Empty,
                    ["expects"] = "status=completed",
                    ["inputs"] = "plan/provider/env",
                    ["kind"] = step.Kind,
                    ["outputs"] = string.Equals(step.Kind, "retrieve_context.v1", StringComparison.Ordinal) ? "retrieval" : string.Equals(step.Kind, "synthesize_plan.v1", StringComparison.Ordinal) ? "plan_synthesis,plan" : string.Equals(step.Kind, "write_text.v1", StringComparison.Ordinal) || string.Equals(step.Kind, "read_text.v1", StringComparison.Ordinal) || string.Equals(step.Kind, "assert_contains.v1", StringComparison.Ordinal) || string.Equals(step.Kind, "apply_unified_diff.v1", StringComparison.Ordinal) ? $"steps/{step.StepId}" : $"tool/{step.StepId}",
                    ["status"] = "completed",
                    ["stepId"] = step.StepId,
                    ["toolId"] = step.ToolId
                });
            }

            Emit(narrator, "execute", "execute.end", "Execution completed", IdentityData(runId, planHash, providerHash, envHash, refs));
            Emit(narrator, "builder", "builder.execute.end", "Builder execution completed", IdentityData(runId, planHash, providerHash, envHash, refs));

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

            var traceEvents = new object[]
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
            var outputManifest = new List<string> { "run.json", "hashes.json", "identity.json", "result.json", "trace/events.ndjson", "narration/events.ndjson", "provider/request.json", "provider/result.json" };
            if (!string.IsNullOrWhiteSpace(retrievalHash))
            {
                outputManifest.Add("retrieval/request.json");
                outputManifest.Add("retrieval/result.json");
                outputManifest.Add("retrieval/query_pack.json");
                outputManifest.Add("retrieval/hits.ndjson");
                outputManifest.Add("retrieval/context_pack.txt");
                outputManifest.Add("retrieval/hashes.json");
                outputManifest.Add("retrieval/scoring.ndjson");
                outputManifest.Add("retrieval/stats.json");
                outputManifest.Add("slice/decisions.ndjson");
                outputManifest.Add("plan_synthesis/request.json");
                outputManifest.Add("plan_synthesis/result.json");
                outputManifest.Add("plan_synthesis/evidence.ndjson");
                outputManifest.Add("plan/plan.json");
                outputManifest.Add("plan/hashes.json");
            }
            var outputManifestHash = ComputeHashHex(string.Join("\n", outputManifest));
            var retrievalScoringPath = Path.Combine(runDir, "retrieval", "scoring.ndjson");
            var sliceDecisionsPath = Path.Combine(runDir, "slice", "decisions.ndjson");
            var evidencePath = Path.Combine(runDir, "plan_synthesis", "evidence.ndjson");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runId"] = runId,
                ["planHash"] = planHash,
                ["providerHash"] = providerHash,
                ["envHash"] = envHash,
                ["traceHash"] = traceHash,
                ["retrievalHash"] = retrievalHash,
                ["outputManifestHash"] = outputManifestHash
            };
            if (File.Exists(retrievalScoringPath))
            {
                hashes["scoringHash"] = ComputeHashHex(await File.ReadAllTextAsync(retrievalScoringPath).ConfigureAwait(false));
            }
            if (File.Exists(sliceDecisionsPath))
            {
                hashes["sliceDecisionHash"] = ComputeHashHex(await File.ReadAllTextAsync(sliceDecisionsPath).ConfigureAwait(false));
            }
            if (File.Exists(evidencePath))
            {
                hashes["evidenceHash"] = ComputeHashHex(await File.ReadAllTextAsync(evidencePath).ConfigureAwait(false));
            }
            await File.WriteAllTextAsync(Path.Combine(runDir, "hashes.json"), JsonSerializer.Serialize(hashes, JsonOptions())).ConfigureAwait(false);

            var identity = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runId"] = runId,
                ["planHash"] = planHash,
                ["retrievalHash"] = retrievalHash,
                ["scoringHash"] = hashes.TryGetValue("scoringHash", out var sh) ? sh : string.Empty,
                ["evidenceHash"] = hashes.TryGetValue("evidenceHash", out var ehv) ? ehv : string.Empty,
                ["replayOfRunId"] = string.Empty
            };
            await File.WriteAllTextAsync(Path.Combine(runDir, "identity.json"), JsonSerializer.Serialize(identity, JsonOptions())).ConfigureAwait(false);

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
            EmitFailure(narrator, "builder", "builder.execute.failed", "Builder execution failed", runId, planHash, providerHash, envHash, refs, details: ex.Message);
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

        var replayRoot = Path.Combine(resolved, "replay");
        var replayRetrievalRoot = Path.Combine(replayRoot, "retrieval");
        Directory.CreateDirectory(replayRetrievalRoot);
        foreach (var file in new[] { "context_pack.txt", "hits.ndjson", "scoring.ndjson", "hashes.json" })
        {
            var src = Path.Combine(resolved, "retrieval", file);
            var dst = Path.Combine(replayRetrievalRoot, file);
            if (File.Exists(src))
            {
                await File.WriteAllTextAsync(dst, await File.ReadAllTextAsync(src).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        var runIdentityPath = Path.Combine(resolved, "identity.json");
        var replayIdentity = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["planHash"] = planHash,
            ["retrievalHash"] = hashesDoc.RootElement.TryGetProperty("retrievalHash", out var rh) ? rh.GetString() ?? string.Empty : string.Empty,
            ["scoringHash"] = hashesDoc.RootElement.TryGetProperty("scoringHash", out var rsh) ? rsh.GetString() ?? string.Empty : string.Empty,
            ["evidenceHash"] = hashesDoc.RootElement.TryGetProperty("evidenceHash", out var reh) ? reh.GetString() ?? string.Empty : string.Empty,
            ["replayOfRunId"] = runId
        };
        await File.WriteAllTextAsync(Path.Combine(replayRoot, "identity.json"), JsonSerializer.Serialize(replayIdentity, JsonOptions())).ConfigureAwait(false);
        if (!File.Exists(runIdentityPath))
        {
            await File.WriteAllTextAsync(runIdentityPath, JsonSerializer.Serialize(new Dictionary<string, string>(replayIdentity, StringComparer.Ordinal) { ["replayOfRunId"] = string.Empty }, JsonOptions())).ConfigureAwait(false);
        }

        Emit(narrator, "replay", "replay.inputs", "Loaded replay inputs", IdentityData(runId, planHash, providerHash, envHash, refs));

        var expectedRunId = ComputeHashHex($"{planHash}|{providerHash}|{envHash}|builder_smoke.v2")[..16];
        var expectedTraceHash = ComputeHashHex(await File.ReadAllTextAsync(tracePath).ConfigureAwait(false));

        var resultJsonPath = Path.Combine(resolved, "result.json");
        var expectedManifest = new List<string> { "run.json", "hashes.json", "result.json", "trace/events.ndjson", "narration/events.ndjson" };
        if (File.Exists(resultJsonPath))
        {
            var resultDoc = JsonDocument.Parse(await File.ReadAllTextAsync(resultJsonPath).ConfigureAwait(false));
            if (resultDoc.RootElement.TryGetProperty("outputs", out var outputsEl) && outputsEl.ValueKind == JsonValueKind.Array)
            {
                expectedManifest = outputsEl.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
        }

        var expectedManifestHash = ComputeHashHex(string.Join("\n", expectedManifest));

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

    static string BaseArtifactRefs() => "json:run.json,json:hashes.json,json:result.json,json:provider/request.json,json:provider/result.json,ndjson:trace/events.ndjson,ndjson:narration/events.ndjson,json:retrieval/result.json,json:retrieval/stats.json,ndjson:retrieval/scoring.ndjson,ndjson:slice/decisions.ndjson,txt:retrieval/context_pack.txt,json:plan_synthesis/result.json,ndjson:plan_synthesis/evidence.ndjson,json:plan/plan.json";

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


    static string GetArgString(Dictionary<string, object?> args, string key, string fallback)
    {
        if (args.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        return fallback;
    }

    static int GetArgInt(Dictionary<string, object?> args, string key, int fallback)
    {
        if (args.TryGetValue(key, out var value))
        {
            if (value is double d)
            {
                return Convert.ToInt32(d);
            }

            if (value is string s && int.TryParse(s, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    static string[] GetArgStringList(Dictionary<string, object?> args, string key, string[] fallback)
    {
        if (args.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s))
        {
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0)
            {
                return parts.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
        }

        return fallback.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    static async Task WriteHashesAsync(string hashesPath, string requestPath, string stdoutPath, string stderrPath, string resultPath, string exitPath)
    {
        var stepHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exit.json"] = ComputeHashHex(await File.ReadAllTextAsync(exitPath).ConfigureAwait(false)),
            ["request.json"] = ComputeHashHex(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false)),
            ["result.json"] = ComputeHashHex(await File.ReadAllTextAsync(resultPath).ConfigureAwait(false)),
            ["stderr.txt"] = ComputeHashHex(await File.ReadAllTextAsync(stderrPath).ConfigureAwait(false)),
            ["stdout.txt"] = ComputeHashHex(await File.ReadAllTextAsync(stdoutPath).ConfigureAwait(false))
        };

        await File.WriteAllTextAsync(hashesPath, JsonSerializer.Serialize(stepHashes, JsonOptions())).ConfigureAwait(false);
    }

    static string? ResolvePolicyPath(string root, string relativePath, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            errorCode = "file.policy.path.missing";
            return null;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            errorCode = "file.policy.path.escape";
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            errorCode = "file.policy.path.escape";
            return null;
        }

        return candidate;
    }

    static bool TryApplySimpleUnifiedDiff(string original, string diffText, out string patched, out string? errorCode)
    {
        patched = original;
        errorCode = null;

        if (string.IsNullOrWhiteSpace(diffText))
        {
            errorCode = "builder.patch.invalid";
            return false;
        }

        var removed = new List<string>();
        var added = new List<string>();
        foreach (var line in diffText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("-", StringComparison.Ordinal))
            {
                removed.Add(line[1..]);
            }
            else if (line.StartsWith("+", StringComparison.Ordinal))
            {
                added.Add(line[1..]);
            }
        }

        var oldBlock = string.Join("\n", removed);
        var newBlock = string.Join("\n", added);
        if (oldBlock.Length == 0)
        {
            errorCode = "builder.patch.invalid";
            return false;
        }

        if (!original.Contains(oldBlock, StringComparison.Ordinal))
        {
            errorCode = "builder.patch.reject";
            return false;
        }

        patched = original.Replace(oldBlock, newBlock, StringComparison.Ordinal);
        return true;
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


    static PlanSynthesisResult SynthesizePlanV1(PlanSynthesisRequest request, IReadOnlyList<RetrievalHit> hits)
    {
        var normalized = request.Normalize();
        var requestHash = normalized.ComputeRequestHash();
        var selected = hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.TokensMatched).ThenBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.FirstMatchOffset).Take(3).ToArray();

        var evidence = new List<PlanStepEvidence>();
        var steps = selected.Select((hit, idx) =>
        {
            var stepId = ComputeHashHex($"{requestHash}|{idx}|{hit.Path}")[..12];
            var stepEvidence = new PlanStepEvidence
            {
                StepId = stepId,
                HitId = hit.HitId,
                Path = hit.Path,
                SnippetHash = ComputeHashHex(hit.Excerpt),
                Range = "1-*"
            };
            evidence.Add(stepEvidence);

            return new Dictionary<string, object?>
            {
                ["stepId"] = stepId,
                ["kind"] = "RunTool",
                ["toolId"] = "linux.noop.v1",
                ["args"] = new Dictionary<string, object?> { ["targetPath"] = hit.Path, ["requiresNetwork"] = false },
                ["evidence"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["hitId"] = stepEvidence.HitId,
                        ["path"] = stepEvidence.Path,
                        ["snippetHash"] = stepEvidence.SnippetHash,
                        ["range"] = stepEvidence.Range
                    }
                },
                ["inputs"] = new[] { $"retrieval/hits/{idx}" },
                ["outputs"] = new[] { $"tool/{idx}/result.json" },
                ["expects"] = new[] { "exitCode==0" }
            };
        }).OrderBy(x => x["stepId"]?.ToString(), StringComparer.Ordinal).ToArray();

        var basePlanObj = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["planKind"] = normalized.PlanKind,
            ["inputs"] = new Dictionary<string, object?>
            {
                ["requestHash"] = requestHash,
                ["retrievalHash"] = normalized.RetrievalHash,
                ["constraints"] = normalized.Constraints,
                ["projectRoot"] = normalized.ProjectRoot
            },
            ["providerRef"] = normalized.ProviderKind,
            ["envRef"] = normalized.EnvironmentKind,
            ["steps"] = steps
        };

        var semanticJson = JsonSerializer.Serialize(basePlanObj, RepoSliceJson.Options);
        var synthesizedPlanHash = ComputeHashHex(semanticJson);
        var evidencePayload = JsonSerializer.Serialize(evidence.OrderBy(x => x.StepId, StringComparer.Ordinal).ThenBy(x => x.HitId, StringComparer.Ordinal).ToArray(), RepoSliceJson.Options);
        var evidenceHash = ComputeHashHex(evidencePayload);

        var planEnvelope = new Dictionary<string, object?>(basePlanObj)
        {
            ["evidenceHash"] = evidenceHash,
            ["planHash"] = synthesizedPlanHash
        };

        var planJson = JsonSerializer.Serialize(planEnvelope, RepoSliceJson.Options);

        return new PlanSynthesisResult
        {
            PlanJson = planJson,
            PlanHash = synthesizedPlanHash,
            RequestHash = requestHash,
            EvidenceHash = evidenceHash,
            Evidence = evidence.OrderBy(x => x.StepId, StringComparer.Ordinal).ThenBy(x => x.HitId, StringComparer.Ordinal).ToArray(),
            Stats = new PlanSynthesisStats
            {
                RetrievedHitCount = hits.Count,
                StepCount = steps.Length,
                ToolCount = steps.Select(x => x["toolId"]?.ToString() ?? string.Empty).Distinct(StringComparer.Ordinal).Count()
            }
        };
    }


    static bool ValidateSynthesizedPlan(string planJson, JsonElement envDescriptor, out string errorCode, out string summary, out string details)
    {
        errorCode = string.Empty;
        summary = string.Empty;
        details = string.Empty;

        var doc = JsonDocument.Parse(planJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
        {
            errorCode = "builder.plan.steps.missing";
            summary = "Synthesized plan is missing steps array.";
            details = "steps.missing";
            return false;
        }

        var outputs = new HashSet<string>(StringComparer.Ordinal);
        var caps = LoadEnvironmentCapabilities(envDescriptor, out _);
        if (caps.Count == 0)
        {
            caps.Add("process");
        }

        var idx = 0;
        foreach (var step in stepsEl.EnumerateArray())
        {
            var kind = step.TryGetProperty("kind", out var k) ? k.GetString() ?? string.Empty : string.Empty;
            if (!StepKinds.Registry.ContainsKey(kind))
            {
                errorCode = "builder.plan.step.kind.unknown";
                summary = "Synthesized plan contains unknown step kind.";
                details = $"index={idx};kind={kind}";
                return false;
            }

            if (!StepKinds.IsSupportedInEnvironment(kind, caps))
            {
                errorCode = "builder.plan.step.kind.unsupported";
                summary = "Synthesized plan step kind not supported in environment.";
                details = $"index={idx};kind={kind}";
                return false;
            }

            var toolId = step.TryGetProperty("toolId", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(toolId, "linux.noop.v1", StringComparison.Ordinal))
            {
                errorCode = "builder.plan.tool.missing";
                summary = "Synthesized plan references unknown tool.";
                details = $"index={idx};toolId={toolId}";
                return false;
            }

            if (step.TryGetProperty("outputs", out var outEl) && outEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var output in outEl.EnumerateArray().Select(x => x.GetString() ?? string.Empty))
                {
                    if (!outputs.Add(output))
                    {
                        errorCode = "builder.plan.output.duplicate";
                        summary = "Synthesized plan has duplicate outputs.";
                        details = $"index={idx};output={output}";
                        return false;
                    }
                }
            }

            idx++;
        }

        return true;
    }

    static async Task WriteInputsFingerprintAsync(string runDir, string projectRoot, (string StepId, string Kind, string ToolId, bool RequiresNetwork, Dictionary<string, object?> Args) step, string retrievalHash, string evidenceHash)
    {
        var stepRoot = Path.Combine(runDir, "steps", step.StepId);
        if (!Directory.Exists(stepRoot))
        {
            return;
        }

        var refs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (step.Args.TryGetValue("targetPath", out var targetObj) && targetObj is string targetPath && !string.IsNullOrWhiteSpace(targetPath))
        {
            var abs = Path.GetFullPath(Path.Combine(projectRoot, targetPath));
            if (File.Exists(abs))
            {
                refs[$"project/{targetPath.Replace('\\', '/')}"] = ComputeHashHex(await File.ReadAllTextAsync(abs).ConfigureAwait(false));
            }
        }

        if (step.Args.TryGetValue("queryFile", out var queryObj) && queryObj is string queryFile && !string.IsNullOrWhiteSpace(queryFile))
        {
            var abs = Path.GetFullPath(Path.Combine(projectRoot, queryFile));
            if (File.Exists(abs))
            {
                refs[$"project/{queryFile.Replace('\\', '/')}"] = ComputeHashHex(await File.ReadAllTextAsync(abs).ConfigureAwait(false));
            }
        }

        var contextPackPath = Path.Combine(runDir, "retrieval", "context_pack.txt");
        if (File.Exists(contextPackPath))
        {
            refs["retrieval/context_pack.txt"] = ComputeHashHex(await File.ReadAllTextAsync(contextPackPath).ConfigureAwait(false));
        }

        if (!string.IsNullOrWhiteSpace(retrievalHash))
        {
            refs["retrieval/hash"] = retrievalHash;
        }

        if (!string.IsNullOrWhiteSpace(evidenceHash))
        {
            refs["plan_synthesis/evidenceHash"] = evidenceHash;
        }

        var payload = new
        {
            stepId = step.StepId,
            stepKind = step.Kind,
            inputs = refs.Select(x => new { path = x.Key, hash = x.Value }).ToArray()
        };

        await File.WriteAllTextAsync(Path.Combine(stepRoot, "inputs_fingerprint.json"), JsonSerializer.Serialize(payload, JsonOptions())).ConfigureAwait(false);
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
            ["EmitArtifact"] = new[] { "filesystem" },
            ["retrieve_context.v1"] = new[] { "retrieval.lexical" },
            ["synthesize_plan.v1"] = new[] { "process" },
            ["write_text.v1"] = new[] { "filesystem" },
            ["read_text.v1"] = new[] { "filesystem" },
            ["apply_unified_diff.v1"] = new[] { "filesystem" },
            ["assert_contains.v1"] = new[] { "verify" }
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
}
