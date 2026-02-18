using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Abstractions.Provider;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RuntimeOrchestratorRunEligibilityTests
{
    [Fact]
    public void Waiting_rerun_without_progress_is_blocked_without_runtime_reinvoke()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var plan = CreatePlan("wo-run-block");

        var first = orchestrator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, first.State.Status);
        Assert.Equal(1, decisions.Calls);

        decisions.Enabled = true;
        var second = orchestrator.Run(plan);

        Assert.Equal(RoutingStatus.Waiting, second.State.Status);
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, second.Trace.Entries[^1].Event);
    }

    [Fact]
    public void Waiting_rerun_with_injected_decision_digest_allows_progress()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var plan = CreatePlan("wo-run-inject");
        var first = orchestrator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, first.State.Status);

        decisions.Enabled = true;
        var second = orchestrator.Run(plan, new RuntimeRunOptions(ResumeMode.InjectDecision, "digest-2"));

        Assert.Equal(RoutingStatus.Completed, second.State.Status);
        Assert.True(decisions.Calls >= 2);
    }

    [Fact]
    public void Waiting_plan_hash_change_requires_override_mode()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var firstPlan = CreatePlan("wo-run-planchange");
        var waiting = orchestrator.Run(firstPlan);
        Assert.Equal(RoutingStatus.Waiting, waiting.State.Status);

        var secondPlan = firstPlan with { PlanId = "plan-v2" };

        decisions.Enabled = true;

        var blocked = orchestrator.Run(secondPlan);
        Assert.Equal(RoutingStatus.Waiting, blocked.State.Status);
        Assert.Equal("plan_changed_requires_explicit_resume", blocked.Waiting!.ReasonCode);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, blocked.Trace.Entries[^1].Event);

        var resumed = orchestrator.Run(secondPlan, new RuntimeRunOptions(ResumeMode.OverridePlanChange, null, AllowPlanChangeOverride: true));
        Assert.Equal(RoutingStatus.Waiting, resumed.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostResumeOverridePlanChange, resumed.Trace.Entries[^1].Event);
    }

    [Fact]
    public void Waiting_rerun_loop_is_idempotently_blocked_and_does_not_mutate_progress_state()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var plan = CreatePlan("wo-run-loop");
        var first = orchestrator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, first.State.Status);

        var stateStore = (IRunResumeStateStore)persistence;
        var before = stateStore.LoadByWorkOrderId("wo-run-loop")!;

        var second = orchestrator.Run(plan);
        var third = orchestrator.Run(plan);

        Assert.Equal(RoutingStatus.Waiting, second.State.Status);
        Assert.Equal(RoutingStatus.Waiting, third.State.Status);
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, second.Trace.Entries[^1].Event);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, third.Trace.Entries[^1].Event);

        var after = stateStore.LoadByWorkOrderId("wo-run-loop")!;
        Assert.Equal(before.AttemptCounter, after.AttemptCounter);
        Assert.Equal(before.ProgressToken, after.ProgressToken);
    }

    [Fact]
    public void Discard_waiting_start_over_emits_host_discard_marker()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var plan = CreatePlan("wo-run-discard");
        var first = orchestrator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, first.State.Status);

        var restarted = orchestrator.Run(plan, new RuntimeRunOptions(ResumeMode.DiscardWaitingStartOver, DiscardWaiting: true));
        Assert.Equal(RoutingStatus.Waiting, restarted.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostResumeDiscardWaiting, restarted.Trace.Entries[^1].Event);
    }

    private static BuildPlan CreatePlan(string workOrderId)
    {
        var workOrder = new WorkOrder(
            new WorkOrderId(workOrderId),
            "Original request.",
            "Run eligibility.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, System.Array.Empty<string>())
            });

        var steps = new BuildStep[]
        {
            new RouteStep("select", "Select tool.", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("terminate", "Terminate route.", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        };

        return BuildPlanTestFactory.CreatePlan(request, steps);
    }

    private sealed class ToggleDecisionProvider : IAiDecisionProvider
    {
        public bool Enabled { get; set; }
        public int Calls { get; private set; }

        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
        {
            Calls++;
            if (!Enabled)
                return null;

            return new ToolSelectionDecision(new ToolId("tools.sample"), new Dictionary<string, object?>());
        }
    }

    private sealed class SuccessfulProviderClient : IProviderClient
    {
        public ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, System.Threading.CancellationToken ct)
        {
            var result = new ToolResult(
                new ToolId("tools.sample"),
                new Dictionary<string, object?> { ["output"] = "ok" },
                true);

            return ValueTask.FromResult(new ProviderExecutionResult(
                envelope.RequestId,
                ProviderExecutionResultKind.ToolExecuted,
                result,
                null,
                null,
                null));
        }
    }

    private sealed class SampleToolRegistry : IToolRegistry
    {
        public string CatalogHash => "sample";

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => GetSnapshot();

        public ToolRegistryEntry? GetTool(ToolId toolId)
        {
            return toolId.Value == "tools.sample"
                ? new ToolRegistryEntry(CreateToolSpec())
                : null;
        }

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { new ToolRegistryEntry(CreateToolSpec()) };

        private static ToolSpec CreateToolSpec()
        {
            return new ToolSpec(
                new ToolId("tools.sample"),
                "Sample tool.",
                new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
                new List<ToolInputSpec>(),
                new List<ToolOutputSpec>(),
                new[] { "sample" });
        }
    }
}
