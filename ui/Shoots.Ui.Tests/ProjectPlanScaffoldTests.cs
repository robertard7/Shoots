using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Shoots.UI.ViewModels;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class ProjectPlanScaffoldTests
{
    [Fact]
    public void Scaffold_plan_hash_is_deterministic_for_same_semantic_inputs()
    {
        var rootA = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var rootB = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);

        try
        {
            InvokeScaffold(rootA, DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"));
            InvokeScaffold(rootB, DateTimeOffset.Parse("2025-01-01T00:00:00+00:00"));

            var hashA = ReadPlanHash(rootA);
            var hashB = ReadPlanHash(rootB);

            Assert.Equal(hashA, hashB);
        }
        finally
        {
            if (Directory.Exists(rootA)) Directory.Delete(rootA, true);
            if (Directory.Exists(rootB)) Directory.Delete(rootB, true);
        }
    }

    private static void InvokeScaffold(string root, DateTimeOffset createdUtc)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "CreateProjectScaffold",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[]
        {
            root,
            "abc12345",
            "Demo",
            "demo desc",
            "dotnet",
            "Local",
            string.Empty,
            "host-local",
            createdUtc
        });
    }

    private static string ReadPlanHash(string root)
    {
        var planPath = Path.Combine(root, "plan", "plan.json");
        var doc = JsonDocument.Parse(File.ReadAllText(planPath));
        return doc.RootElement.GetProperty("planHash").GetString() ?? string.Empty;
    }
}
