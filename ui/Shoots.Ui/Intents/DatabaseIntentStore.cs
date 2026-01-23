using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoots.UI.Intents;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed class DatabaseIntentStore : IDatabaseIntentStore
{
    public const string FileName = "workspace-intents.json";

    private readonly string _storePath;

    public DatabaseIntentStore(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Shoots");

        _storePath = Path.Combine(root, FileName);
    }

    public DatabaseIntent GetIntent(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return DatabaseIntent.Undecided;

        var all = LoadAll();
        return all.TryGetValue(workspaceRoot, out var intent)
            ? intent
            : DatabaseIntent.Undecided;
    }

    public void SetIntent(string workspaceRoot, DatabaseIntent intent)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        var intents = LoadAll()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        intents[workspaceRoot] = intent;
        SaveAll(intents);
    }

    public IReadOnlyDictionary<string, DatabaseIntent> LoadAll()
    {
        if (!File.Exists(_storePath))
            return new Dictionary<string, DatabaseIntent>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_storePath);
            var payload = JsonSerializer.Deserialize<Dictionary<string, DatabaseIntent>>(json, JsonOptions())
                ?? new Dictionary<string, DatabaseIntent>(StringComparer.OrdinalIgnoreCase);

            return new Dictionary<string, DatabaseIntent>(payload, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, DatabaseIntent>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveAll(IDictionary<string, DatabaseIntent> intents)
    {
        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(intents, JsonOptions());
        File.WriteAllText(_storePath, json);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
}
