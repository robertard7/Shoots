using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoots.Contracts.Core.AI.Narration;

namespace Shoots.Runtime.Runner;

internal sealed class TextNarrator : INarrator, IDisposable
{
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
            phase = narrationEvent.Phase,
            code = narrationEvent.Code,
            message = narrationEvent.Message,
            data = narrationEvent.Data.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)
        };

        _writer.WriteLine(JsonSerializer.Serialize(payload));
        _writer.Flush();
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}
