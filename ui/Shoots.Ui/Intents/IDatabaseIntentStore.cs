using System.Collections.Generic;

namespace Shoots.UI.Intents;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public interface IDatabaseIntentStore
{
    DatabaseIntent GetIntent(string workspaceRoot);

    void SetIntent(string workspaceRoot, DatabaseIntent intent);

    IReadOnlyDictionary<string, DatabaseIntent> LoadAll();
}
