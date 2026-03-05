using System;
using System.Collections.Generic;
using System.IO;
using Shoots.UI.Builder;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ToolExecutionServiceTests
{
    [Fact]
    public void ExecuteStep_rejects_path_outside_workspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            var registry = new ToolRegistry("etc/ui.tools.catalog.json");
            var service = new ToolExecutionService(registry);
            var step = new PlanStep("step-001", "write_text", new Dictionary<string, string>
            {
                ["path"] = "../outside.txt",
                ["text"] = "nope"
            }, "../outside.txt");

            var ex = Assert.Throws<InvalidOperationException>(() => service.ExecuteStep(step, workspace));
            Assert.Contains("escapes workspace", ex.Message, StringComparison.OrdinalIgnoreCase);
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
