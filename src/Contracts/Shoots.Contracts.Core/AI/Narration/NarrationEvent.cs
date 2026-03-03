using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Shoots.Contracts.Core.AI.Narration;

public sealed record NarrationEvent
{
    public NarrationEvent(
        string phase,
        string code,
        string message,
        IDictionary<string, string>? data = null,
        string? errorCode = null,
        string? summary = null,
        string? details = null)
    {
        Phase = phase ?? throw new ArgumentNullException(nameof(phase));
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        ErrorCode = errorCode;
        Summary = summary;
        Details = details;
        Data = new ReadOnlyDictionary<string, string>(
            (data ?? new Dictionary<string, string>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public string Phase { get; }

    public string Code { get; }

    public string Message { get; }

    public string? ErrorCode { get; }

    public string? Summary { get; }

    public string? Details { get; }

    public IReadOnlyDictionary<string, string> Data { get; }
}
