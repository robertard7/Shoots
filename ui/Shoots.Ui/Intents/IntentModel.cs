using System;
using System.Collections.Generic;

namespace Shoots.UI.Intents;

public sealed record IntentModel(
    Guid IntentId,
    DateTimeOffset CreatedUtc,
    string RawUserText,
    string NormalizedText,
    IntentKind Kind,
    IReadOnlyDictionary<string, string> Args,
    double Confidence,
    string Diagnostics
);
