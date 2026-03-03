using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoots.Contracts.Core.AI.Narration;

namespace Shoots.Runtime.Runner;

internal sealed class TextNarrator : INarrator, IDisposable
{
    private const int MaxFieldLength = 512;
    private const int MaxLineLength = 2048;

    private readonly StreamWriter _writer;

    public TextNarrator(string runDirectory)
    {
        var narrationDir = Path.Combine(runDirectory, "narration");
        Directory.CreateDirectory(narrationDir);
        var path = Path.Combine(narrationDir, "events.ndjson");
        _writer = new StreamWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read));
    }

    public void Emit(NarrationEvent narrationEvent)
    {
        var payload = new
        {
            phase = Truncate(narrationEvent.Phase),
            code = Truncate(narrationEvent.Code),
            message = Truncate(narrationEvent.Message),
            errorCode = Truncate(narrationEvent.ErrorCode),
            summary = Truncate(narrationEvent.Summary),
            details = Truncate(narrationEvent.Details),
            data = narrationEvent.Data.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => Truncate(kvp.Key), kvp => Truncate(kvp.Value), StringComparer.Ordinal)
        };

        var line = JsonSerializer.Serialize(payload);
        if (line.Length > MaxLineLength)
        {
            line = line[..MaxLineLength];
        }

        _writer.WriteLine(line);
        _writer.Flush();
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    private static string? Truncate(string? value)
    {
        if (value is null) return null;
        if (value.Length <= MaxFieldLength) return value;
        return value[..MaxFieldLength];
    }
}
