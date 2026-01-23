using System;
using System.Collections.Generic;

namespace Shoots.Runtime.Ui.Abstractions;

public sealed record AiHelpIntentUsage(
    DateTimeOffset Timestamp,
    string SurfaceId,
    AiIntentDescriptor Intent,
    string? ScopeSummary,
    IReadOnlyDictionary<string, string> ScopeData
);

public interface IAiHelpIntentLogger
{
    void Record(AiHelpIntentUsage usage);
}
