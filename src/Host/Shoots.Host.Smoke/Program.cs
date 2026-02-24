using System.Text.Json;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Abstractions.Provider;
using Shoots.Runtime.Core;
using Shoots.ProviderAdapters.Abstractions;
using Shoots.ProviderAdapters.Embedded;

var command = args.FirstOrDefault() ?? "ChatIntakeSmoke";
if (!string.Equals(command, "ChatIntakeSmoke", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Unknown command. Supported: ChatIntakeSmoke");
    return 2;
}

var workOrderId = $"wo-smoke-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
var repoRoot = System.IO.Directory.GetCurrentDirectory();
var orchestrator = new RuntimeOrchestrator(
    new SmokeToolRegistry(),
    new WaitingThenDecisionProvider(new ToolId("linux.fs.write_text.v1")),
    NullRuntimeNarrator.Instance,
    new EmbeddedToolProviderClient(repoRoot),
    new InMemoryRuntimePersistence());

var plan = BuildPlan(workOrderId);
var waiting = orchestrator.Run(plan);
if (waiting.State.Status != RoutingStatus.Waiting || waiting.Waiting is null)
    throw new InvalidOperationException("Expected WAITING on first run.");

var blocked = orchestrator.Run(plan);
if (blocked.State.Status != RoutingStatus.Waiting || blocked.Trace.Entries[^1].Event != RoutingTraceEventKind.HostBlockedRerunWaiting)
    throw new InvalidOperationException("Expected blocked waiting rerun without host intent.");

var payload = new
{
    WorkOrderId = waiting.Waiting.WorkOrderId.Value,
    PlanHash = waiting.Waiting.PlanHash,
    NodeId = waiting.Waiting.CurrentNodeId,
    Policy = waiting.Waiting.Policy.ToString(),
    waiting.Waiting.AllowedNextNodes,
    ToolId = "linux.fs.write_text.v1",
    Bindings = new { path = "artifacts/smoke/local/tool-smoke.txt", text = "smoke" }
};
var canonicalPayload = JsonSerializer.Serialize(payload);
var resume = orchestrator.Run(plan, new RuntimeRunOptions(ResumeMode.InjectDecision, canonicalPayload));
if (resume.State.Status != RoutingStatus.Completed)
    throw new InvalidOperationException($"Expected COMPLETE after resume, got {resume.State.Status}.");

var tracePath = Path.GetFullPath(Path.Combine(".state", "trace", $"{workOrderId}.trace.json"));
var artifactDir = Path.GetFullPath(Path.Combine(".state", "artifacts", workOrderId));
Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
Directory.CreateDirectory(artifactDir);
File.WriteAllText(tracePath, JsonSerializer.Serialize(resume.Trace.Entries, new JsonSerializerOptions { WriteIndented = true }));

if (!File.Exists(tracePath))
    throw new InvalidOperationException("Expected persisted trace file.");
if (!Directory.Exists(artifactDir))
    throw new InvalidOperationException("Expected artifacts directory.");

Console.WriteLine($"Smoke OK. WorkOrder={workOrderId}");
Console.WriteLine($"Trace={tracePath}");
Console.WriteLine($"Artifacts={artifactDir}");
return 0;

static BuildPlan BuildPlan(string workOrderId)
{
    var workOrder = new WorkOrder(new WorkOrderId(workOrderId), "smoke request", "smoke intent", Array.Empty<string>(), Array.Empty<string>());
    var rules = new[]
    {
        new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "finish" }, DecisionPolicy.Hard),
        new RouteRule("finish", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
    };

    var request = new BuildRequest(workOrder, "chat.intake.smoke", new Dictionary<string, object?>(), rules);
    var authority = new DelegationAuthority(new ProviderId("smoke.local"), ProviderKind.Local, "smoke", true);
    var steps = new BuildStep[]
    {
        new RouteStep("select", "Select tool", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
        new RouteStep("finish", "Finish", "finish", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
    };

    return new BuildPlan("plan-smoke", request, "g", "n", "e", authority, steps, Array.Empty<BuildArtifact>());
}

file sealed class SmokeToolRegistry : IToolRegistry
{
    private static readonly ToolRegistryEntry Entry = new(
        new ToolSpec(
            new ToolId("linux.fs.write_text.v1"),
            "Smoke write tool",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.Execute),
            new[]
            {
                new ToolInputSpec("path", "string", true, "path"),
                new ToolInputSpec("text", "string", true, "text")
            },
            Array.Empty<ToolOutputSpec>(),
            Array.Empty<string>()));

    public string CatalogHash => "smoke.catalog";
    public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new[] { Entry };
    public ToolRegistryEntry? GetTool(ToolId toolId) => toolId.Value == Entry.Spec.ToolId.Value ? Entry : null;
    public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { Entry };
}

file sealed class WaitingThenDecisionProvider : IAiDecisionProvider
{
    private readonly ToolId _toolId;
    private int _calls;

    public WaitingThenDecisionProvider(ToolId toolId)
    {
        _toolId = toolId;
    }

    public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
    {
        _calls++;
        if (_calls == 1)
            return null;

        return new ToolSelectionDecision(_toolId, new Dictionary<string, object?>
        {
            ["path"] = "artifacts/smoke/local/tool-smoke.txt",
            ["text"] = "smoke"
        });
    }
}

