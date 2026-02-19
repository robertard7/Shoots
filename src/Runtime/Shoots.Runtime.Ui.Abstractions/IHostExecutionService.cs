using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Ui.Abstractions;

public interface IHostExecutionService
{
    WorkOrder CreateWorkOrder(string originalRequest, string intent, IReadOnlyList<string> constraints, IReadOnlyList<string> requestedArtifacts);

    BuildPlan PreviewPlan(BuildRequest request, DelegationAuthority authority, IReadOnlyList<BuildStep> steps, IReadOnlyList<BuildArtifact> artifacts);

    Task<RuntimeResult> RunAsync(BuildPlan plan, RuntimeRunOptions? options = null, CancellationToken ct = default);

    Task<RuntimeResult> ResumeAsync(BuildPlan plan, DecisionInjectionRequest request, CancellationToken ct = default);

    Shoots.Contracts.Core.ToolCatalogSnapshot GetToolCatalogSnapshot(BuildPlan plan);
}
