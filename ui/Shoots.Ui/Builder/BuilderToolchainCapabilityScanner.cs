using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Shoots.UI.Services;

namespace Shoots.UI.Builder;

public interface IBuilderToolchainCapabilityScanner
{
    IReadOnlyList<BuilderToolchainCapabilityObservation> Scan(string repoRoot);
}

public sealed record BuilderToolchainCapabilityObservation(
    string ToolId,
    string ToolCategory,
    string DiscoveredPath,
    string Version,
    bool Installed,
    bool Callable,
    string ProbeState,
    string ProbeMessage,
    DateTimeOffset ObservedUtc);

public sealed class DefaultBuilderToolchainCapabilityScanner : IBuilderToolchainCapabilityScanner
{
    private readonly IValidationCommandExecutor _executor;

    private static readonly ToolProbeDefinition[] ProbeDefinitions =
    {
        new("dotnet", "sdk", "dotnet", new[] { "--version" }),
        new("msbuild", "build_tool", "msbuild", new[] { "-version" }),
        new("cl", "compiler", "cl", new[] { "/Bv" }),
        new("gcc", "compiler", "gcc", new[] { "--version" }),
        new("g++", "compiler", "g++", new[] { "--version" }),
        new("clang", "compiler", "clang", new[] { "--version" }),
        new("cmake", "build_tool", "cmake", new[] { "--version" }),
        new("ninja", "build_tool", "ninja", new[] { "--version" }),
        new("node", "runtime", "node", new[] { "--version" }),
        new("npm", "packaging_tool", "npm", new[] { "--version" }),
        new("pnpm", "packaging_tool", "pnpm", new[] { "--version" }),
        new("yarn", "packaging_tool", "yarn", new[] { "--version" }),
        new("python", "runtime", "python", new[] { "--version" }),
        new("java", "runtime", "java", new[] { "-version" })
    };

    public DefaultBuilderToolchainCapabilityScanner(IValidationCommandExecutor? executor = null)
    {
        _executor = executor ?? new ValidationCommandExecutor();
    }

    public IReadOnlyList<BuilderToolchainCapabilityObservation> Scan(string repoRoot)
        => ProbeDefinitions
            .Select(definition => Probe(repoRoot, definition))
            .OrderBy(entry => entry.ToolId, StringComparer.Ordinal)
            .ToArray();

    private BuilderToolchainCapabilityObservation Probe(string repoRoot, ToolProbeDefinition definition)
    {
        var observedUtc = DateTimeOffset.UtcNow;
        var discoveredPath = ResolveOnPath(definition.CommandName);
        if (string.IsNullOrWhiteSpace(discoveredPath))
        {
            return new BuilderToolchainCapabilityObservation(
                definition.ToolId,
                definition.ToolCategory,
                string.Empty,
                string.Empty,
                false,
                false,
                "not_found",
                $"{definition.CommandName} is not installed or not discoverable on PATH.",
                observedUtc);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new BuilderToolchainCapabilityObservation(
                definition.ToolId,
                definition.ToolCategory,
                discoveredPath,
                string.Empty,
                true,
                false,
                "probe_failed",
                "Safe toolchain probing currently requires Windows shell execution.",
                observedUtc);
        }

        try
        {
            var probeDirectory = ResolveProbeDirectory(repoRoot);
            Directory.CreateDirectory(probeDirectory);

            var logPath = Path.Combine(probeDirectory, $"{definition.ToolId}.log");
            DeleteIfExists(logPath);
            DeleteIfExists(Path.Combine(probeDirectory, $"{definition.ToolId}.cmd"));
            DeleteIfExists(Path.Combine(probeDirectory, $"{definition.ToolId}.exitcode"));

            var command = new ValidationCommandSpec(
                $"toolchain_probe_{definition.ToolId}",
                $"{definition.ToolId} capability probe",
                discoveredPath,
                definition.Arguments,
                Path.GetFileName(logPath),
                Array.Empty<string>(),
                new[] { "parallel_safe" },
                CanRunIndependently: true,
                TouchesBuildOutputs: false,
                ClearsCaches: false,
                RewritesArtifacts: false,
                ReadsOnly: true,
                SupportsIsolatedWorkspace: false,
                IsolationSupportStatus: "not_required",
                IsolationSupportReason: "Safe capability probes are read-only shell checks.");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = _executor.ExecuteAsync(
                    command,
                    Directory.Exists(repoRoot) ? repoRoot : probeDirectory,
                    logPath,
                    _ => { },
                    timeoutCts.Token)
                .GetAwaiter()
                .GetResult();
            var combinedOutput = string.Join(System.Environment.NewLine, execution.OutputLines).Trim();
            var version = ExtractVersion(combinedOutput);
            var callable = execution.ExitCode == 0;

            return new BuilderToolchainCapabilityObservation(
                definition.ToolId,
                definition.ToolCategory,
                discoveredPath,
                version,
                true,
                callable,
                callable ? "probe_succeeded" : "probe_failed",
                callable
                    ? string.Empty
                    : $"{definition.CommandName} probe exited with code {execution.ExitCode}.",
                observedUtc);
        }
        catch (OperationCanceledException)
        {
            return new BuilderToolchainCapabilityObservation(
                definition.ToolId,
                definition.ToolCategory,
                discoveredPath,
                string.Empty,
                true,
                false,
                "probe_failed",
                $"{definition.CommandName} timed out during the safe capability probe.",
                observedUtc);
        }
        catch (Exception ex)
        {
            return new BuilderToolchainCapabilityObservation(
                definition.ToolId,
                definition.ToolCategory,
                discoveredPath,
                string.Empty,
                true,
                false,
                "probe_failed",
                ex.Message,
                observedUtc);
        }
    }

    private static string ResolveProbeDirectory(string repoRoot)
        => string.IsNullOrWhiteSpace(repoRoot)
            ? Path.Combine(Path.GetTempPath(), "Shoots", "builder-toolchain-probes")
            : Path.Combine(BuilderExecutionService.BuilderProofRootForRepo(repoRoot), "toolchain-probes");

    private static string ResolveOnPath(string commandName)
    {
        if (Path.IsPathRooted(commandName) && File.Exists(commandName))
        {
            return commandName;
        }

        var searchPaths = (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasExtension = Path.HasExtension(commandName);
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { string.Empty };

        foreach (var directory in searchPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (hasExtension)
            {
                var candidate = Path.Combine(directory, commandName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(
                    directory,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? $"{commandName}{extension}"
                        : commandName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var match = Regex.Match(output, @"\d+\.\d+(?:\.\d+){0,3}");
        return match.Success ? match.Value : output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record ToolProbeDefinition(
        string ToolId,
        string ToolCategory,
        string CommandName,
        IReadOnlyList<string> Arguments);
}
