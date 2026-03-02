#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Contracts.Core.AI;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

using ContractsToolCatalogSnapshot = Shoots.Contracts.Core.ToolCatalogSnapshot;

namespace Shoots.Runtime.Loader;

public sealed class AiHelpFacade : IAiHelpFacade
{
    private static readonly ConcurrentQueue<AiHelpIntentUsage> IntentLog = new();

    private readonly IRuntimeFacade _runtimeFacade;
    private readonly IRuntimeNarratorSummary _narratorSummary;
    private readonly IReadOnlyList<IAiHelpSurface> _registeredSurfaces;
    private readonly IAiHelpIntentLogger? _intentLogger;

    public AiHelpFacade(
        IRuntimeFacade runtimeFacade,
        IRuntimeNarratorSummary narratorSummary,
        IEnumerable<IAiHelpSurface>? helpSurfaces = null,
        IAiHelpIntentLogger? intentLogger = null)
    {
        _runtimeFacade = runtimeFacade ?? throw new ArgumentNullException(nameof(runtimeFacade));
        _narratorSummary = narratorSummary ?? throw new ArgumentNullException(nameof(narratorSummary));
        _registeredSurfaces = helpSurfaces?.ToList() ?? new List<IAiHelpSurface>();
        _intentLogger = intentLogger;
    }

    public async Task<string> GetContextSummaryAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        var surfaces = ResolveSurfaces(request);
        if (surfaces.Count == 0)
            return DescribeMissingSurface(request);

        if (!SupportsIntent(surfaces, request.Intent))
            return "AI Help is offline because the intent is not registered for this surface.";

        LogIntentUsage(request, surfaces[0]);

        var status = await _runtimeFacade.QueryStatusAsync(ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("Explanatory assistance only.");
        builder.AppendLine($"Intent: {DescribeIntent(request.Intent)}.");
        builder.AppendLine($"Scope: {DescribeScope(request.Scope)}.");
        builder.AppendLine(_narratorSummary.DescribeRuntime(ToRuntimeVersion(status.Version)));
        builder.AppendLine(DescribeWorkspace(request.Workspace));
        builder.AppendLine(DescribePlan(request.Plan));
        builder.AppendLine(DescribeCatalog(request.ToolCatalog, request.Role));
        builder.AppendLine(DescribeRole(request.Role));
        builder.AppendLine(DescribeSurfaceContexts(surfaces));

        return builder.ToString().Trim();
    }

    public async Task<string> ExplainStateAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        var surfaces = ResolveSurfaces(request);
        if (surfaces.Count == 0)
            return DescribeMissingSurface(request);

        if (!SupportsIntent(surfaces, request.Intent))
            return "AI Help is offline because the intent is not registered for this surface.";

        LogIntentUsage(request, surfaces[0]);

        var status = await _runtimeFacade.QueryStatusAsync(ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("State summary:");
        builder.AppendLine($"Intent: {DescribeIntent(request.Intent)}.");
        builder.AppendLine($"Scope: {DescribeScope(request.Scope)}.");
        builder.AppendLine(_narratorSummary.DescribeRuntime(ToRuntimeVersion(status.Version)));

        if (!string.IsNullOrWhiteSpace(request.ExecutionState))
            builder.AppendLine($"Execution state: {request.ExecutionState}.");

        if (!string.IsNullOrWhiteSpace(request.EnvironmentProfile))
            builder.AppendLine($"Environment profile: {request.EnvironmentProfile}.");

        if (!string.IsNullOrWhiteSpace(request.LastAppliedProfile))
            builder.AppendLine($"Last applied: {request.LastAppliedProfile}.");

        builder.AppendLine($"Tool tier: {request.Workspace.Tier}.");
        builder.AppendLine($"Allowed capabilities: {DescribeStrings(request.Workspace.AllowedCapabilities)}.");
        builder.AppendLine(DescribeSurfaceConstraints(surfaces));

        return builder.ToString().Trim();
    }

    public Task<string> SuggestNextStepsAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        _ = ct;

        var surfaces = ResolveSurfaces(request);
        if (surfaces.Count == 0)
            return Task.FromResult(DescribeMissingSurface(request));

        if (!SupportsIntent(surfaces, request.Intent))
            return Task.FromResult("AI Help is offline because the intent is not registered for this surface.");

        LogIntentUsage(request, surfaces[0]);

        var steps = new List<string>
        {
            "Review the current workspace and environment summary.",
            "Confirm the execution plan before starting a run.",
            "Apply environment profiles or scripts only when needed."
        };

        if (string.IsNullOrWhiteSpace(request.Workspace.Name))
            steps.Insert(0, "Select a workspace to scope context.");

        steps.AddRange(DescribeSurfaceCapabilities(surfaces));

        return Task.FromResult($"{string.Join(" ", steps)} Intent: {DescribeIntent(request.Intent)}. Scope: {DescribeScope(request.Scope)}.");
    }

    private IReadOnlyList<IAiHelpSurface> ResolveSurfaces(AiHelpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Scope.SurfaceId))
            return Array.Empty<IAiHelpSurface>();

        var normalized = NormalizeSurfaceId(request.Scope.SurfaceId);

        return _registeredSurfaces
            .Where(surface => NormalizeSurfaceId(surface.SurfaceId) == normalized)
            .Distinct()
            .ToList();
    }

    private static string NormalizeSurfaceId(string value)
        => value.Trim().ToLowerInvariant();

    // ---- Intent handling (enum-first, drift-safe fallback) ----

    private static string DescribeIntent(AiIntentSnapshot intent)
    {
        var (type, scope, targetId) = ReadIntent(intent);

        if (string.IsNullOrWhiteSpace(targetId))
            return $"{type} for {scope}";

        return $"{type} for {scope} ({targetId})";
    }

    private static (AiIntentType Type, AiIntentScope Scope, string TargetId) ReadIntent(AiIntentSnapshot snapshot)
    {
        // IMPORTANT: Do NOT assume enums include "Unknown".
        // Use default(T) as the fallback sentinel.
        var typeFallback = default(AiIntentType);
        var scopeFallback = default(AiIntentScope);

        var type =
            TryReadEnumProp(snapshot, "Type", typeFallback)
            ?? TryParseEnumFromStringProp<AiIntentType>(snapshot, "Type")
            ?? TryParseEnumFromStringProp<AiIntentType>(snapshot, "IntentType")
            ?? TryParseEnumFromStringProp<AiIntentType>(snapshot, "Kind")
            ?? typeFallback;

        var scope =
            TryReadEnumProp(snapshot, "Scope", scopeFallback)
            ?? TryParseEnumFromStringProp<AiIntentScope>(snapshot, "Scope")
            ?? TryParseEnumFromStringProp<AiIntentScope>(snapshot, "IntentScope")
            ?? scopeFallback;

        var target =
            ReadStringProp(snapshot, "TargetId") ??
            ReadStringProp(snapshot, "Target") ??
            ReadStringProp(snapshot, "Id") ??
            string.Empty;

        return (type, scope, target.Trim());
    }

    private static AiIntentDescriptor ToIntentDescriptor(AiIntentSnapshot snapshot)
    {
        var (type, scope, targetId) = ReadIntent(snapshot);
        return new AiIntentDescriptor(type, scope, targetId);
    }

    private static bool SupportsIntent(IReadOnlyList<IAiHelpSurface> surfaces, AiIntentSnapshot intentSnapshot)
    {
        var (type, scope, _) = ReadIntent(intentSnapshot);

        var typeFallback = default(AiIntentType);
        var scopeFallback = default(AiIntentScope);

        return surfaces.Any(surface =>
        {
            foreach (var registered in surface.SupportedIntents)
            {
                if (registered is null) continue;

                var regType =
                    TryReadEnumProp(registered, "Type", typeFallback)
                    ?? TryParseEnumFromStringProp<AiIntentType>(registered, "Type")
                    ?? typeFallback;

                var regScope =
                    TryReadEnumProp(registered, "Scope", scopeFallback)
                    ?? TryParseEnumFromStringProp<AiIntentScope>(registered, "Scope")
                    ?? scopeFallback;

                if (regType.Equals(type) && regScope.Equals(scope))
                    return true;
            }

            return false;
        });
    }

    // ---- Scope text ----

    private static string DescribeScope(AiHelpScope scope)
    {
        if (!string.IsNullOrWhiteSpace(scope.Summary))
            return scope.Summary;

        if (scope.Data is null || scope.Data.Count == 0)
            return scope.SurfaceId;

        var detail = string.Join(", ", scope.Data.Select(pair => $"{pair.Key}={pair.Value}"));
        return $"{scope.SurfaceId} ({detail})";
    }

    private string DescribeMissingSurface(AiHelpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Scope.SurfaceId))
            return "AI Help requires a surface scope to operate.";

        return $"AI Help is offline because no surfaces match '{request.Scope.SurfaceId}'.";
    }

    private void LogIntentUsage(AiHelpRequest request, IAiHelpSurface surface)
    {
        var intentDesc = ToIntentDescriptor(request.Intent);

        var usage = new AiHelpIntentUsage(
            DateTimeOffset.UtcNow,
            surface.SurfaceId,
            intentDesc,
            request.Scope.Summary,
            request.Scope.Data ?? new Dictionary<string, string>());

        if (_intentLogger is null)
        {
            IntentLog.Enqueue(usage);
            return;
        }

        _intentLogger.Record(usage);
    }

    // ---- Surface descriptions ----

    private static string DescribeSurfaceContexts(IEnumerable<IAiHelpSurface> surfaces)
    {
        var summaries = surfaces
            .Select(surface => $"Surface {surface.SurfaceKind}: {surface.DescribeContext()}")
            .ToList();

        return summaries.Count == 0 ? "No surface context available." : string.Join(Environment.NewLine, summaries);
    }

    private static string DescribeSurfaceConstraints(IEnumerable<IAiHelpSurface> surfaces)
    {
        var summaries = surfaces
            .Select(surface => $"Surface {surface.SurfaceKind} constraints: {surface.DescribeConstraints()}")
            .ToList();

        return summaries.Count == 0 ? "No surface constraints available." : string.Join(Environment.NewLine, summaries);
    }

    private static IEnumerable<string> DescribeSurfaceCapabilities(IEnumerable<IAiHelpSurface> surfaces)
    {
        foreach (var surface in surfaces)
            yield return $"Review {surface.SurfaceKind} capabilities: {surface.DescribeCapabilities()}.";
    }

    // ---- Workspace/plan/catalog/role ----

    private static string DescribeWorkspace(AiWorkspaceSnapshot workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Name))
            return "Workspace: none selected.";

        if (string.IsNullOrWhiteSpace(workspace.RootPath))
            return $"Workspace: {workspace.Name}.";

        return $"Workspace: {workspace.Name} ({workspace.RootPath}).";
    }

    private static string DescribePlan(BuildPlan? plan)
    {
        if (plan is null)
            return "Plan: none loaded.";

        return $"Plan: {plan.PlanId} with {plan.Steps.Count} steps and {plan.Artifacts.Count} artifacts.";
    }

    private static string DescribeCatalog(ContractsToolCatalogSnapshot? catalog, AiRoleSnapshot? role)
    {
        if (catalog is null)
            return "Tool catalog: unavailable.";

        var (hash, toolIds, tagsByToolId) = TryExtractToolCatalog(catalog);
        var preferredTags = ReadPreferredTags(role);

        if (preferredTags.Count == 0)
            return $"Tool catalog: {toolIds.Count} tools (hash {hash}).";

        var preferred = toolIds
            .OrderByDescending(id => ScoreTool(tagsByToolId.TryGetValue(id, out var t) ? t : Array.Empty<string>(), preferredTags))
            .ThenBy(id => id, StringComparer.Ordinal)
            .Take(3)
            .ToList();

        if (preferred.Count == 0)
            return $"Tool catalog: {toolIds.Count} tools (hash {hash}).";

        return $"Tool catalog: {toolIds.Count} tools (hash {hash}). Preferred: {string.Join(", ", preferred)}.";
    }

    private static string DescribeRole(AiRoleSnapshot? role)
    {
        if (role is null)
            return "Role: none selected.";

        var name = (ReadStringProp(role, "Name") ?? "unknown-role").Trim();
        var preferred = ReadPreferredTags(role);

        if (preferred.Count == 0)
            return $"Role: {name}.";

        return $"Role: {name} (prefers {string.Join(", ", preferred)}).";
    }

    private static string DescribeStrings(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return "none";

        return string.Join(", ", values);
    }

    private static int ScoreTool(IReadOnlyList<string> tags, IReadOnlyList<string> preferredTags)
    {
        var score = 0;

        foreach (var tag in tags)
        {
            var normalized = (tag ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
                continue;

            if (preferredTags.Contains(normalized))
                score++;
        }

        return score;
    }

    // ---- Preferred tags: drift shims (reflection) ----

    private static IReadOnlyList<string> ReadPreferredTags(AiRoleSnapshot? role)
    {
        if (role is null)
            return Array.Empty<string>();

        var raw =
            ReadStringListProp(role, "PreferredCapabilities") ??
            ReadStringListProp(role, "PreferredTags") ??
            ReadStringListProp(role, "PreferredToolTags") ??
            ReadStringListProp(role, "Capabilities") ??
            new List<string>();

        return raw
            .Select(v => (v ?? string.Empty).Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // ---- Runtime version mapping (drift-safe) ----

    private static RuntimeVersion ToRuntimeVersion(RuntimeVersionInfo info)
    {
        var version = Activator.CreateInstance<RuntimeVersion>();

        TrySetIntProp(version, "Major", ReadIntProp(info, "Major"));
        TrySetIntProp(version, "Minor", ReadIntProp(info, "Minor"));
        TrySetIntProp(version, "Patch", ReadIntProp(info, "Patch"));

        var label = ReadStringProp(info, "Label") ?? ReadStringProp(info, "Suffix") ?? string.Empty;
        TrySetStringProp(version, "Label", label);

        return version;
    }

    // ---- Tool catalog extraction (reflection) ----

    private static (string Hash, IReadOnlyList<string> ToolIds, IReadOnlyDictionary<string, IReadOnlyList<string>> TagsByToolId)
        TryExtractToolCatalog(ContractsToolCatalogSnapshot catalog)
    {
        var hash =
            ReadStringProp(catalog, "Hash") ??
            ReadStringProp(catalog, "Id") ??
            ReadStringProp(catalog, "Digest") ??
            "unknown-hash";

        var toolsObj =
            (object?)GetProp(catalog, "Tools") ??
            (object?)GetProp(catalog, "Entries");

        var toolIds = new List<string>();
        var tagsById = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (toolsObj is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is null) continue;

                var specObj = (object?)GetProp(item, "Spec") ?? item;

                var toolId = TryGetToolId(specObj);
                if (string.IsNullOrWhiteSpace(toolId))
                    continue;

                toolIds.Add(toolId);

                var tags = TryGetTags(specObj);
                tagsById[toolId] = tags;
            }
        }

        return (hash, toolIds, tagsById);
    }

    private static string? TryGetToolId(object specObj)
    {
        var toolIdObj = GetProp(specObj, "ToolId");
        if (toolIdObj is null)
            return null;

        if (toolIdObj is string s)
            return s;

        var value = GetProp(toolIdObj, "Value");
        return value as string;
    }

    private static IReadOnlyList<string> TryGetTags(object specObj)
    {
        var tagsObj = GetProp(specObj, "Tags");
        if (tagsObj is IReadOnlyList<string> tagsList)
            return tagsList;

        if (tagsObj is IEnumerable<string> tagsEnum)
            return tagsEnum.ToList();

        if (tagsObj is System.Collections.IEnumerable e)
        {
            var list = new List<string>();
            foreach (var item in e)
            {
                if (item is string s && !string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
            return list;
        }

        return Array.Empty<string>();
    }

    // ---- Reflection helpers ----

    private static object? GetProp(object obj, string name)
        => obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);

    private static string? ReadStringProp(object obj, string name)
        => obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj) as string;

    private static int ReadIntProp(object obj, string name)
    {
        var v = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
        return v is int i ? i : 0;
    }

    private static List<string>? ReadStringListProp(object obj, string name)
    {
        var v = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
        if (v is null)
            return null;

        if (v is IReadOnlyList<string> ro)
            return ro.ToList();

        if (v is IEnumerable<string> e)
            return e.ToList();

        if (v is System.Collections.IEnumerable any)
        {
            var list = new List<string>();
            foreach (var item in any)
            {
                if (item is string s)
                    list.Add(s);
            }
            return list;
        }

        return null;
    }

    private static void TrySetIntProp(object obj, string name, int value)
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite)
            return;

        if (p.PropertyType == typeof(int))
            p.SetValue(obj, value);
    }

    private static void TrySetStringProp(object obj, string name, string value)
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite)
            return;

        if (p.PropertyType == typeof(string))
            p.SetValue(obj, value);
    }

    private static TEnum? TryReadEnumProp<TEnum>(object obj, string name, TEnum _)
        where TEnum : struct, Enum
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return null;

        var v = p.GetValue(obj);
        return v is TEnum e ? e : null;
    }

    private static TEnum? TryParseEnumFromStringProp<TEnum>(object obj, string name)
        where TEnum : struct, Enum
    {
        var s = ReadStringProp(obj, name);
        if (string.IsNullOrWhiteSpace(s)) return null;

        return Enum.TryParse<TEnum>(s.Trim(), ignoreCase: true, out var t) ? t : null;
    }
}