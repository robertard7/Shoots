using System.Xml.Linq;
using Xunit;

namespace Shoots.Client.Tests;

public sealed class ShootsClientApiFreezeTests
{
    [Fact]
    public void Client_public_method_set_is_frozen()
    {
        var methods = typeof(Shoots.Client.ShootsClient)
                        .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "CreateWorkOrderAsync",
            "GetModelCatalogAsync",
            "GetSessionStateAsync",
            "GetToolCatalogAsync",
            "PreviewPlanAsync",
            "ResumeAsync",
            "RunAsync"
        }, methods);
    }

    [Fact]
    public void Client_project_does_not_reference_runtime_core()
    {
        var root = ResolveRepoRoot();
        var clientCsproj = Path.Combine(root, "src", "Client", "Shoots.Client", "Shoots.Client.csproj");
        var doc = XDocument.Load(clientCsproj);

        var refs = doc.Descendants("ProjectReference")
            .Select(x => x.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(refs, r => r.Contains("Shoots.Runtime.Core.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Client_sample_project_does_not_reference_runtime_core()
    {
        var root = ResolveRepoRoot();
        var sampleCsproj = Path.Combine(root, "src", "Client", "Shoots.Client.Sample", "Shoots.Client.Sample.csproj");
        var doc = XDocument.Load(sampleCsproj);

        var refs = doc.Descendants("ProjectReference")
            .Select(x => x.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(refs, r => r.Contains("Shoots.Runtime.Core.csproj", StringComparison.Ordinal));
    }

    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", i))));
            if (File.Exists(Path.Combine(candidate, "Shoots.sln")))
                return candidate;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }
}
