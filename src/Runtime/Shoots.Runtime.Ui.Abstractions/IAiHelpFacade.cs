using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IAiHelpFacade
{
    // AI Help is descriptive only and never triggers execution.
    Task<string> GetContextSummaryAsync(AiHelpRequest request, CancellationToken ct = default);

    Task<string> ExplainStateAsync(AiHelpRequest request, CancellationToken ct = default);

    Task<string> SuggestNextStepsAsync(AiHelpRequest request, CancellationToken ct = default);
}

/// <summary>
/// Identifies which UI surface asked for help and provides small, safe context.
/// </summary>
public sealed record AiHelpScope
{
    public string SurfaceId { get; }
    public string? Summary { get; }
    public IReadOnlyDictionary<string, string> Data { get; }

    public AiHelpScope(
        string surfaceId,
        string? summary,
        IReadOnlyDictionary<string, string> data)
    {
        SurfaceId = surfaceId ?? throw new ArgumentNullException(nameof(surfaceId));
        Summary = summary;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public AiHelpScope(string surfaceId, string? summary)
        : this(surfaceId, summary, new Dictionary<string, string>())
    {
    }
}

/// <summary>
/// UI-safe request contract for AI Help.
/// Does not depend on runtime or host internal assemblies.
/// </summary>
public sealed record AiHelpRequest
{
    public AiHelpScope Scope { get; }
    public AiIntentSnapshot Intent { get; }
    public AiWorkspaceSnapshot Workspace { get; }
    public BuildPlan? Plan { get; }

    // Contract snapshot (UI does not depend on runtime internals)
    public ToolCatalogSnapshot? ToolCatalog { get; }

    public string? ExecutionState { get; }
    public string? EnvironmentProfile { get; }
    public string? LastAppliedProfile { get; }

    public AiRoleSnapshot? Role { get; }

    public IReadOnlyList<AiHelpSurfaceSnapshot> Surfaces { get; }

    public AiHelpRequest(
        AiHelpScope scope,
        AiIntentSnapshot intent,
        AiWorkspaceSnapshot workspace,
        BuildPlan? plan,
        ToolCatalogSnapshot? toolCatalog,
        string? executionState,
        string? environmentProfile,
        string? lastAppliedProfile,
        AiRoleSnapshot? role,
        IReadOnlyList<AiHelpSurfaceSnapshot>? surfaces)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Plan = plan;
        ToolCatalog = toolCatalog;
        ExecutionState = executionState;
        EnvironmentProfile = environmentProfile;
        LastAppliedProfile = lastAppliedProfile;
        Role = role;
        Surfaces = surfaces ?? Array.Empty<AiHelpSurfaceSnapshot>();
    }
}

/// <summary>
/// UI-safe intent descriptor (stringly-typed on purpose to avoid taking dependencies).
/// </summary>
public sealed record AiIntentSnapshot(
    string? Text,
    string? Target = null,
    IReadOnlyList<string>? Constraints = null,
    IReadOnlyList<string>? RequestedArtifacts = null)
{
    public IReadOnlyList<string> Constraints { get; } = Constraints ?? Array.Empty<string>();
    public IReadOnlyList<string> RequestedArtifacts { get; } = RequestedArtifacts ?? Array.Empty<string>();
}

/// <summary>
/// UI-safe workspace snapshot. Uses strings to avoid depending on tier/capability enums.
/// </summary>
public sealed record AiWorkspaceSnapshot
{
    public string? Name { get; }
    public string? RootPath { get; }

    // Avoid ToolpackTier dependency: store as label/id.
    public string Tier { get; }

    // Avoid ToolpackCapability dependency: store as labels/ids.
    public IReadOnlyList<string> AllowedCapabilities { get; }

    public AiWorkspaceSnapshot(
        string? name,
        string? rootPath,
        string tier,
        IReadOnlyList<string>? allowedCapabilities)
    {
        Name = name;
        RootPath = rootPath;
        Tier = tier ?? throw new ArgumentNullException(nameof(tier));
        AllowedCapabilities = allowedCapabilities ?? Array.Empty<string>();
    }
}

/// <summary>
/// UI-safe role snapshot (no dependency on RoleDescriptor).
/// </summary>
public sealed record AiRoleSnapshot(
    string? Name,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Claims = null)
{
    public IReadOnlyDictionary<string, string> Claims { get; } = Claims ?? new Dictionary<string, string>();
}

/// <summary>
/// UI-safe surface snapshot (no dependency on IAiHelpSurface).
/// </summary>
public sealed record AiHelpSurfaceSnapshot(
    string SurfaceId,
    string? Title = null,
    string? Summary = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public IReadOnlyDictionary<string, string> Data { get; } = Data ?? new Dictionary<string, string>();
}