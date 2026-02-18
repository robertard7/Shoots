using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Abstractions;
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

        var secondPlan = CreatePlan("wo-run-planchange", commandId: "core.route.v2");

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
    public void Waiting_plan_hash_change_with_injected_decision_digest_allows_progress()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var firstPlan = CreatePlan("wo-run-planchange-inject", commandId: "core.route.v1");
        var waiting = orchestrator.Run(firstPlan);
        Assert.Equal(RoutingStatus.Waiting, waiting.State.Status);

        decisions.Enabled = true;

        var secondPlan = CreatePlan("wo-run-planchange-inject", commandId: "core.route.v2");
        var resumed = orchestrator.Run(secondPlan, new RuntimeRunOptions(ResumeMode.InjectDecision, "digest-v2"));

        Assert.Equal(RoutingStatus.Completed, resumed.State.Status);
        Assert.True(decisions.Calls >= 2);
    }

    [Fact]
    public void Waiting_plan_hash_change_override_flag_and_mode_are_equivalent()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var firstPlan = CreatePlan("wo-run-planchange-eq", commandId: "core.route.v1");
        var waiting = orchestrator.Run(firstPlan);
        Assert.Equal(RoutingStatus.Waiting, waiting.State.Status);

        var secondPlan = CreatePlan("wo-run-planchange-eq", commandId: "core.route.v2");
        var stateStore = (IRunResumeStateStore)persistence;
        var before = stateStore.LoadByWorkOrderId("wo-run-planchange-eq")!;

        var overrideByMode = orchestrator.Run(secondPlan, new RuntimeRunOptions(ResumeMode.OverridePlanChange));
        Assert.Equal(RoutingStatus.Waiting, overrideByMode.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostResumeOverridePlanChange, overrideByMode.Trace.Entries[^1].Event);

        var afterMode = stateStore.LoadByWorkOrderId("wo-run-planchange-eq")!;
        Assert.Equal(before.AttemptCounter, afterMode.AttemptCounter);
        Assert.Equal(before.ProgressToken, afterMode.ProgressToken);

        var overrideByFlag = orchestrator.Run(secondPlan, new RuntimeRunOptions(ResumeMode.None, null, AllowPlanChangeOverride: true));
        Assert.Equal(RoutingStatus.Waiting, overrideByFlag.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostResumeOverridePlanChange, overrideByFlag.Trace.Entries[^1].Event);

        var afterFlag = stateStore.LoadByWorkOrderId("wo-run-planchange-eq")!;
        Assert.Equal(before.AttemptCounter, afterFlag.AttemptCounter);
        Assert.Equal(before.ProgressToken, afterFlag.ProgressToken);
    }

    [Fact]
    public void Same_content_different_plan_id_does_not_trigger_plan_changed_reason()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var firstPlan = CreatePlan("wo-run-planid-samecontent", commandId: "core.route.v1");
        var waiting = orchestrator.Run(firstPlan);
        Assert.Equal(RoutingStatus.Waiting, waiting.State.Status);

        var sameContentDifferentPlanId = firstPlan with { PlanId = "other-plan-id" };
        var blocked = orchestrator.Run(sameContentDifferentPlanId);

        Assert.Equal(RoutingStatus.Waiting, blocked.State.Status);
        Assert.NotNull(blocked.Waiting);
        Assert.Equal("decision_required", blocked.Waiting!.ReasonCode);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, blocked.Trace.Entries[^1].Event);
    }

    [Fact]
    public void Different_content_same_plan_id_requires_explicit_override()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var firstPlan = CreatePlan("wo-run-sameplanid-diffcontent", commandId: "core.route.v1");
        var waiting = orchestrator.Run(firstPlan);
        Assert.Equal(RoutingStatus.Waiting, waiting.State.Status);

        var changedContent = CreatePlan("wo-run-sameplanid-diffcontent", commandId: "core.route.v2") with { PlanId = firstPlan.PlanId };
        var blocked = orchestrator.Run(changedContent);

        Assert.Equal(RoutingStatus.Waiting, blocked.State.Status);
        Assert.Equal("plan_changed_requires_explicit_resume", blocked.Waiting!.ReasonCode);
    }

    [Fact]
    public void Injected_decision_digest_must_change_to_unblock_rerun()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var plan = CreatePlan("wo-run-digest");
        var first = orchestrator.Run(plan, new RuntimeRunOptions(ResumeMode.InjectDecision, "digest-A"));
        Assert.Equal(RoutingStatus.Waiting, first.State.Status);
        Assert.Equal(1, decisions.Calls);

        decisions.Enabled = true;

        var sameDigest = orchestrator.Run(plan, new RuntimeRunOptions(ResumeMode.InjectDecision, "digest-A"));
        Assert.Equal(RoutingStatus.Waiting, sameDigest.State.Status);
        Assert.Equal(1, decisions.Calls);

        var newDigest = orchestrator.Run(plan, new RuntimeRunOptions(ResumeMode.InjectDecision, "digest-B"));
        Assert.Equal(RoutingStatus.Completed, newDigest.State.Status);
        Assert.True(decisions.Calls >= 2);
    }

    [Fact]
    public void InMemory_persistence_indexes_envelope_by_plan_id_and_plan_hash()
    {
        var persistence = new InMemoryRuntimePersistence();
        var decisions = new ToggleDecisionProvider();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            decisions,
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var firstPlan = CreatePlan("wo-run-index", commandId: "core.route.v1");
        var firstEnvelope = orchestrator.Run(firstPlan);
        var firstHash = BuildPlanIdentity.ComputePlanHash(firstPlan);

        Assert.NotNull(persistence.Load(firstPlan.PlanId));
        Assert.NotNull(persistence.Load(firstHash));

        var secondPlan = CreatePlan("wo-run-index", commandId: "core.route.v2") with { PlanId = firstPlan.PlanId };
        decisions.Enabled = true;
        var secondEnvelope = orchestrator.Run(secondPlan, new RuntimeRunOptions(ResumeMode.InjectDecision, "digest-index-v2"));
        var secondHash = BuildPlanIdentity.ComputePlanHash(secondPlan);

        var byPlanId = persistence.Load(firstPlan.PlanId);
        var byFirstHashAfterSecondSave = persistence.Load(firstHash);
        var bySecondHash = persistence.Load(secondHash);

        Assert.NotNull(byPlanId);
        Assert.NotNull(byFirstHashAfterSecondSave);
        Assert.NotNull(bySecondHash);
        Assert.Equal(secondEnvelope.Plan.Request.CommandId, byPlanId!.Plan.Request.CommandId);
        Assert.Equal(firstEnvelope.Plan.Request.CommandId, byFirstHashAfterSecondSave!.Plan.Request.CommandId);
        Assert.Equal(secondEnvelope.Plan.Request.CommandId, bySecondHash!.Plan.Request.CommandId);
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

    private static BuildPlan CreatePlan(string workOrderId, string commandId = "core.route")
    {
        var workOrder = new WorkOrder(
            new WorkOrderId(workOrderId),
            "Original request.",
            "Run eligibility.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            commandId,
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
