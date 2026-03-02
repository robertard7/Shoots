#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;

namespace Shoots.UI.Services;

/// <summary>
/// UI-side adapter that maps UI execution DTOs to host-facing results.
/// IMPORTANT:
/// - Must NOT take dependencies on Shoots.Runtime.* at the assembly level.
/// - Host abstractions are allowed (Shoots.Host.Abstractions).
/// </summary>
public sealed class HostExecutionService : IHostExecutionService
{
    private readonly IExecutionCommandService _execution;

    public HostExecutionService(IExecutionCommandService execution)
    {
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
    }

    public WorkOrder CreateWorkOrder(
        string originalRequest,
        string intent,
        IReadOnlyList<string> constraints,
        IReadOnlyList<string> requestedArtifacts)
    {
        var id = new WorkOrderId($"wo-{Guid.NewGuid():N}");

        return new WorkOrder(
            id,
            originalRequest ?? string.Empty,
            intent ?? string.Empty,
            constraints ?? Array.Empty<string>(),
            requestedArtifacts ?? Array.Empty<string>());
    }

    public BuildPlan PreviewPlan(
        BuildRequest request,
        DelegationAuthority authority,
        IReadOnlyList<BuildStep> steps,
        IReadOnlyList<BuildArtifact> artifacts)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (authority is null) throw new ArgumentNullException(nameof(authority));
        if (steps is null) throw new ArgumentNullException(nameof(steps));
        if (artifacts is null) throw new ArgumentNullException(nameof(artifacts));

        // UI preview only. Deterministic-ish id for debugging, not a runtime contract.
        var planId = HashTools.ComputeSha256Hash(
            $"{request.CommandId}|{request.WorkOrder.Id.Value}|{steps.Count}|{artifacts.Count}"
        );

        // We cannot trust ctor param names (drift), so use positional + fallback.
        // Prefer: (planId, request, graphHash, nodesHash, edgesHash, authority, steps, artifacts)
        var graphHash = "preview-graph";
        var nodesHash = "preview-nodes";
        var edgesHash = "preview-edges";

        var created =
            TryCreate<BuildPlan>(planId, request, graphHash, nodesHash, edgesHash, authority, steps, artifacts) ??
            // Some versions drop one of the hashes or reorder them.
            TryCreate<BuildPlan>(planId, request, graphHash, authority, steps, artifacts) ??
            TryCreate<BuildPlan>(planId, request, authority, steps, artifacts) ??
            TryCreate<BuildPlan>(request, authority, steps, artifacts) ??
            TryCreate<BuildPlan>(planId, request, steps, artifacts) ??
            TryCreate<BuildPlan>(planId, request) ??
            TryCreate<BuildPlan>();

        if (created is null)
            throw new InvalidOperationException("Unable to construct BuildPlan (constructor drift exceeded).");

        // Best-effort property hydration (harmless if props don't exist).
        TrySet(created, "PlanId", planId);
        TrySet(created, "Id", planId);
        TrySet(created, "GraphHash", graphHash);
        TrySet(created, "NodesHash", nodesHash);
        TrySet(created, "NodeSetHash", nodesHash);
        TrySet(created, "EdgesHash", edgesHash);
        TrySet(created, "EdgeSetHash", edgesHash);
        TrySet(created, "Request", request);
        TrySet(created, "Authority", authority);
        TrySet(created, "Steps", steps);
        TrySet(created, "Artifacts", artifacts);

        return created;
    }

    public async Task<HostExecutionResult> RunAsync(
        BuildPlan plan,
        HostRunOptions? options = null,
        CancellationToken ct = default)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var start = await _execution.StartAsync(plan, options, ct).ConfigureAwait(false);
        return Map(start, plan);
    }

    // ------------------------------------------------------------------
    // ResumeAsync: interface signature in THIS repo resolves to runtime-ui
    // abstraction types (not the host types). Implement explicitly.
    // ------------------------------------------------------------------
    async Task<HostExecutionResult> IHostExecutionService.ResumeAsync(
        BuildPlan plan,
        Shoots.Runtime.Ui.Abstractions.DecisionInjectionRequest request,
        Shoots.Runtime.Ui.Abstractions.HostResumeIntent intent,
        CancellationToken ct)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        _ = request;
        _ = intent;

        // UI-only placeholder: treat "resume" as "start again".
        var start = await _execution.StartAsync(plan, options: null, ct).ConfigureAwait(false);
        return Map(start, plan);
    }

    // Optional convenience overload (host abstractions). Keep or delete.
    public async Task<HostExecutionResult> ResumeAsync(
        BuildPlan plan,
        Shoots.Host.Abstractions.DecisionInjectionRequest request,
        Shoots.Host.Abstractions.HostResumeIntent intent,
        CancellationToken ct = default)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        _ = request;
        _ = intent;

        var start = await _execution.StartAsync(plan, options: null, ct).ConfigureAwait(false);
        return Map(start, plan);
    }

    public ToolCatalogSnapshot GetToolCatalogSnapshot(BuildPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        // UI preview only. Real catalogs should come from host/runtime later.
        var toolSteps = plan.Steps.OfType<ToolBuildStep>().ToList();

        var tools = toolSteps.Select(step => CreateToolSpecPreview(step.ToolId)).ToList();

        var hash = HashTools.ComputeSha256Hash(
            string.Join("|", tools.Select(t => t.ToolId.Value), StringComparer.Ordinal)
        );

        return new ToolCatalogSnapshot(hash, tools);
    }

    private static ToolSpec CreateToolSpecPreview(ToolId toolId)
    {
        // ToolSpec also drifts. Avoid named args. Try a few ctor shapes.
        var authority = new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None);
        var emptyInputs = Array.Empty<ToolInputSpec>();
        var emptyOutputs = Array.Empty<ToolOutputSpec>();
        var emptyTags = Array.Empty<string>();

        // Common shapes we’ve seen:
        // (ToolId, string description, ToolAuthorityScope, IReadOnlyList<ToolInputSpec>, IReadOnlyList<ToolOutputSpec>, IReadOnlyList<string>)
        var spec =
            TryCreate<ToolSpec>(toolId, "UI preview", authority, emptyInputs, emptyOutputs, emptyTags) ??
            // (ToolId, ToolAuthorityScope, IReadOnlyList<ToolInputSpec>, IReadOnlyList<ToolOutputSpec>, IReadOnlyList<string>)
            TryCreate<ToolSpec>(toolId, authority, emptyInputs, emptyOutputs, emptyTags) ??
            // (ToolId, ToolAuthorityScope)
            TryCreate<ToolSpec>(toolId, authority) ??
            // (ToolId)
            TryCreate<ToolSpec>(toolId) ??
            TryCreate<ToolSpec>();

        if (spec is null)
            throw new InvalidOperationException("Unable to construct ToolSpec (constructor drift exceeded).");

        // Best-effort hydration
        TrySet(spec, "ToolId", toolId);
        TrySet(spec, "Id", toolId);
        TrySet(spec, "Description", "UI preview");
        TrySet(spec, "Authority", authority);
        TrySet(spec, "Inputs", emptyInputs);
        TrySet(spec, "Outputs", emptyOutputs);
        TrySet(spec, "Tags", emptyTags);

        return spec;
    }

    private static HostExecutionResult Map(ExecutionStartResult start, BuildPlan plan)
    {
        var outcome = start.Outcome switch
        {
            ExecutionOutcome.Started => HostExecutionOutcome.Started,
            ExecutionOutcome.Completed => HostExecutionOutcome.Completed,
            ExecutionOutcome.Cancelled => HostExecutionOutcome.Cancelled,
            ExecutionOutcome.Waiting => HostExecutionOutcome.Waiting,
            ExecutionOutcome.Failed => HostExecutionOutcome.Failed,
            _ => HostExecutionOutcome.Unknown
        };

        var workOrderId = string.IsNullOrWhiteSpace(start.WorkOrderId)
            ? plan.Request.WorkOrder.Id.Value
            : start.WorkOrderId!;

        var planId = string.IsNullOrWhiteSpace(start.PlanId)
            ? GetPlanIdFallback(plan)
            : start.PlanId!;

        var planHash = string.IsNullOrWhiteSpace(start.PlanHash)
            ? GetPlanHashFallback(plan)
            : start.PlanHash!;

        // ErrorCode drifts. Read if present, else null.
        var errorCode = GetOptionalString(start, "ErrorCode") ?? GetOptionalString(start, "Code");

        return new HostExecutionResult(
            outcome,
            WorkOrderId: workOrderId,
            PlanId: planId,
            PlanHash: planHash,
            Message: start.Message,
            ErrorCode: errorCode
        );
    }

    private static string GetPlanIdFallback(BuildPlan plan)
    {
        var t = plan.GetType();

        var planIdProp = t.GetProperty("PlanId");
        if (planIdProp?.GetValue(plan) is string s1 && !string.IsNullOrWhiteSpace(s1))
            return s1;

        var idProp = t.GetProperty("Id");
        var idVal = idProp?.GetValue(plan);
        if (idVal is string s2 && !string.IsNullOrWhiteSpace(s2))
            return s2;

        if (idVal is not null)
        {
            var valueProp = idVal.GetType().GetProperty("Value");
            if (valueProp?.GetValue(idVal) is string s3 && !string.IsNullOrWhiteSpace(s3))
                return s3;
        }

        return "unknown-plan";
    }

    private static string GetPlanHashFallback(BuildPlan plan)
    {
        var t = plan.GetType();

        var p = t.GetProperty("GraphHash");
        if (p?.GetValue(plan) is string s1 && !string.IsNullOrWhiteSpace(s1))
            return s1;

        p = t.GetProperty("PlanHash");
        if (p?.GetValue(plan) is string s2 && !string.IsNullOrWhiteSpace(s2))
            return s2;

        p = t.GetProperty("Hash");
        if (p?.GetValue(plan) is string s3 && !string.IsNullOrWhiteSpace(s3))
            return s3;

        return "unknown-hash";
    }

    // ------------------------------------------------------------------
    // Drift-tolerant helpers
    // ------------------------------------------------------------------

    private static T? TryCreate<T>(params object?[] args) where T : class
    {
        var t = typeof(T);

        // Prefer exact match ctors first.
        foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = ctor.GetParameters();
            if (ps.Length != args.Length) continue;

            if (!CanBindArgs(ps, args))
                continue;

            try
            {
                return (T?)ctor.Invoke(args);
            }
            catch
            {
                // keep hunting
            }
        }

        // If a parameterless ctor exists and args were empty, try it.
        if (args.Length == 0)
        {
            try { return Activator.CreateInstance(t) as T; }
            catch { return null; }
        }

        // Last resort: Activator may find something we missed.
        try { return Activator.CreateInstance(t, args) as T; }
        catch { return null; }
    }

    private static bool CanBindArgs(ParameterInfo[] ps, object?[] args)
    {
        for (var i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            var av = args[i];

            if (av is null)
            {
                if (pt.IsValueType && Nullable.GetUnderlyingType(pt) is null)
                    return false;
                continue;
            }

            if (!pt.IsInstanceOfType(av))
            {
                // Allow IReadOnlyList<T> -> IEnumerable<T> etc.
                if (pt.IsAssignableFrom(av.GetType()))
                    continue;

                return false;
            }
        }
        return true;
    }

    private static void TrySet(object target, string propName, object? value)
    {
        var t = target.GetType();
        var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite) return;

        try
        {
            if (value is null)
            {
                p.SetValue(target, null);
                return;
            }

            if (p.PropertyType.IsInstanceOfType(value) || p.PropertyType.IsAssignableFrom(value.GetType()))
            {
                p.SetValue(target, value);
                return;
            }
        }
        catch
        {
            // ignored by design
        }
    }

    private static string? GetOptionalString(object target, string propName)
    {
        var t = target.GetType();
        var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return null;

        try
        {
            var v = p.GetValue(target);
            return v as string;
        }
        catch
        {
            return null;
        }
    }
}