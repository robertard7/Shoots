using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Shoots.UI.Diagnostics;

namespace Shoots.UI.Builder;

public static class RunRecoveryService
{
    public static IReadOnlyList<string> MarkCrashedRunningRuns(string workspacePath, Action<NarrationEvent>? narrate = null)
    {
        var recovered = new List<string>();
        var runsRoot = Path.Combine(workspacePath, "runs");
        if (!Directory.Exists(runsRoot))
        {
            return recovered;
        }

        foreach (var runDir in Directory.GetDirectories(runsRoot))
        {
            var runJsonPath = Path.Combine(runDir, "run.json");
            if (!File.Exists(runJsonPath))
            {
                continue;
            }

            RunModel? run;
            try
            {
                run = JsonSerializer.Deserialize<RunModel>(File.ReadAllText(runJsonPath));
            }
            catch
            {
                continue;
            }

            if (run is null || !string.Equals(run.Status, RunStates.Running, StringComparison.Ordinal))
            {
                continue;
            }

            var updated = run with { Status = RunStates.FailedCrash };
            File.WriteAllText(runJsonPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));

            var narratorPath = Path.Combine(runDir, "narrator.jsonl");
            var evt = new NarrationEvent(DateTimeOffset.UtcNow, "warn", "RUN_RECOVERED_AS_FAILED_CRASH", new Dictionary<string, string>
            {
                ["run_id"] = run.RunId,
                ["status"] = RunStates.FailedCrash
            });
            File.AppendAllLines(narratorPath, new[] { JsonSerializer.Serialize(evt) });
            narrate?.Invoke(evt);
            recovered.Add(run.RunId);
        }

        return recovered;
    }
}
