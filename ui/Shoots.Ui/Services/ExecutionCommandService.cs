#nullable enable

using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public sealed class ExecutionCommandService : IExecutionCommandService
{
    private readonly IRuntimeFacade _runtimeFacade;

    public ExecutionCommandService(IRuntimeFacade runtimeFacade)
    {
        _runtimeFacade = runtimeFacade ?? throw new ArgumentNullException(nameof(runtimeFacade));
    }

    public async Task<ExecutionStartResult> StartAsync(
        BuildPlan plan,
        HostRunOptions? options = null,
        CancellationToken ct = default)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        // Runtime returns RuntimeExecutionResult (not UI ExecutionStartResult).
        var runtime = await _runtimeFacade.StartExecutionAsync(plan, options, ct).ConfigureAwait(false);
        return MapStart(runtime, plan);
    }

    public Task CancelAsync(CancellationToken ct = default)
        => _runtimeFacade.CancelExecutionAsync(ct);

    public async Task<ExecutionStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default)
    {
        // Runtime returns RuntimeStatusSnapshot (not UI ExecutionStatusSnapshot).
        var status = await _runtimeFacade.QueryStatusAsync(ct).ConfigureAwait(false);
        return MapStatus(status);
    }

    // ------------------------------------------------------------------
    // Mapping: runtime -> UI
    // ------------------------------------------------------------------

    private static ExecutionStartResult MapStart(object runtimeResult, BuildPlan plan)
    {
        // Create UI ExecutionStartResult in a drift-tolerant way.
        var ui = TryCreate<ExecutionStartResult>() ?? CreateUninitialized<ExecutionStartResult>();

        // Outcome: try to read runtime outcome-ish fields.
        // If we can parse to Shoots.Contracts.Core.ExecutionOutcome, do it.
        var outcomeObj =
            TryGet(runtimeResult, "Outcome") ??
            TryGet(runtimeResult, "Status") ??
            TryGet(runtimeResult, "Result") ??
            TryGet(runtimeResult, "ExecutionOutcome");

        var outcome = ParseExecutionOutcome(outcomeObj) ?? ExecutionOutcome.Started;

        // IDs/hashes/messages with fallback to plan
        var workOrderId =
            TryGetString(runtimeResult, "WorkOrderId") ??
            TryGetString(runtimeResult, "WorkOrder") ??
            plan.Request.WorkOrder.Id.Value;

        var planId =
            TryGetString(runtimeResult, "PlanId") ??
            TryGetString(runtimeResult, "Id") ??
            TryGetString(runtimeResult, "ExecutionPlanId") ??
            "unknown-plan";

        var planHash =
            TryGetString(runtimeResult, "PlanHash") ??
            TryGetString(runtimeResult, "GraphHash") ??
            TryGetString(runtimeResult, "Hash") ??
            "unknown-hash";

        var message =
            TryGetString(runtimeResult, "Message") ??
            TryGetString(runtimeResult, "Detail") ??
            string.Empty;

        // ErrorCode is optional and drifts.
        var errorCode =
            TryGetString(runtimeResult, "ErrorCode") ??
            TryGetString(runtimeResult, "Code");

        // Try ctor shapes first (positional), then set props.
        var ctorMade =
            TryCreate<ExecutionStartResult>(outcome, workOrderId, planId, planHash, message, errorCode) ??
            TryCreate<ExecutionStartResult>(outcome, workOrderId, planId, planHash, message) ??
            TryCreate<ExecutionStartResult>(outcome, workOrderId, planId, planHash) ??
            TryCreate<ExecutionStartResult>(outcome, message) ??
            null;

        if (ctorMade is not null)
            ui = ctorMade;

        // Best-effort property hydration.
        TrySet(ui, "Outcome", outcome);
        TrySet(ui, "WorkOrderId", workOrderId);
        TrySet(ui, "PlanId", planId);
        TrySet(ui, "PlanHash", planHash);
        TrySet(ui, "Message", message);
        TrySet(ui, "ErrorCode", errorCode);

        return ui;
    }

    private static ExecutionStatusSnapshot MapStatus(object runtimeStatus)
    {
        // Create UI snapshot (even if the type loses a parameterless ctor).
        var snapshot = TryCreate<ExecutionStatusSnapshot>() ?? CreateUninitialized<ExecutionStatusSnapshot>();

        // Provider id/kind
        TrySet(snapshot, "ProviderId",
            TryGetString(runtimeStatus, "ProviderId") ??
            TryGetString(runtimeStatus, "Provider") ??
            string.Empty);

        TrySet(snapshot, "ProviderKind",
            TryGet(runtimeStatus, "ProviderKind") ??
            TryGet(runtimeStatus, "Kind"));

        // Plan id/hashes
        TrySet(snapshot, "PlanId",
            TryGetString(runtimeStatus, "PlanId") ??
            TryGetString(runtimeStatus, "ActivePlanId") ??
            TryGetString(runtimeStatus, "ExecutionPlanId") ??
            string.Empty);

        TrySet(snapshot, "GraphHash",
            TryGetString(runtimeStatus, "GraphHash") ??
            TryGetString(runtimeStatus, "ExecutionGraphHash") ??
            TryGetString(runtimeStatus, "PlanHash") ??
            string.Empty);

        TrySet(snapshot, "NodeSetHash",
            TryGetString(runtimeStatus, "NodeSetHash") ??
            TryGetString(runtimeStatus, "NodesHash") ??
            TryGetString(runtimeStatus, "NodeHash") ??
            string.Empty);

        TrySet(snapshot, "EdgeSetHash",
            TryGetString(runtimeStatus, "EdgeSetHash") ??
            TryGetString(runtimeStatus, "EdgesHash") ??
            TryGetString(runtimeStatus, "EdgeHash") ??
            string.Empty);

        // State/message/version
        TrySet(snapshot, "State",
            TryGet(runtimeStatus, "State") ??
            TryGet(runtimeStatus, "ExecutionState") ??
            TryGet(runtimeStatus, "Status"));

        TrySet(snapshot, "Message",
            TryGetString(runtimeStatus, "Message") ??
            TryGetString(runtimeStatus, "Detail") ??
            string.Empty);

        TrySet(snapshot, "Version",
            TryGet(runtimeStatus, "Version"));

        return snapshot;
    }

    private static ExecutionOutcome? ParseExecutionOutcome(object? raw)
    {
        if (raw is null) return null;

        if (raw is ExecutionOutcome eo) return eo;

        // Runtime may use a different enum type: read as string.
        var s = raw.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;

        return Enum.TryParse<ExecutionOutcome>(s, ignoreCase: true, out var parsed) ? parsed : null;
    }

    // ------------------------------------------------------------------
    // Reflection helpers (drift-tolerant)
    // ------------------------------------------------------------------

    private static object? TryGet(object obj, string name)
        => obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);

    private static string? TryGetString(object obj, string name)
        => TryGet(obj, name) as string;

    private static void TrySet(object target, string propName, object? value)
    {
        if (value is null) return;

        var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite) return;

        var destType = prop.PropertyType;

        // Direct assign if compatible.
        if (destType.IsInstanceOfType(value) || destType.IsAssignableFrom(value.GetType()))
        {
            prop.SetValue(target, value);
            return;
        }

        // Enum conversion (string/foreign enum -> our enum).
        if (destType.IsEnum)
        {
            var s = value.ToString();
            if (!string.IsNullOrWhiteSpace(s) &&
                Enum.TryParse(destType, s, ignoreCase: true, out var parsed) &&
                parsed is not null)
            {
                prop.SetValue(target, parsed);
                return;
            }
        }

        // Best-effort convert primitives.
        try
        {
            var converted = Convert.ChangeType(value, destType);
            prop.SetValue(target, converted);
        }
        catch
        {
            // ignore: UI status is best-effort
        }
    }

    private static T? TryCreate<T>(params object?[] args) where T : class
    {
        var t = typeof(T);

        // Prefer exact-match public ctors.
        foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = ctor.GetParameters();
            if (ps.Length != args.Length) continue;
            if (!CanBindArgs(ps, args)) continue;

            try { return (T?)ctor.Invoke(args); }
            catch { /* keep hunting */ }
        }

        // Fallback to Activator.
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

            var at = av.GetType();
            if (pt.IsInstanceOfType(av) || pt.IsAssignableFrom(at))
                continue;

            return false;
        }
        return true;
    }

    private static T CreateUninitialized<T>() where T : class
    {
        // Last resort: keep UI compiling even if ctor changes.
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
    }
}