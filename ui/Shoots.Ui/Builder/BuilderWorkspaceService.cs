using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.UI.Builder;

public sealed record BuilderWorkspaceDescriptor(
    string WorkspaceId,
    string RepoRootPath,
    string RepoName);

public sealed record BuilderWorkspaceRegistryEntryRecord(
    string WorkspaceId,
    string RepoRootPath,
    string RepoName,
    IReadOnlyList<string> DetectedLanguages,
    IReadOnlyList<string> ToolchainCapabilitySnapshot,
    DateTimeOffset LastCapabilityScan,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWorkspaceRegistryRecord(
    string ActiveWorkspaceId,
    IReadOnlyList<BuilderWorkspaceRegistryEntryRecord> Entries,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWorkspaceContextRecord(
    string ActiveWorkspaceId,
    string RepoRoot,
    string RepoName,
    string RoutingPolicySource,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWorkspaceCapabilitiesRecord(
    string WorkspaceId,
    IReadOnlyList<string> LanguagesDetected,
    IReadOnlyList<string> CompilersAvailable,
    IReadOnlyList<string> BuildSystems,
    IReadOnlyList<string> TestFrameworks,
    IReadOnlyList<string> ToolchainCapabilitySnapshot,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWorkspaceRouteResolutionRecord(
    string RequestId,
    string WorkspaceId,
    string ResolvedRepo,
    string RouteDecision,
    string Summary,
    string ArtifactPath,
    DateTimeOffset ObservedUtc);

public sealed record BuilderWorkspaceResolutionRequest(
    string ExplicitWorkspaceId = "",
    string ExplicitRepoRoot = "",
    string ContextPath = "",
    string CurrentWorkingDirectory = "",
    string RoutingPolicySource = "workspace_selector");

public sealed record BuilderWorkspaceSurfaceContext(
    BuilderWorkspaceRegistryRecord Registry,
    BuilderWorkspaceContextRecord Context,
    BuilderWorkspaceCapabilitiesRecord Capabilities,
    BuilderWorkspaceRouteResolutionRecord? RouteResolution);

public static class BuilderWorkspaceService
{
    public const string WorkspaceRegistryFileName = "builder_workspace_registry.json";
    public const string WorkspaceContextFileName = "builder_workspace_context.json";
    public const string WorkspaceCapabilitiesFileName = "builder_workspace_capabilities.json";
    public const string WorkspaceRouteResolutionFileName = "builder_workspace_route_resolution.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] WorkspaceRootMarkers =
    {
        "package.json",
        "pyproject.toml",
        "requirements.txt",
        "go.mod",
        "Cargo.toml",
        "pom.xml",
        "build.gradle",
        "build.gradle.kts",
        "CMakeLists.txt",
        "Shoots.sln"
    };

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".codex",
        "bin",
        "obj",
        "node_modules",
        ".vs",
        ".idea",
        ".vscode"
    };

    public static string WorkspacesRootForRepo(string repoRoot)
        => Path.Combine(NormalizeRepoRoot(repoRoot), ".codex", "workspaces");

    public static string WorkspaceRootForRepo(string repoRoot)
        => Path.Combine(WorkspacesRootForRepo(repoRoot), ResolveWorkspaceId(repoRoot));

    public static string BuilderProofRootForRepo(string repoRoot)
        => Path.Combine(WorkspaceRootForRepo(repoRoot), "builder-proof");

    public static string RegistryPathForRepo(string repoRoot)
        => Path.Combine(WorkspacesRootForRepo(repoRoot), WorkspaceRegistryFileName);

    public static string ContextPathForRepo(string repoRoot)
        => Path.Combine(WorkspaceRootForRepo(repoRoot), WorkspaceContextFileName);

    public static string CapabilitiesPathForRepo(string repoRoot)
        => Path.Combine(WorkspaceRootForRepo(repoRoot), WorkspaceCapabilitiesFileName);

    public static string RouteResolutionPathForRepo(string repoRoot)
        => Path.Combine(WorkspaceRootForRepo(repoRoot), WorkspaceRouteResolutionFileName);

    public static BuilderWorkspaceDescriptor CreateDescriptor(string repoRoot, string? repoName = null)
    {
        var normalizedRoot = NormalizeRepoRoot(repoRoot);
        var resolvedName = string.IsNullOrWhiteSpace(repoName)
            ? new DirectoryInfo(normalizedRoot).Name
            : repoName.Trim();
        return new BuilderWorkspaceDescriptor(
            ResolveWorkspaceId(normalizedRoot),
            normalizedRoot,
            string.IsNullOrWhiteSpace(resolvedName) ? "workspace" : resolvedName);
    }

    public static string ResolveWorkspaceId(string repoRoot)
    {
        var normalizedRoot = NormalizeRepoRoot(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoName = new DirectoryInfo(normalizedRoot).Name;
        if (string.IsNullOrWhiteSpace(repoName))
        {
            repoName = "workspace";
        }

        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedRoot.ToLowerInvariant()));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"{SanitizeWorkspaceSegment(repoName)}-{hash[..8]}";
    }

    public static bool IsWorkspaceRootEligible(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return false;
        }

        if (Directory.Exists(Path.Combine(repoRoot, ".git")) ||
            Directory.Exists(Path.Combine(repoRoot, ".codex", "workspaces")))
        {
            return true;
        }

        if (WorkspaceRootMarkers.Any(marker => File.Exists(Path.Combine(repoRoot, marker))))
        {
            return true;
        }

        if (Directory.EnumerateFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.TopDirectoryOnly).Any())
        {
            return true;
        }

        return false;
    }

    public static BuilderWorkspaceSurfaceContext? RefreshWorkspaceArtifacts(
        IEnumerable<BuilderWorkspaceDescriptor> workspaces,
        BuilderWorkspaceResolutionRequest? request = null,
        IBuilderToolchainCapabilityScanner? scanner = null,
        DateTimeOffset? observedUtc = null,
        bool forceCapabilityScan = false)
    {
        if (workspaces is null)
        {
            throw new ArgumentNullException(nameof(workspaces));
        }

        var descriptors = workspaces
            .Where(descriptor => descriptor is not null && !string.IsNullOrWhiteSpace(descriptor.RepoRootPath))
            .Select(descriptor => CreateDescriptor(descriptor.RepoRootPath, descriptor.RepoName))
            .Where(descriptor => Directory.Exists(descriptor.RepoRootPath))
            .GroupBy(descriptor => descriptor.RepoRootPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(descriptor => descriptor.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.RepoRootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (descriptors.Length == 0)
        {
            return null;
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var resolution = ResolveActiveWorkspace(descriptors, request);
        var activeDescriptor = resolution.Workspace;

        var capabilitiesByWorkspaceId = new Dictionary<string, BuilderWorkspaceCapabilitiesRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            var capabilities = forceCapabilityScan
                ? BuildCapabilities(descriptor, scanner, effectiveObservedUtc)
                : LoadCapabilities(descriptor.RepoRootPath) ?? BuildCapabilities(descriptor, scanner, effectiveObservedUtc);

            capabilities = capabilities with
            {
                WorkspaceId = descriptor.WorkspaceId,
                ArtifactPath = CapabilitiesPathForRepo(descriptor.RepoRootPath),
                ObservedUtc = effectiveObservedUtc
            };

            Directory.CreateDirectory(WorkspaceRootForRepo(descriptor.RepoRootPath));
            Save(capabilities.ArtifactPath, capabilities);
            capabilitiesByWorkspaceId[descriptor.WorkspaceId] = capabilities;
        }

        var registry = BuildRegistry(activeDescriptor, descriptors, capabilitiesByWorkspaceId, effectiveObservedUtc);
        var context = BuildContext(activeDescriptor, resolution.Source, effectiveObservedUtc);
        Directory.CreateDirectory(WorkspacesRootForRepo(activeDescriptor.RepoRootPath));
        Save(registry.ArtifactPath, registry);
        Save(context.ArtifactPath, context);

        return new BuilderWorkspaceSurfaceContext(
            registry,
            context,
            capabilitiesByWorkspaceId[activeDescriptor.WorkspaceId],
            LoadRouteResolution(activeDescriptor.RepoRootPath));
    }

    public static BuilderWorkspaceCapabilitiesRecord? LoadCapabilities(string repoRoot)
        => Load<BuilderWorkspaceCapabilitiesRecord>(CapabilitiesPathForRepo(repoRoot));

    public static BuilderWorkspaceRegistryRecord? LoadRegistry(string repoRoot)
        => Load<BuilderWorkspaceRegistryRecord>(RegistryPathForRepo(repoRoot));

    public static BuilderWorkspaceContextRecord? LoadContext(string repoRoot)
        => Load<BuilderWorkspaceContextRecord>(ContextPathForRepo(repoRoot));

    public static BuilderWorkspaceRouteResolutionRecord? LoadRouteResolution(string repoRoot)
        => Load<BuilderWorkspaceRouteResolutionRecord>(RouteResolutionPathForRepo(repoRoot));

    public static BuilderWorkspaceRouteResolutionRecord RecordRouteResolution(
        BuilderWorkspaceContextRecord context,
        string requestId,
        string routeDecision,
        DateTimeOffset? observedUtc = null)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var effectiveObservedUtc = observedUtc ?? DateTimeOffset.UtcNow;
        var resolvedRepo = NormalizeRepoRoot(context.RepoRoot);
        var artifactPath = RouteResolutionPathForRepo(resolvedRepo);
        var record = new BuilderWorkspaceRouteResolutionRecord(
            FirstNonEmpty(requestId, "builder_request"),
            context.ActiveWorkspaceId,
            resolvedRepo,
            FirstNonEmpty(routeDecision, "not_recorded"),
            $"Route {FirstNonEmpty(routeDecision, "not_recorded")} resolved for {context.RepoName} ({context.ActiveWorkspaceId}).",
            artifactPath,
            effectiveObservedUtc);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        Save(artifactPath, record);
        return record;
    }

    private static BuilderWorkspaceRegistryRecord BuildRegistry(
        BuilderWorkspaceDescriptor activeDescriptor,
        IReadOnlyList<BuilderWorkspaceDescriptor> descriptors,
        IReadOnlyDictionary<string, BuilderWorkspaceCapabilitiesRecord> capabilitiesByWorkspaceId,
        DateTimeOffset observedUtc)
    {
        var entries = descriptors
            .Select(descriptor =>
            {
                var capabilities = capabilitiesByWorkspaceId[descriptor.WorkspaceId];
                return new BuilderWorkspaceRegistryEntryRecord(
                    descriptor.WorkspaceId,
                    descriptor.RepoRootPath,
                    descriptor.RepoName,
                    capabilities.LanguagesDetected,
                    capabilities.ToolchainCapabilitySnapshot,
                    capabilities.ObservedUtc,
                    observedUtc);
            })
            .OrderBy(entry => entry.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RepoRootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderWorkspaceRegistryRecord(
            activeDescriptor.WorkspaceId,
            entries,
            $"Registered {entries.Length} builder workspace(s). Active workspace: {activeDescriptor.RepoName} ({activeDescriptor.WorkspaceId}).",
            RegistryPathForRepo(activeDescriptor.RepoRootPath),
            observedUtc);
    }

    private static BuilderWorkspaceContextRecord BuildContext(
        BuilderWorkspaceDescriptor activeDescriptor,
        string resolutionSource,
        DateTimeOffset observedUtc)
        => new(
            activeDescriptor.WorkspaceId,
            activeDescriptor.RepoRootPath,
            activeDescriptor.RepoName,
            resolutionSource,
            $"Resolved builder workspace {activeDescriptor.RepoName} ({activeDescriptor.WorkspaceId}) via {resolutionSource}.",
            ContextPathForRepo(activeDescriptor.RepoRootPath),
            observedUtc);

    private static BuilderWorkspaceCapabilitiesRecord BuildCapabilities(
        BuilderWorkspaceDescriptor descriptor,
        IBuilderToolchainCapabilityScanner? scanner,
        DateTimeOffset observedUtc)
    {
        var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var buildSystems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var testFrameworks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateWorkspaceFiles(descriptor.RepoRootPath))
        {
            var fileName = Path.GetFileName(file);
            var extension = Path.GetExtension(file).ToLowerInvariant();
            switch (extension)
            {
                case ".cs":
                    languages.Add("csharp");
                    break;
                case ".xaml":
                    languages.Add("xaml");
                    break;
                case ".ts":
                case ".tsx":
                    languages.Add("typescript");
                    break;
                case ".js":
                case ".jsx":
                    languages.Add("javascript");
                    break;
                case ".py":
                    languages.Add("python");
                    break;
                case ".go":
                    languages.Add("go");
                    if (fileName.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase))
                    {
                        testFrameworks.Add("go_test");
                    }
                    break;
                case ".rs":
                    languages.Add("rust");
                    break;
                case ".java":
                    languages.Add("java");
                    break;
            }

            switch (fileName.ToLowerInvariant())
            {
                case "package.json":
                    languages.Add("javascript");
                    InspectPackageJson(file, buildSystems, testFrameworks);
                    break;
                case "package-lock.json":
                    buildSystems.Add("npm");
                    break;
                case "pnpm-lock.yaml":
                    buildSystems.Add("pnpm");
                    break;
                case "yarn.lock":
                    buildSystems.Add("yarn");
                    break;
                case "tsconfig.json":
                    languages.Add("typescript");
                    break;
                case "pyproject.toml":
                    languages.Add("python");
                    buildSystems.Add("pyproject");
                    InspectTextFile(file, text =>
                    {
                        if (ContainsAny(text, "pytest"))
                        {
                            testFrameworks.Add("pytest");
                        }
                    });
                    break;
                case "requirements.txt":
                    languages.Add("python");
                    buildSystems.Add("pip");
                    InspectTextFile(file, text =>
                    {
                        if (ContainsAny(text, "pytest"))
                        {
                            testFrameworks.Add("pytest");
                        }
                    });
                    break;
                case "go.mod":
                    languages.Add("go");
                    buildSystems.Add("go");
                    testFrameworks.Add("go_test");
                    break;
                case "cargo.toml":
                    languages.Add("rust");
                    buildSystems.Add("cargo");
                    testFrameworks.Add("cargo_test");
                    break;
                case "pom.xml":
                    languages.Add("java");
                    buildSystems.Add("maven");
                    break;
                case "build.gradle":
                case "build.gradle.kts":
                    languages.Add("java");
                    buildSystems.Add("gradle");
                    break;
                case "cmakelists.txt":
                    buildSystems.Add("cmake");
                    break;
            }

            if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                languages.Add("csharp");
                buildSystems.Add("msbuild");
                InspectTextFile(file, text =>
                {
                    if (ContainsAny(text, "xunit"))
                    {
                        testFrameworks.Add("xunit");
                    }

                    if (ContainsAny(text, "nunit"))
                    {
                        testFrameworks.Add("nunit");
                    }

                    if (ContainsAny(text, "mstest", "microsoft.net.test.sdk"))
                    {
                        testFrameworks.Add("mstest");
                    }
                });
            }

            if (fileName.Contains("test", StringComparison.OrdinalIgnoreCase) &&
                extension is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py")
            {
                testFrameworks.Add(extension switch
                {
                    ".cs" => "xunit",
                    ".ts" or ".tsx" or ".js" or ".jsx" => "jest",
                    ".py" => "pytest",
                    _ => "tests"
                });
            }
        }

        var observations = Array.Empty<BuilderToolchainCapabilityObservation>();
        if (scanner is not null)
        {
            try
            {
                observations = scanner.Scan(descriptor.RepoRootPath)
                    .OrderBy(observation => observation.ToolId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                observations = Array.Empty<BuilderToolchainCapabilityObservation>();
            }
        }

        var compilers = observations
            .Where(observation => observation.Callable &&
                                  (string.Equals(observation.ToolCategory, "compiler", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(observation.ToolCategory, "sdk", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(observation.ToolCategory, "runtime", StringComparison.OrdinalIgnoreCase)))
            .Select(observation => observation.ToolId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(toolId => toolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var toolchainSnapshot = observations
            .Select(observation => $"{observation.ToolId}:{observation.ProbeState}:{FirstNonEmpty(observation.Version, observation.Installed ? "installed" : "missing")}")
            .ToArray();

        return new BuilderWorkspaceCapabilitiesRecord(
            descriptor.WorkspaceId,
            languages.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            compilers,
            buildSystems.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            testFrameworks.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            toolchainSnapshot,
            BuildCapabilitiesSummary(descriptor, languages, compilers, buildSystems, testFrameworks),
            CapabilitiesPathForRepo(descriptor.RepoRootPath),
            observedUtc);
    }

    private static (BuilderWorkspaceDescriptor Workspace, string Source) ResolveActiveWorkspace(
        IReadOnlyList<BuilderWorkspaceDescriptor> descriptors,
        BuilderWorkspaceResolutionRequest? request)
    {
        var explicitWorkspaceId = request?.ExplicitWorkspaceId?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitWorkspaceId))
        {
            var matched = descriptors.FirstOrDefault(descriptor =>
                string.Equals(descriptor.WorkspaceId, explicitWorkspaceId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return (matched, "explicit_workspace_selector");
            }
        }

        var explicitRepoRoot = NormalizePathOrEmpty(request?.ExplicitRepoRoot);
        if (!string.IsNullOrWhiteSpace(explicitRepoRoot))
        {
            var matched = descriptors.FirstOrDefault(descriptor =>
                string.Equals(descriptor.RepoRootPath, explicitRepoRoot, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return (matched, "explicit_repo_root");
            }
        }

        var contextPath = NormalizePathOrEmpty(request?.ContextPath);
        var contextual = ResolveContainingWorkspace(descriptors, contextPath);
        if (contextual is not null)
        {
            return (contextual, "request_context_path");
        }

        var currentWorkingDirectory = NormalizePathOrEmpty(request?.CurrentWorkingDirectory);
        var cwdMatch = ResolveContainingWorkspace(descriptors, currentWorkingDirectory);
        if (cwdMatch is not null)
        {
            return (cwdMatch, "current_working_directory");
        }

        return (descriptors[0], "workspace_registry_fallback");
    }

    private static BuilderWorkspaceDescriptor? ResolveContainingWorkspace(
        IEnumerable<BuilderWorkspaceDescriptor> descriptors,
        string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        return descriptors
            .Where(descriptor => PathStartsWith(candidatePath, descriptor.RepoRootPath))
            .OrderByDescending(descriptor => descriptor.RepoRootPath.Length)
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string repoRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repoRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current)
                    .Where(path => !SkippedDirectoryNames.Contains(Path.GetFileName(path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories.Reverse())
            {
                pending.Push(directory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static void InspectPackageJson(string path, ISet<string> buildSystems, ISet<string> testFrameworks)
        => InspectTextFile(path, text =>
        {
            buildSystems.Add("npm");
            if (ContainsAny(text, "\"pnpm\"", "\"pnpm-lock.yaml\""))
            {
                buildSystems.Add("pnpm");
            }

            if (ContainsAny(text, "\"yarn\"", "\"yarn.lock\""))
            {
                buildSystems.Add("yarn");
            }

            if (ContainsAny(text, "\"jest\"", "\"@jest"))
            {
                testFrameworks.Add("jest");
            }

            if (ContainsAny(text, "\"vitest\""))
            {
                testFrameworks.Add("vitest");
            }
        });

    private static void InspectTextFile(string path, Action<string> inspector)
    {
        try
        {
            inspector(File.ReadAllText(path));
        }
        catch
        {
            // Ignore unreadable marker files and continue with partial repo knowledge.
        }
    }

    private static string BuildCapabilitiesSummary(
        BuilderWorkspaceDescriptor descriptor,
        IEnumerable<string> languages,
        IEnumerable<string> compilers,
        IEnumerable<string> buildSystems,
        IEnumerable<string> testFrameworks)
        => $"Workspace {descriptor.RepoName}: languages={FormatList(languages)}; compilers={FormatList(compilers)}; build systems={FormatList(buildSystems)}; tests={FormatList(testFrameworks)}.";

    private static string FormatList(IEnumerable<string> values)
    {
        var array = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return array.Length == 0 ? "none" : string.Join(", ", array);
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeRepoRoot(string repoRoot)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(repoRoot) ? "." : repoRoot);

    private static string NormalizePathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool PathStartsWith(string path, string root)
    {
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeWorkspaceSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }

    private static string FirstNonEmpty(string? primary, string fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();

    private static T? Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            lock (GetSaveLock(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream);
            }
        }
        catch
        {
            return default;
        }
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (GetSaveLock(path))
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value, SerializerOptions);
        }
    }

    private static object GetSaveLock(string path)
        => SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());
}
