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
        var repoRoot = ResolveRepositoryRoot();
        var uiTestProjectPath = Path.Combine(repoRoot, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj");

        var projectDocument = XDocument.Load(uiTestProjectPath);
        var isTestProjectElement = projectDocument
            .Descendants("IsTestProject")
            .SingleOrDefault(element => (string?)element.Attribute("Condition") == "'$(OS)' != 'Windows_NT'");

        Assert.NotNull(isTestProjectElement);
        Assert.Equal("false", isTestProjectElement!.Value.Trim());
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "ui", "Shoots.Ui.Tests", "Shoots.Ui.Tests.csproj");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root containing ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj.");
    }
}
