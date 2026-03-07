using System;
using System.Collections.Generic;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ExecutionContractAdapterTests
{
    [Fact]
    public void ToExecutionRequest_and_ToExecutionResult_preserve_core_shape()
    {
        var plan = new PlanModel(
            "plan-1",
            PlanSourceType.Demo,
            new[]
            {
                new PlanStep("step-1", "write_text", new Dictionary<string, string> { ["path"] = "a.txt" }, "a.txt")
            });
        var project = new ProjectModel("p1", "demo", "c:/workspace/demo", DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"));

        var request = ExecutionContractAdapter.ToExecutionRequest(plan, project, "RuntimePlanner", "RuntimeBridgeLocal", "local", "none", "hash-plan");

        Assert.Equal(ExecutionContract.Version, request.ContractVersion);
        Assert.Equal("hash-plan", request.Plan.PlanHash);
        Assert.Single(request.Plan.Steps);
        Assert.Equal("write_text", request.Plan.Steps[0].ToolId);

        var run = new RunModel(
            "000001",
            "p1",
            "plan-1",
            "hash-plan",
            "hash-catalog",
            "hash-workspace",
            DateTimeOffset.UtcNow,
            RunStates.Completed,
            new[] { new RunStep("step-1", "write_text", RunStates.Completed, "a.txt", null) },
            ExecutionContract.Version,
            "RuntimePlanner",
            "RuntimeBridgeLocal",
            "local",
            "none",
            "hash-env",
            "hash-manifest",
            "hash-narrator",
            "hash-transcript",
            "hash-evidence",
            null);

        var result = ExecutionContractAdapter.ToExecutionResult(run);

        Assert.Equal(ExecutionContract.Version, result.ContractVersion);
        Assert.Equal("RuntimeBridgeLocal", result.RuntimeBridge);
        Assert.Equal("local", result.Provider);
        Assert.Single(result.ToolInvocations);
        Assert.Equal("hash-manifest", result.Evidence.ManifestHash);
    }
}
