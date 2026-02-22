using Shoots.Client;
using Shoots.Host.Abstractions;
using Shoots.Runtime.Abstractions;

var client = new ShootsClient(new ShootsClientOptions());

var workOrder = await client.CreateWorkOrderAsync(
    "build a deterministic sample",
    "sample",
    Array.Empty<string>(),
    Array.Empty<string>());

var plan = await client.PreviewPlanAsync(workOrder);
Console.WriteLine($"WorkOrder={workOrder.Id.Value}");
Console.WriteLine($"Plan={plan.PlanId}");

var first = await client.RunAsync(workOrder.Id.Value);
Console.WriteLine($"Run1={first.State.Status}");
if (first.State.Status != RoutingStatus.Waiting)
    return 2;

var blocked = await client.RunAsync(workOrder.Id.Value);
Console.WriteLine($"Run2={blocked.State.Status};Event={blocked.Trace.Entries[^1].Event}");
if (blocked.Trace.Entries[^1].Event != RoutingTraceEventKind.HostBlockedRerunWaiting)
    return 3;

var resumed = await client.ResumeAsync(workOrder.Id.Value, new HostResumeIntent(HostResumeIntentMode.InjectDecision));
Console.WriteLine($"Run3={resumed.State.Status}");
if (resumed.State.Status != RoutingStatus.Completed)
    return 4;

return 0;
