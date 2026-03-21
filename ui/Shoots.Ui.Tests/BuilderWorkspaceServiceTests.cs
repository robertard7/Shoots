using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoots.UI.Blueprints;
using Shoots.UI.Builder;
using Shoots.UI.Environment;
using Shoots.UI.ExecutionEnvironments;
using Shoots.UI.Projects;
using Shoots.UI.Services;
using Shoots.UI.Services.Backends;
using Shoots.UI.Settings;
using Shoots.UI.ViewModels;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class BuilderWorkspaceServiceTests
{
    [Fact]
    public void Refresh_workspace_artifacts_creates_registry_context_and_capabilities_for_multiple_workspaces()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("provider-b");
        try
        {
            BuilderWorkspaceTestData.WriteFile(repoA, "src/Runtime/Program.cs", "namespace RuntimeA;\npublic static class Program { }\n");
            BuilderWorkspaceTestData.WriteFile(repoA, "src/Runtime/Runtime.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.0.0\" /></ItemGroup></Project>");
            BuilderWorkspaceTestData.WriteFile(repoB, "package.json", "{ \"devDependencies\": { \"jest\": \"^29.0.0\" } }");
            BuilderWorkspaceTestData.WriteFile(repoB, "src/index.ts", "export const value = 1;\n");

            var scanner = BuilderWorkspaceTestData.CreateScanner(
                repoA,
                new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc),
                new BuilderToolchainCapabilityObservation("msbuild", "build_tool", "msbuild", "17.0.0", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));
            scanner.AddObservations(
                repoB,
                new BuilderToolchainCapabilityObservation("node", "runtime", "node", "20.0.0", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc),
                new BuilderToolchainCapabilityObservation("npm", "packaging_tool", "npm", "10.0.0", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));

            var context = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
                new[]
                {
                    BuilderWorkspaceService.CreateDescriptor(repoA, "runtime-a"),
                    BuilderWorkspaceService.CreateDescriptor(repoB, "provider-b")
                },
                new BuilderWorkspaceResolutionRequest(ExplicitRepoRoot: repoB),
                scanner,
                BuilderWorkspaceTestData.ObservedUtc,
                forceCapabilityScan: true);

            Assert.NotNull(context);
            Assert.Equal(BuilderWorkspaceService.ResolveWorkspaceId(repoB), context!.Context.ActiveWorkspaceId);
            Assert.Equal(repoB, context.Context.RepoRoot);
            Assert.Equal("explicit_repo_root", context.Context.RoutingPolicySource);
            Assert.Equal(2, context.Registry.Entries.Count);

            var runtimeEntry = Assert.Single(context.Registry.Entries, entry => string.Equals(entry.RepoRootPath, repoA, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("csharp", runtimeEntry.DetectedLanguages);
            Assert.Contains("dotnet:probe_succeeded:8.0.100", runtimeEntry.ToolchainCapabilitySnapshot);

            var providerCapabilities = BuilderWorkspaceService.LoadCapabilities(repoB);
            Assert.NotNull(providerCapabilities);
            Assert.Contains("javascript", providerCapabilities!.LanguagesDetected);
            Assert.Contains("npm", providerCapabilities.BuildSystems);
            Assert.Contains("jest", providerCapabilities.TestFrameworks);

            Assert.True(File.Exists(BuilderWorkspaceService.RegistryPathForRepo(repoB)));
            Assert.True(File.Exists(BuilderWorkspaceService.ContextPathForRepo(repoB)));
            Assert.True(File.Exists(BuilderWorkspaceService.CapabilitiesPathForRepo(repoA)));
            Assert.True(File.Exists(BuilderWorkspaceService.CapabilitiesPathForRepo(repoB)));
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Builder_proof_roots_are_isolated_per_workspace()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("provider-b");
        try
        {
            var rootA = BuilderExecutionService.BuilderProofRootForRepo(repoA);
            var rootB = BuilderExecutionService.BuilderProofRootForRepo(repoB);

            Assert.NotEqual(rootA, rootB);
            Assert.Contains(Path.Combine(".codex", "workspaces"), rootA, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BuilderWorkspaceService.ResolveWorkspaceId(repoA), rootA, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BuilderWorkspaceService.ResolveWorkspaceId(repoB), rootB, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Workspace_resolution_prefers_explicit_selector_then_request_context_then_current_working_directory()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("provider-b");
        try
        {
            BuilderWorkspaceTestData.WriteFile(repoA, "src/Runtime/Program.cs", "namespace RuntimeA;\npublic static class Program { }\n");
            BuilderWorkspaceTestData.WriteFile(repoB, "tools/setup/package.json", "{ \"name\": \"provider-b\" }");
            var descriptors = new[]
            {
                BuilderWorkspaceService.CreateDescriptor(repoA, "runtime-a"),
                BuilderWorkspaceService.CreateDescriptor(repoB, "provider-b")
            };
            var scanner = BuilderWorkspaceTestData.CreateScanner(repoA);
            scanner.AddObservations(repoB);

            var explicitResult = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
                descriptors,
                new BuilderWorkspaceResolutionRequest(
                    ExplicitWorkspaceId: BuilderWorkspaceService.ResolveWorkspaceId(repoB),
                    ContextPath: Path.Combine(repoA, "src", "Runtime", "Program.cs"),
                    CurrentWorkingDirectory: Path.Combine(repoA, "src", "Runtime")),
                scanner,
                BuilderWorkspaceTestData.ObservedUtc,
                forceCapabilityScan: true);
            Assert.NotNull(explicitResult);
            Assert.Equal(repoB, explicitResult!.Context.RepoRoot);
            Assert.Equal("explicit_workspace_selector", explicitResult.Context.RoutingPolicySource);

            var requestContextResult = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
                descriptors,
                new BuilderWorkspaceResolutionRequest(
                    ContextPath: Path.Combine(repoA, "src", "Runtime", "Program.cs")),
                scanner,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1),
                forceCapabilityScan: true);
            Assert.NotNull(requestContextResult);
            Assert.Equal(repoA, requestContextResult!.Context.RepoRoot);
            Assert.Equal("request_context_path", requestContextResult.Context.RoutingPolicySource);

            var cwdResult = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
                descriptors,
                new BuilderWorkspaceResolutionRequest(
                    CurrentWorkingDirectory: Path.Combine(repoB, "tools", "setup")),
                scanner,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(2),
                forceCapabilityScan: true);
            Assert.NotNull(cwdResult);
            Assert.Equal(repoB, cwdResult!.Context.RepoRoot);
            Assert.Equal("current_working_directory", cwdResult.Context.RoutingPolicySource);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Route_resolution_artifact_is_written_per_workspace()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("provider-b");
        try
        {
            var scanner = BuilderWorkspaceTestData.CreateScanner(repoA);
            scanner.AddObservations(repoB);

            var contextA = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
                new[]
                {
                    BuilderWorkspaceService.CreateDescriptor(repoA, "runtime-a"),
                    BuilderWorkspaceService.CreateDescriptor(repoB, "provider-b")
                },
                new BuilderWorkspaceResolutionRequest(ExplicitRepoRoot: repoA),
                scanner,
                BuilderWorkspaceTestData.ObservedUtc,
                forceCapabilityScan: true);
            var contextB = BuilderWorkspaceService.RefreshWorkspaceArtifacts(
                new[]
                {
                    BuilderWorkspaceService.CreateDescriptor(repoA, "runtime-a"),
                    BuilderWorkspaceService.CreateDescriptor(repoB, "provider-b")
                },
                new BuilderWorkspaceResolutionRequest(ExplicitRepoRoot: repoB),
                scanner,
                BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1),
                forceCapabilityScan: true);

            Assert.NotNull(contextA);
            Assert.NotNull(contextB);

            var routeA = BuilderWorkspaceService.RecordRouteResolution(contextA!.Context, "request-a", "builder_proof_matrix", BuilderWorkspaceTestData.ObservedUtc);
            var routeB = BuilderWorkspaceService.RecordRouteResolution(contextB!.Context, "request-b", "builder_comparative_proof", BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));

            Assert.NotEqual(routeA.ArtifactPath, routeB.ArtifactPath);
            Assert.Equal("builder_proof_matrix", BuilderWorkspaceService.LoadRouteResolution(repoA)!.RouteDecision);
            Assert.Equal("builder_comparative_proof", BuilderWorkspaceService.LoadRouteResolution(repoB)!.RouteDecision);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public void Review_queues_remain_independent_across_workspaces()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("provider-b");
        try
        {
            var seededA = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoA, "session-a");
            var seededB = BuilderReviewWorkspaceTestData.SeedQueueArtifacts(repoB, "session-b");

            var contextA = BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoA,
                new BuilderReviewWorkspacePreferences("all", "directory", seededA.PendingFilePath),
                observedUtc: BuilderWorkspaceTestData.ObservedUtc);
            var contextB = BuilderReviewWorkspaceService.RefreshWorkspaceArtifacts(
                repoB,
                new BuilderReviewWorkspacePreferences("all", "directory", seededB.PendingFilePath),
                observedUtc: BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1));

            Assert.NotNull(contextA);
            Assert.NotNull(contextB);
            Assert.Equal("session-a", contextA!.Queue.ExecutionSessionId);
            Assert.Equal("session-b", contextB!.Queue.ExecutionSessionId);
            Assert.NotEqual(contextA.Queue.ArtifactPath, contextB.Queue.ArtifactPath);
            Assert.Contains(BuilderWorkspaceService.ResolveWorkspaceId(repoA), contextA.Queue.ArtifactPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BuilderWorkspaceService.ResolveWorkspaceId(repoB), contextB.Queue.ArtifactPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

public sealed class MainWindowViewModelBuilderWorkspaceTests
{
    [Fact]
    public async Task Builder_workspace_switching_refreshes_capabilities_and_review_state()
    {
        var repoA = BuilderWorkspaceTestData.CreateWorkspaceRoot("runtime-a");
        var repoB = BuilderWorkspaceTestData.CreateWorkspaceRoot("provider-b");
        try
        {
            var seededA = BuilderReviewWorkspaceTestData.SeedArtifacts(repoA, "session-runtime");
            var seededB = BuilderReviewWorkspaceTestData.SeedArtifacts(repoB, "session-provider");
            BuilderWorkspaceTestData.WriteFile(repoB, "package.json", "{ \"devDependencies\": { \"vitest\": \"^1.0.0\" } }");
            BuilderWorkspaceTestData.WriteFile(repoB, "src/index.ts", "export const provider = true;\n");

            var workspaceProvider = new MultiWorkspaceProvider(
                new ProjectWorkspace("runtime-a", repoA, BuilderWorkspaceTestData.ObservedUtc, ProjectId: "runtime-a"),
                new ProjectWorkspace("provider-b", repoB, BuilderWorkspaceTestData.ObservedUtc.AddMinutes(1), ProjectId: "provider-b"));
            var scanner = BuilderWorkspaceTestData.CreateScanner(
                repoA,
                new BuilderToolchainCapabilityObservation("dotnet", "sdk", "dotnet", "8.0.100", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));
            scanner.AddObservations(
                repoB,
                new BuilderToolchainCapabilityObservation("node", "runtime", "node", "20.0.0", true, true, "probe_succeeded", string.Empty, BuilderWorkspaceTestData.ObservedUtc));

            var viewModel = BuilderWorkspaceTestData.CreateViewModel(repoA, workspaceProvider, scanner);

            Assert.Equal(repoA, viewModel.BuilderWorkspaceRepoRoot);
            Assert.Contains("runtime-a", viewModel.BuilderWorkspaceBadge, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("csharp", viewModel.BuilderWorkspaceCapabilitySummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(seededA.PendingFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);

            viewModel.SelectedBuilderWorkspaceId = BuilderWorkspaceService.ResolveWorkspaceId(repoB);

            Assert.Equal(repoB, viewModel.BuilderWorkspaceRepoRoot);
            Assert.Contains("provider-b", viewModel.BuilderWorkspaceBadge, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("javascript", viewModel.BuilderWorkspaceCapabilitySummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(BuilderReviewWorkspaceService.ReviewWorkspacePathForRepo(repoB), viewModel.BuilderReviewWorkspaceArtifactPath);

            await viewModel.SelectFirstBuilderReviewPendingFileCommand.ExecuteAsync();
            Assert.Contains(seededB.PendingFilePath, viewModel.BuilderReviewCurrentFileHeader, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }
}

internal static class BuilderWorkspaceTestData
{
    public static readonly DateTimeOffset ObservedUtc = new(2026, 03, 14, 20, 00, 00, TimeSpan.Zero);

    public static string CreateWorkspaceRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"shoots-builder-workspace-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Shoots.sln"), "Microsoft Visual Studio Solution File");
        return root;
    }

    public static void WriteFile(string repoRoot, string relativePath, string contents)
    {
        var path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents.Replace("\n", System.Environment.NewLine, StringComparison.Ordinal));
    }

    public static DeterministicBuilderToolchainCapabilityScanner CreateScanner(
        string repoRoot,
        params BuilderToolchainCapabilityObservation[] observations)
    {
        var scanner = new DeterministicBuilderToolchainCapabilityScanner();
        scanner.AddObservations(repoRoot, observations);
        return scanner;
    }

    public static MainWindowViewModel CreateViewModel(
        string repoRoot,
        IProjectWorkspaceProvider workspaceProvider,
        IBuilderToolchainCapabilityScanner scanner)
    {
        var stateRoot = Path.Combine(repoRoot, ".test-state");
        Directory.CreateDirectory(stateRoot);

        return new MainWindowViewModel(
            new NullExecutionCommandService(),
            new TestEnvironmentProfileService(),
            new EnvironmentCapabilityProvider(),
            new EnvironmentProfilePrompt(),
            new EnvironmentScriptLoader(),
            workspaceProvider,
            new RecordingWorkspaceShellService(),
            new InMemoryDatabaseIntentStore(),
            new ToolTierPrompt(),
            new SystemBlueprintStore(Path.Combine(stateRoot, "blueprints")),
            new ExecutionEnvironmentSettingsStore(Path.Combine(stateRoot, "execution")),
            new AiPolicyStore(Path.Combine(stateRoot, "ai-policy")),
            new AiPanelVisibilityService(),
            new NullAiHelpFacade(),
            new TestBackendProbeService(),
            new TestOllamaClient(),
            validationSettingsStore: new ValidationSettingsStore(Path.Combine(stateRoot, "validation-settings")),
            validationRunnerService: new ValidationRunnerService(repoRoot),
            autoRefreshBackends: false,
            builderToolchainCapabilityScanner: scanner);
    }
}

internal sealed class DeterministicBuilderToolchainCapabilityScanner : IBuilderToolchainCapabilityScanner
{
    private readonly Dictionary<string, IReadOnlyList<BuilderToolchainCapabilityObservation>> _observationsByRoot = new(StringComparer.OrdinalIgnoreCase);

    public void AddObservations(string repoRoot, params BuilderToolchainCapabilityObservation[] observations)
        => _observationsByRoot[Path.GetFullPath(repoRoot)] = observations
            .OrderBy(observation => observation.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<BuilderToolchainCapabilityObservation> Scan(string repoRoot)
        => _observationsByRoot.TryGetValue(Path.GetFullPath(repoRoot), out var observations)
            ? observations
            : Array.Empty<BuilderToolchainCapabilityObservation>();
}

internal sealed class MultiWorkspaceProvider : IProjectWorkspaceProvider
{
    private readonly List<ProjectWorkspace> _workspaces;
    private ProjectWorkspace? _activeWorkspace;

    public MultiWorkspaceProvider(params ProjectWorkspace[] workspaces)
    {
        _workspaces = workspaces.ToList();
        _activeWorkspace = _workspaces.FirstOrDefault();
    }

    public IReadOnlyList<ProjectWorkspace> GetRecentWorkspaces() => _workspaces;

    public ProjectWorkspace? GetActiveWorkspace() => _activeWorkspace;

    public void SetActiveWorkspace(ProjectWorkspace workspace)
    {
        _activeWorkspace = workspace;
        if (!_workspaces.Any(existing => string.Equals(existing.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase)))
        {
            _workspaces.Add(workspace);
        }
    }

    public void RemoveWorkspace(ProjectWorkspace workspace)
    {
        _workspaces.RemoveAll(existing => string.Equals(existing.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase));
        if (_activeWorkspace is not null && string.Equals(_activeWorkspace.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            _activeWorkspace = _workspaces.FirstOrDefault();
        }
    }

    public void UpdateWorkspace(ProjectWorkspace workspace)
    {
        var index = _workspaces.FindIndex(existing => string.Equals(existing.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _workspaces[index] = workspace;
        }
    }
}
