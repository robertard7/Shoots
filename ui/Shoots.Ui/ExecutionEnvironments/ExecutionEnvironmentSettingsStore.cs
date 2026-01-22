using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.ExecutionEnvironments;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IExecutionEnvironmentSettingsStore
{
    ExecutionEnvironmentSettings Load();

    void Save(ExecutionEnvironmentSettings settings);
}

public sealed class ExecutionEnvironmentSettingsStore : IExecutionEnvironmentSettingsStore
{
    public const string FileName = "execution-environments.json";

    private readonly string _storePath;

    public ExecutionEnvironmentSettingsStore(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shoots");
        _storePath = Path.Combine(root, FileName);
    }

    public ExecutionEnvironmentSettings Load()
    {
        if (!File.Exists(_storePath))
            return CreateDefault();

        try
        {
            var json = File.ReadAllText(_storePath);
            var settings = JsonSerializer.Deserialize<ExecutionEnvironmentSettings>(json, JsonOptions());
            return settings ?? CreateDefault();
        }
        catch (Exception)
        {
            return CreateDefault();
        }
    }

    public void Save(ExecutionEnvironmentSettings settings)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, JsonOptions());
        File.WriteAllText(_storePath, json);
    }

    private static ExecutionEnvironmentSettings CreateDefault()
    {
        var catalog = new[]
        {
            new RootFsDescriptor(
                "alpine",
                "Alpine Linux",
                "http",
                "https://dl-cdn.alpinelinux.org/alpine/v3.19/releases/x86_64/alpine-minirootfs-3.19.0-x86_64.tar.gz",
                "sha256:TODO",
                "GPLv2 + others",
                "Official Alpine minirootfs")
        };

        return new ExecutionEnvironmentSettings("alpine", catalog);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
