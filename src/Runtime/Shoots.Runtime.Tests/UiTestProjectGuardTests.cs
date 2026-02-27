using System;
using System.IO;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class UiTestProjectGuardTests
{
    [Fact]
    public void Ui_test_project_is_guarded_on_non_windows()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var uiTestProjectPath = Path.Combine(repoRoot, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj");
        var projectXml = File.ReadAllText(uiTestProjectPath);

        Assert.Contains("Condition=\"'$(OS)' != 'Windows_NT'\"", projectXml);
        Assert.Contains("<IsTestProject Condition=\"'$(OS)' != 'Windows_NT'\">false</IsTestProject>", projectXml);
    }
}
