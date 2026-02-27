using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class UiTestProjectGuardTests
{
    [Fact]
    public void Ui_test_project_is_guarded_on_non_windows()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var uiTestProjectPath = Path.Combine(repoRoot, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj");

        var projectDocument = XDocument.Load(uiTestProjectPath);
        var isTestProjectElement = projectDocument
            .Descendants("IsTestProject")
            .SingleOrDefault(element => (string?)element.Attribute("Condition") == "'$(OS)' != 'Windows_NT'");

        Assert.NotNull(isTestProjectElement);
        Assert.Equal("false", isTestProjectElement!.Value.Trim());
    }
}
