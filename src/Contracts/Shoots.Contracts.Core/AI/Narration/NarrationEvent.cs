using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Shoots.Contracts.Core.AI.Narration;

public sealed record NarrationEvent
{
    public NarrationEvent(string phase, string code, string message, IDictionary<string, string>? data = null)
    {
        Phase = phase ?? throw new ArgumentNullException(nameof(phase));
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Data = new ReadOnlyDictionary<string, string>(
            (data ?? new Dictionary<string, string>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public string Phase { get; }

    public string Code { get; }

    public string Message { get; }

    public IReadOnlyDictionary<string, string> Data { get; }
}
