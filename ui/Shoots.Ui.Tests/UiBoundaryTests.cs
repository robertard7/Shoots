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
    public void UiDoesNotReferenceGitHubSdk()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            reference.Name is not null &&
            (reference.Name.Contains("Octokit", StringComparison.OrdinalIgnoreCase) ||
             reference.Name.Contains("GitHub", StringComparison.OrdinalIgnoreCase)));
    }

    // Language hygiene guard: keep documentation descriptive and avoid implying authority over external tools.
    [Fact]
    public void ReadmeLanguageIsDescriptive()
    {
        var readme = LoadReadme();
        var sanitized = readme.Replace(
            "This repository does not enforce behavior or validate compliance.",
            string.Empty,
            StringComparison.OrdinalIgnoreCase);
        var forbiddenMarkers = new[]
        {
            "OpenAI",
            "Anthropic",
            "Codex",
            "GPT",
            "GitHub",
            "Octokit",
            "invalid output",
            "must comply",
            "must",
            "shall",
            "require",
            "required",
            "enforce",
            "enforcement",
            "policy",
            "compliance",
            "invalid",
            "invalidate",
            "validated by",
            "guarantee",
            "guarantees",
            "certify",
            "certified",
            "approved",
            "forbidden",
            "prohibited",
            "mandate",
            "violation",
            "exclusive",
            "exclusively",
            "warranty",
            "warrant",
            "liable",
            "liability",
            "audit",
            "attest",
            "attestation",
            "obligation"
        };

        foreach (var marker in forbiddenMarkers)
            Assert.False(
                sanitized.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"README contains \"{marker}\", which can imply authority over external tools. Use descriptive, non-enforcing language.");
    }

    // Architecture guard: AI help surface remains descriptive and read-only.
    [Fact]
    public void AiHelpFacadeUsesDescriptiveMethodsOnly()
    {
        var methods = typeof(Shoots.Runtime.Ui.Abstractions.IAiHelpFacade)
            .GetMethods();

        foreach (var method in methods)
        {
            Assert.Equal(typeof(Task<string>), method.ReturnType);
            var name = method.Name;
            Assert.DoesNotContain("Validate", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Enforce", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Execute", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Approve", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Decide", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Architecture guard: AI help receives read-only snapshots only.
    [Fact]
    public void AiHelpRequestDoesNotExposeExecutionServices()
    {
        var properties = typeof(Shoots.Runtime.Ui.Abstractions.AiHelpRequest)
            .GetProperties();

        foreach (var property in properties)
        {
            var typeName = property.PropertyType.FullName ?? property.PropertyType.Name;
            Assert.DoesNotContain("IRuntimeFacade", typeName, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecutionCommand", typeName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AiHelpFacadeDoesNotAcceptExecutionServices()
    {
        var methods = typeof(Shoots.Runtime.Ui.Abstractions.IAiHelpFacade)
            .GetMethods();

        foreach (var method in methods)
        {
            foreach (var parameter in method.GetParameters())
            {
                var type = parameter.ParameterType;
                var typeName = type.FullName ?? type.Name;
                Assert.DoesNotContain("IRuntimeFacade", typeName, StringComparison.Ordinal);
                Assert.DoesNotContain("ExecutionCommand", typeName, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void UiReferencesRuntimeFacadeOnly()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("Shoots.Runtime", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain(references, name => name != "Shoots.Runtime.Ui.Abstractions");
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

    // Language hygiene guard: UI strings stay descriptive.
    [Fact]
    public void UiStringsAreDescriptive()
    {
        var content = LoadMainWindowXaml();
        var forbiddenMarkers = new[]
        {
            "must",
            "shall",
            "require",
            "required",
            "enforce",
            "enforcement",
            "policy",
            "compliance",
            "forbidden",
            "prohibited",
            "mandate",
            "violation",
            "invalidate",
            "invalid"
        };

        foreach (var marker in forbiddenMarkers)
            Assert.False(
                content.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"MainWindow.xaml contains \"{marker}\", which can imply authority. Use descriptive language.");
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

    private static string LoadReadme()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "README.md");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("README.md not found.");
    }

    private static string LoadMainWindowXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "ui", "Shoots.Ui", "MainWindow.xaml");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("MainWindow.xaml not found.");
    }
}
