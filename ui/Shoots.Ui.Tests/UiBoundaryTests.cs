using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI;
using Shoots.UI.Environment;
using Shoots.UI.Projects;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class UiBoundaryTests
{
    [Fact]
    public void UiDoesNotReferenceRuntimeCore()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "Shoots.Runtime.Core");
    }

    [Fact]
    public void UiDoesNotReferenceRuntimeLoader()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "Shoots.Runtime.Loader");
    }

    [Fact]
    public void UiDoesNotReferenceSystemDiagnosticsProcess()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "System.Diagnostics.Process");
    }

    [Fact]
    public void EnvironmentNamespaceAvoidsRuntimeTypes()
    {
        var environmentTypes = typeof(EnvironmentProfileService).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Shoots.UI.Environment")
            .ToList();

        foreach (var type in environmentTypes)
        {
            var members = type.GetMembers();
            foreach (var member in members)
            {
                var memberType = member switch
                {
                    System.Reflection.PropertyInfo property => property.PropertyType,
                    System.Reflection.FieldInfo field => field.FieldType,
                    System.Reflection.MethodInfo method => method.ReturnType,
                    _ => null
                };

                if (memberType is null || memberType.Namespace is null)
                    continue;

                Assert.DoesNotContain("Shoots.Runtime", memberType.Namespace);
            }
        }
    }

    [Fact]
    public void EnvironmentNamespaceAvoidsExecutableDelegatesAndAsyncMethods()
    {
        var environmentTypes = typeof(EnvironmentProfileService).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Shoots.UI.Environment")
            .ToList();

        foreach (var type in environmentTypes)
        {
            var members = type.GetMembers();
            foreach (var member in members)
            {
                var memberType = member switch
                {
                    System.Reflection.PropertyInfo property => property.PropertyType,
                    System.Reflection.FieldInfo field => field.FieldType,
                    System.Reflection.MethodInfo method => method.ReturnType,
                    _ => null
                };

                if (memberType is null)
                    continue;

                Assert.False(typeof(Delegate).IsAssignableFrom(memberType), $"Delegate type found: {memberType.FullName}");
                Assert.False(IsAsyncReturnType(memberType), $"Async method return type found: {memberType.FullName}");
            }
        }
    }

    [Fact]
    public void ProjectsNamespaceAvoidsRuntimeTypes()
    {
        var projectTypes = typeof(ProjectWorkspaceProvider).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Shoots.UI.Projects")
            .ToList();

        foreach (var type in projectTypes)
        {
            var members = type.GetMembers();
            foreach (var member in members)
            {
                var memberType = member switch
                {
                    System.Reflection.PropertyInfo property => property.PropertyType,
                    System.Reflection.FieldInfo field => field.FieldType,
                    System.Reflection.MethodInfo method => method.ReturnType,
                    _ => null
                };

                if (memberType is null || memberType.Namespace is null)
                    continue;

                Assert.DoesNotContain("Shoots.Runtime", memberType.Namespace);
            }
        }
    }

    [Fact]
    public void EnvironmentScriptLoaderDoesNotCreateDirectories()
    {
        var loader = new EnvironmentScriptLoader();
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var result = loader.TryLoad(tempRoot, out _, out _);

        Assert.False(result);
        Assert.False(Directory.Exists(tempRoot));
    }

    [Fact]
    public void ProjectWorkspaceStorePersistsRecentWorkspaces()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new ProjectWorkspaceStore(tempRoot);
        var now = DateTimeOffset.UtcNow;
        var workspaces = new[]
        {
            new ProjectWorkspace("Alpha", "C:\\Alpha", now.AddHours(-2)),
            new ProjectWorkspace("Beta", "C:\\Beta", now)
        };

        store.SaveRecentWorkspaces(workspaces);
        var loaded = store.LoadRecentWorkspaces().ToList();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Beta", loaded[0].Name);
        Assert.Equal("Alpha", loaded[1].Name);
    }

    private static bool IsAsyncReturnType(Type type)
    {
        if (type == typeof(Task) || type == typeof(ValueTask))
            return true;

        if (!type.IsGenericType)
            return false;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Task<>) || definition == typeof(ValueTask<>);
    }
}
