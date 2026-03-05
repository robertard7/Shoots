using System.Collections.Generic;

namespace Shoots.UI.Intents;

public sealed record IntentTrace(
    string Raw,
    string Normalized,
    IntentKind Kind,
    IReadOnlyDictionary<string, string> Args,
    string Handler,
    string Result
);
