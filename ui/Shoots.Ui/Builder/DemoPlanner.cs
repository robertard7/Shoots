using System.Collections.Generic;
using Shoots.UI.Projects;

namespace Shoots.UI.Builder;

public sealed class DemoPlanner : IPlanner
{
    public bool TryBuildPlan(ProjectModel project, out PlanModel plan)
    {
        var steps = new List<PlanStep>
        {
            new(
                StepId: "step-001",
                ToolId: "create_directory",
                Args: new Dictionary<string, string> { ["path"] = "artifacts/demo" },
                OutputPath: "artifacts/demo"),
            new(
                StepId: "step-002",
                ToolId: "write_text",
                Args: new Dictionary<string, string>
                {
                    ["path"] = "artifacts/demo/output.txt",
                    ["text"] = "demo output"
                },
                OutputPath: "artifacts/demo/output.txt"),
            new(
                StepId: "step-003",
                ToolId: "dotnet.build",
                Args: new Dictionary<string, string>
                {
                    ["project"] = "DemoProject.csproj"
                },
                OutputPath: "artifacts/build-logs/dotnet-build.log")
        };

        plan = new PlanModel("demo-plan-v1", PlanSourceType.Demo, steps);
        return true;
    }
}
