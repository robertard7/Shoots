using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Host.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public sealed class HostExecutionService : IHostExecutionService
{
    private readonly IExecutionCommandService _execution;

    public HostExecutionService(IExecutionCommandService execution)
    {
        _execution = execution;
    }

    public WorkOrder CreateWorkOrder(string originalRequest, string intent, IReadOnlyList<string> constraints, IReadOnlyList<string> requestedArtifacts)
    {
        var id = new WorkOrderId($"wo-{Guid.NewGuid():N}");
        return new WorkOrder(id, originalRequest, intent, constraints, requestedArtifacts);
    }

    public BuildPlan PreviewPlan(BuildRequest request, DelegationAuthority authority, IReadOnlyList<BuildStep> steps, IReadOnlyList<BuildArtifact> artifacts)
    {
        var planId = HashTools.ComputeSha256Hash($"{request.CommandId}|{request.WorkOrder.Id.Value}|{steps.Count}");
        return new BuildPlan(planId, request, "preview-graph", "preview-nodes", "preview-edges", authority, steps, artifacts);
    }

    public Task<RuntimeResult> RunAsync(BuildPlan plan, RuntimeRunOptions? options = null, CancellationToken ct = default)
        => _execution.StartAsync(plan, options, ct);

    public Task<RuntimeResult> ResumeAsync(BuildPlan plan, DecisionInjectionRequest request, CancellationToken ct = default)
    {
        var digest = DecisionDigest.Compute(request);
        return _execution.StartAsync(plan, new RuntimeRunOptions(ResumeMode.InjectDecision, digest), ct);
    }

    public Shoots.Contracts.Core.ToolCatalogSnapshot GetToolCatalogSnapshot(BuildPlan plan)
    {
        var tools = plan.Steps
            .OfType<ToolBuildStep>()
            .Select(step => new ToolSpec(
                step.ToolId,
                "UI preview",
                new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
                Array.Empty<ToolInputSpec>(),
                Array.Empty<ToolOutputSpec>(),
                Array.Empty<string>()))
            .ToList();

        return new Shoots.Contracts.Core.ToolCatalogSnapshot(
            HashTools.ComputeSha256Hash(string.Join("|", tools.Select(x => x.ToolId.Value))),
            tools);
    }
}
