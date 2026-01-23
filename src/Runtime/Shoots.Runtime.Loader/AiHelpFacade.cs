#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;
using RuntimeSnapshot = Shoots.Runtime.Abstractions.ToolCatalogSnapshot;

namespace Shoots.Runtime.Loader;

public sealed class AiHelpFacade : IAiHelpFacade
{
    private readonly IRuntimeFacade _runtimeFacade;
    private readonly IRuntimeNarratorSummary _narratorSummary;
    private readonly IReadOnlyList<IAiHelpSurface> _registeredSurfaces;

    public AiHelpFacade(
        IRuntimeFacade runtimeFacade,
        IRuntimeNarratorSummary narratorSummary,
        IEnumerable<IAiHelpSurface>? helpSurfaces = null)
    {
        _runtimeFacade = runtimeFacade ?? throw new ArgumentNullException(nameof(runtimeFacade));
        _narratorSummary = narratorSummary ?? throw new ArgumentNullException(nameof(narratorSummary));
        _registeredSurfaces = helpSurfaces?.ToList() ?? Array.Empty<IAiHelpSurface>();
    }

    public async Task<string> GetContextSummaryAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        var surfaces = ResolveSurfaces(request);
        if (surfaces.Count == 0)
            return "AI Help is offline because no help surfaces are registered.";

        var status = await _runtimeFacade.QueryStatus(ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("Explanatory assistance only.");
        builder.AppendLine($"Intent: {DescribeIntent(request.Intent)}.");
        builder.AppendLine(_narratorSummary.DescribeRuntime(status.Version));
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
            return "AI Help is offline because no help surfaces are registered.";

        var status = await _runtimeFacade.QueryStatus(ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("State summary:");
        builder.AppendLine($"Intent: {DescribeIntent(request.Intent)}.");
        builder.AppendLine(_narratorSummary.DescribeRuntime(status.Version));

        if (!string.IsNullOrWhiteSpace(request.ExecutionState))
            builder.AppendLine($"Execution state: {request.ExecutionState}.");

        if (!string.IsNullOrWhiteSpace(request.EnvironmentProfile))
            builder.AppendLine($"Environment profile: {request.EnvironmentProfile}.");

        if (!string.IsNullOrWhiteSpace(request.LastAppliedProfile))
            builder.AppendLine($"Last applied: {request.LastAppliedProfile}.");

        builder.AppendLine($"Tool tier: {request.Workspace.Tier}.");
        builder.AppendLine($"Allowed capabilities: {DescribeCapabilities(request.Workspace.AllowedCapabilities)}.");
        builder.AppendLine(DescribeSurfaceConstraints(surfaces));

        return builder.ToString().Trim();
    }

    public Task<string> SuggestNextStepsAsync(AiHelpRequest request, CancellationToken ct = default)
    {
        _ = ct;

        var surfaces = ResolveSurfaces(request);
        if (surfaces.Count == 0)
            return Task.FromResult("AI Help is offline because no help surfaces are registered.");

        var steps = new List<string>
        {
            "Review the current workspace and environment summary.",
            "Confirm the execution plan before starting a run.",
            "Apply environment profiles or scripts only when needed."
        };

        if (string.IsNullOrWhiteSpace(request.Workspace.Name))
            steps.Insert(0, "Select a workspace to scope context.");

        steps.AddRange(DescribeSurfaceCapabilities(surfaces));

        return Task.FromResult($"{string.Join(" ", steps)} Intent: {DescribeIntent(request.Intent)}.");
    }

    private IReadOnlyList<IAiHelpSurface> ResolveSurfaces(AiHelpRequest request)
    {
        var surfaces = new List<IAiHelpSurface>();
        surfaces.AddRange(_registeredSurfaces);

        if (request.Surfaces is not null)
            surfaces.AddRange(request.Surfaces);

        return surfaces
            .Distinct()
            .ToList();
    }

    private static string DescribeIntent(AiIntentDescriptor intent)
    {
        if (string.IsNullOrWhiteSpace(intent.TargetId))
            return $"{intent.Type} for {intent.Scope}";

        return $"{intent.Type} for {intent.Scope} ({intent.TargetId})";
    }

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

    private static string DescribeCatalog(RuntimeSnapshot? catalog, RoleDescriptor? role)
    {
        if (catalog is null)
            return "Tool catalog: unavailable.";

        if (role is null || role.PreferredCapabilities.Count == 0)
            return $"Tool catalog: {catalog.Entries.Count} tools (hash {catalog.Hash}).";

        var preferred = GetPreferredTools(catalog, role)
            .Take(3)
            .ToList();

        if (preferred.Count == 0)
            return $"Tool catalog: {catalog.Entries.Count} tools (hash {catalog.Hash}).";

        return $"Tool catalog: {catalog.Entries.Count} tools (hash {catalog.Hash}). Preferred: {string.Join(", ", preferred)}.";
    }

    private static string DescribeRole(RoleDescriptor? role)
    {
        if (role is null)
            return "Role: none selected.";

        if (role.PreferredCapabilities.Count == 0)
            return $"Role: {role.Name}.";

        var capabilities = DescribeCapabilities(role.PreferredCapabilities);
        return $"Role: {role.Name} (prefers {capabilities}).";
    }

    private static string DescribeCapabilities(IReadOnlyList<ToolpackCapability> capabilities)
    {
        if (capabilities.Count == 0)
            return "none";

        return string.Join(", ", capabilities);
    }

    private static IEnumerable<string> GetPreferredTools(RuntimeSnapshot catalog, RoleDescriptor role)
    {
        var preferredTags = role.PreferredCapabilities
            .Select(capability => capability.ToString())
            .Select(name => name.ToLowerInvariant())
            .ToList();

        return catalog.Entries
            .Select(entry => entry.Spec)
            .OrderByDescending(spec => ScoreTool(spec.Tags, preferredTags))
            .ThenBy(spec => spec.ToolId.Value, StringComparer.Ordinal)
            .Select(spec => spec.ToolId.Value);
    }

    private static int ScoreTool(IReadOnlyList<string> tags, IReadOnlyList<string> preferredTags)
    {
        var score = 0;

        foreach (var tag in tags)
        {
            var normalized = tag.ToLowerInvariant();
            if (preferredTags.Contains(normalized))
                score++;
        }

        return score;
    }
}
