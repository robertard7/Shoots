using System;
using System.IO;
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
            System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData),
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
        catch
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
                "custom",
                "User-provided RootFS",
                "config",
                null,
                null,
                "User-supplied license",
                "Provide a source override to select a distro archive or local path.")
        };

		return new ExecutionEnvironmentSettings(
			"custom",
			catalog,
			string.Empty);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
