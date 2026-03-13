using System.IO;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class CiParityWorkflowTests
{
    [Fact]
    public void Workflow_runs_windows_validation_parity_script_and_uploads_validation_artifacts()
    {
        var root = FindRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains(@".\scripts\validate_ci_parity.ps1", workflow, System.StringComparison.Ordinal);
        Assert.Contains("artifacts/validation/**", workflow, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_parity_script_runs_the_phase11_windows_loop()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "validate_ci_parity.ps1"));

        Assert.Contains(@"dotnet build .\ui\Shoots.Ui\Shoots.Ui.csproj -c $Configuration -v minimal", script, System.StringComparison.Ordinal);
        Assert.Contains(@"dotnet test .\ui\Shoots.Ui.Tests\Shoots.Ui.Tests.csproj -c $Configuration -v minimal", script, System.StringComparison.Ordinal);
        Assert.Contains(@"powershell -File .\tools\smoke\windows\ui_smoke.ps1 -Configuration $Configuration", script, System.StringComparison.Ordinal);
        Assert.Contains(@"powershell -File .\tools\verify\windows_compile_runtime_integrity.ps1 -Configuration $Configuration", script, System.StringComparison.Ordinal);
        Assert.Contains(@"powershell -ExecutionPolicy Bypass -File .\scripts\validate_build.ps1 -Configuration $Configuration", script, System.StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "Shoots.sln")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        throw new DirectoryNotFoundException("Could not locate Shoots.sln from test base directory.");
    }
}
