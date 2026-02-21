using Shoots.Host.Abstractions;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Client.Tests;

public sealed class ShootsClientGoldenFlowTests
{
    [Fact]
    public async Task Golden_flow_waiting_blocked_resume_complete()
    {
        var client = new Shoots.Client.ShootsClient(new Shoots.Client.ShootsClientOptions());

        var workOrder = await client.CreateWorkOrderAsync(
            "build me a deterministic demo",
            "ai builder",
            Array.Empty<string>(),
            Array.Empty<string>());

        var plan = await client.PreviewPlanAsync(workOrder);
        Assert.Equal(workOrder.Id.Value, plan.Request.WorkOrder.Id.Value);

        var first = await client.RunAsync(workOrder.Id.Value);
        Assert.Equal(RoutingStatus.Waiting, first.State.Status);

        var blocked = await client.RunAsync(workOrder.Id.Value);
        Assert.Equal(RoutingStatus.Waiting, blocked.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, blocked.Trace.Entries[^1].Event);

        var resumed = await client.ResumeAsync(workOrder.Id.Value, new HostResumeIntent(HostResumeIntentMode.InjectDecision));
        Assert.Equal(RoutingStatus.Completed, resumed.State.Status);
    }
}
