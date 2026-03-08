using System;
using System.Collections.Generic;

namespace Shoots.UI.Diagnostics;

public sealed record NarrationEvent(
    DateTimeOffset Utc,
    string Kind,
    string Message,
    IReadOnlyDictionary<string, string>? Data = null
);
