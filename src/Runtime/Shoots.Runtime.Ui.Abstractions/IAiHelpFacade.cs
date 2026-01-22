using System.Threading;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using RuntimeCatalogSnapshot = Shoots.Runtime.Abstractions.ToolCatalogSnapshot;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IAiHelpFacade
{
    // AI Help is descriptive only and never triggers execution.
    Task<string> GetContextSummaryAsync(AiHelpRequest request, CancellationToken ct = default);

    Task<string> ExplainStateAsync(AiHelpRequest request, CancellationToken ct = default);

    Task<string> SuggestNextStepsAsync(AiHelpRequest request, CancellationToken ct = default);
}

public sealed record AiHelpRequest(
    AiWorkspaceSnapshot Workspace,
    BuildPlan? Plan,
    RuntimeCatalogSnapshot? ToolCatalog,
    string? ExecutionState,
    string? EnvironmentProfile,
    string? LastAppliedProfile,
    RoleDescriptor? Role);

public sealed record AiWorkspaceSnapshot(
    string? Name,
    string? RootPath,
    ToolpackTier Tier,
    IReadOnlyList<ToolpackCapability> AllowedCapabilities
);
