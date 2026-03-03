using System;
using System.Collections.Generic;

namespace Shoots.Contracts.Core.AI.Narration;

public enum NarrationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public static class NarrationCodebook
{
    private static readonly HashSet<string> KnownPhases = new(StringComparer.Ordinal)
    {
        "startup",
        "plan",
        "provider",
        "env",
        "execute",
        "tool",
        "finalize",
        "replay",
        "retrieval",
        "builder",
        "ui"
    };

    public static bool IsKnownPhase(string phase)
        => !string.IsNullOrWhiteSpace(phase) && KnownPhases.Contains(phase);

    public static NarrationSeverity GetSeverity(string code)
        => string.Equals(code, "error", StringComparison.Ordinal)
            ? NarrationSeverity.Error
            : NarrationSeverity.Info;

    public static bool TryValidate(NarrationEvent narrationEvent, out string error)
    {
        error = string.Empty;
        if (!IsKnownPhase(narrationEvent.Phase))
        {
            error = "narration.phase.unknown";
            return false;
        }

        if (GetSeverity(narrationEvent.Code) >= NarrationSeverity.Error
            && string.IsNullOrWhiteSpace(narrationEvent.ErrorCode))
        {
            error = "narration.errorcode.missing";
            return false;
        }

        if (narrationEvent.Data.TryGetValue("artifactRefs", out var refs)
            && refs is not null
            && refs.Length > 1024)
        {
            error = "narration.artifactrefs.too_long";
            return false;
        }

        return true;
    }
}
