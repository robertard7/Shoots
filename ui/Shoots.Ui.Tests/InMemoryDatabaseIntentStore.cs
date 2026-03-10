using System;
using System.Collections.Generic;
using Shoots.UI.Intents;

namespace Shoots.UI.Tests;

internal sealed class InMemoryDatabaseIntentStore : IDatabaseIntentStore
{
    private readonly Dictionary<string, DatabaseIntent> _intents = new(StringComparer.OrdinalIgnoreCase);

    public DatabaseIntent GetIntent(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return DatabaseIntent.Undecided;
        }

        return _intents.TryGetValue(workspaceRoot, out var intent)
            ? intent
            : DatabaseIntent.Undecided;
    }

    public void SetIntent(string workspaceRoot, DatabaseIntent intent)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        _intents[workspaceRoot] = intent;
    }

    public IReadOnlyDictionary<string, DatabaseIntent> LoadAll()
    {
        return new Dictionary<string, DatabaseIntent>(_intents, StringComparer.OrdinalIgnoreCase);
    }
}
