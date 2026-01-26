using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

// EXPLICIT ALIASES — this is the entire fix
using RuntimeToolCatalogSnapshot = Shoots.Runtime.Abstractions.ToolCatalogSnapshot;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IAiHelpFacade
{
    // AI Help is descriptive only and never triggers execution.
    Task<string> GetContextSummaryAsync(AiHelpRequest request, CancellationToken ct = default);

    Task<string> ExplainStateAsync(AiHelpRequest request, CancellationToken ct = default);

    Task<string> SuggestNextStepsAsync(AiHelpRequest request, CancellationToken ct = default);
}

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
        SurfaceId = surfaceId;
        Summary = summary;
        Data = data;
    }

    public AiHelpScope(string surfaceId, string? summary)
        : this(surfaceId, summary, new Dictionary<string, string>())
    {
    }
}

public sealed record AiHelpRequest
{
    public AiHelpScope Scope { get; }
    public AiIntentDescriptor Intent { get; }
    public AiWorkspaceSnapshot Workspace { get; }
    public BuildPlan? Plan { get; }

    // ✅ RUNTIME snapshot only — UI never uses contract catalog
    public RuntimeToolCatalogSnapshot? ToolCatalog { get; }

    public string? ExecutionState { get; }
    public string? EnvironmentProfile { get; }
    public string? LastAppliedProfile { get; }
    public RoleDescriptor? Role { get; }
    public IReadOnlyList<IAiHelpSurface> Surfaces { get; }

    public AiHelpRequest(
        AiHelpScope scope,
        AiIntentDescriptor intent,
        AiWorkspaceSnapshot workspace,
        BuildPlan? plan,
        RuntimeToolCatalogSnapshot? toolCatalog,
        string? executionState,
        string? environmentProfile,
        string? lastAppliedProfile,
        RoleDescriptor? role,
        IReadOnlyList<IAiHelpSurface>? surfaces)
    {
        Scope = scope;
        Intent = intent;
        Workspace = workspace;
        Plan = plan;
        ToolCatalog = toolCatalog;
        ExecutionState = executionState;
        EnvironmentProfile = environmentProfile;
        LastAppliedProfile = lastAppliedProfile;
        Role = role;
        Surfaces = surfaces ?? Array.Empty<IAiHelpSurface>();
    }
}

public sealed record AiWorkspaceSnapshot
{
    public string? Name { get; }
    public string? RootPath { get; }
    public ToolpackTier Tier { get; }
    public IReadOnlyList<ToolpackCapability> AllowedCapabilities { get; }

    public AiWorkspaceSnapshot(
        string? name,
        string? rootPath,
        ToolpackTier tier,
        IReadOnlyList<ToolpackCapability> allowedCapabilities)
    {
        Name = name;
        RootPath = rootPath;
        Tier = tier;
        AllowedCapabilities = allowedCapabilities;
    }
}
