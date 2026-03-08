using System;
using System.Collections.Generic;
using System.Text.Json;
using Shoots.UI.Builder;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ExecutionContractSnapshotTests
{
    [Fact]
    public void ExecutionRequest_snapshot_matches_contract_v1()
    {
        var plan = new PlanModel(
            "plan-1",
            PlanSourceType.Demo,
            new[] { new PlanStep("step-1", "write_text", new Dictionary<string, string> { ["path"] = "a.txt" }, "a.txt") });
        var project = new ProjectModel("p1", "demo", "C:/workspace/demo", DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"));

        var request = ExecutionContractAdapter.ToExecutionRequest(plan, project, "RuntimePlanner", "RuntimeBridgeLocal", "local", "none", "hash-plan");
        var actual = ToSnapshot(request);

        const string expected = """
{
  "ContractVersion": "ui-runtime-v1",
  "ProjectId": "p1",
  "WorkspacePath": "C:/workspace/demo",
  "Plan": {
    "PlanId": "plan-1",
    "PlanHash": "hash-plan",
    "Steps": [
      {
        "StepId": "step-1",
        "ToolId": "write_text",
        "Args": {
          "path": "a.txt"
        },
        "OutputPath": "a.txt"
      }
    ]
  },
  "PlannerSource": "RuntimePlanner",
  "RuntimeBridge": "RuntimeBridgeLocal",
  "Provider": "local",
  "HostTransport": "none"
}
""";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExecutionResult_snapshot_matches_contract_v1()
    {
        var run = new RunModel(
            "000001",
            "p1",
            "plan-1",
            "hash-plan",
            "hash-catalog",
            "hash-workspace",
            DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
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
        var actual = ToSnapshot(result);

        const string expected = """
{
  "ContractVersion": "ui-runtime-v1",
  "RunId": "000001",
  "Status": "completed",
  "ToolInvocations": [
    {
      "StepId": "step-1",
      "ToolId": "write_text",
      "Status": "completed",
      "OutputPath": "a.txt",
      "Error": null
    }
  ],
  "Evidence": {
    "EnvironmentHash": "hash-env",
    "ManifestHash": "hash-manifest",
    "NarratorHash": "hash-narrator",
    "TranscriptHash": "hash-transcript",
    "EvidenceBundleHash": "hash-evidence",
    "ReproWarning": null
  },
  "PlannerSource": "RuntimePlanner",
  "RuntimeBridge": "RuntimeBridgeLocal",
  "Provider": "local",
  "HostTransport": "none"
}
""";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExecutionPlan_snapshot_matches_contract_v1()
    {
        var plan = new ExecutionPlan(
            "plan-1",
            "hash-plan",
            new[] { new ExecutionStep("step-1", "write_text", new Dictionary<string, string> { ["path"] = "a.txt" }, "a.txt") });

        var actual = ToSnapshot(plan);
        const string expected = """
{
  "PlanId": "plan-1",
  "PlanHash": "hash-plan",
  "Steps": [
    {
      "StepId": "step-1",
      "ToolId": "write_text",
      "Args": {
        "path": "a.txt"
      },
      "OutputPath": "a.txt"
    }
  ]
}
""";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ToolInvocationRecord_snapshot_matches_contract_v1()
    {
        var invocation = new ToolInvocationRecord("step-1", "write_text", "completed", "a.txt", null);
        var actual = ToSnapshot(invocation);
        const string expected = """
{
  "StepId": "step-1",
  "ToolId": "write_text",
  "Status": "completed",
  "OutputPath": "a.txt",
  "Error": null
}
""";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExecutionEvidence_snapshot_matches_contract_v1()
    {
        var evidence = new ExecutionEvidence("hash-env", "hash-manifest", "hash-narrator", "hash-transcript", "hash-evidence", null);
        var actual = ToSnapshot(evidence);
        const string expected = """
{
  "EnvironmentHash": "hash-env",
  "ManifestHash": "hash-manifest",
  "NarratorHash": "hash-narrator",
  "TranscriptHash": "hash-transcript",
  "EvidenceBundleHash": "hash-evidence",
  "ReproWarning": null
}
""";

        Assert.Equal(expected, actual);
    }

    private static string ToSnapshot<T>(T value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
}
