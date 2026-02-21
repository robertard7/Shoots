using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Shoots.Host.Tests;

public sealed class HostBoundaryGuardTests
{
    [Fact]
    public void Ui_project_does_not_reference_runtime_core()
    {
        var root = ResolveRepoRoot();
        var uiCsproj = Path.Combine(root, "ui", "Shoots.Ui", "Shoots.Ui.csproj");
        var doc = XDocument.Load(uiCsproj);

        var references = doc
            .Descendants("ProjectReference")
            .Select(x => x.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, r => r.Contains("Shoots.Runtime.Core.csproj", StringComparison.Ordinal));
    }

    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", i))));
            if (File.Exists(Path.Combine(candidate, "Shoots.sln")))
                return candidate;
        }

        throw new InvalidOperationException("Unable to resolve repository root from test base directory.");
    }
}
