using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Shoots.UI.Builder;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class RunRecoveryServiceTests
{
    [Fact]
    public void MarkCrashedRunningRuns_marks_running_as_failed_crash()
    {
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var runPath = Path.Combine(workspace, "runs", "000001");
        Directory.CreateDirectory(runPath);

        try
        {
            var run = new RunModel(
                "000001",
                "p1",
                "plan-1",
                "planhash",
                "cataloghash",
                "workspacehash",
                DateTimeOffset.UtcNow,
                RunStates.Running,
                new List<RunStep>(),
                ExecutionContract.Version,
                "RuntimePlanner",
                "RuntimeBridgeLocal",
                "local",
                "none");
            File.WriteAllText(Path.Combine(runPath, "run.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));

            var recovered = RunRecoveryService.MarkCrashedRunningRuns(workspace);

            Assert.Single(recovered);
            var updated = JsonSerializer.Deserialize<RunModel>(File.ReadAllText(Path.Combine(runPath, "run.json")));
            Assert.NotNull(updated);
            Assert.Equal(RunStates.FailedCrash, updated!.Status);
            Assert.True(File.Exists(Path.Combine(runPath, "narrator.jsonl")));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }
}
