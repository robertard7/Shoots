using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Shoots.Host.Abstractions;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.Host.Tests;

public sealed class ApiSurfaceFreezeTests
{
    [Fact]
    public void Host_execution_service_shape_is_frozen()
    {
        var signatures = typeof(IHostExecutionService)
            .GetMethods()
            .Select(m =>
                $"{m.ReturnType.Name} {m.Name}(" +
                $"{string.Join(", ", m.GetParameters()
                    .Select(p => p.ParameterType.Name + " " + p.Name))})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "BuildPlan PreviewPlan(BuildRequest request, DelegationAuthority authority, IReadOnlyList`1 steps, IReadOnlyList`1 artifacts)",
            "Task`1 ResumeAsync(BuildPlan plan, DecisionInjectionRequest request, HostResumeIntent intent, CancellationToken ct)",
            "Task`1 RunAsync(BuildPlan plan, HostRunOptions options, CancellationToken ct)",
            "ToolCatalogSnapshot GetToolCatalogSnapshot(BuildPlan plan)",
            "WorkOrder CreateWorkOrder(String originalRequest, String intent, IReadOnlyList`1 constraints, IReadOnlyList`1 requestedArtifacts)"
        }, signatures);
    }

    [Fact]
    public void Host_model_router_public_entrypoints_are_frozen()
    {
        var methods = typeof(Shoots.Host.Core.HostModelRouter)
            .GetMethods()
            .Where(m =>
                m.IsPublic &&
                !m.IsSpecialName &&
                m.DeclaringType == typeof(Shoots.Host.Core.HostModelRouter))
            .Select(m => m.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Select" }, methods);
    }
}