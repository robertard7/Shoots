using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Shoots.UI.Diagnostics;

internal static class UiActionTraceBuffer
{
    private const int MaxLines = 200;
    private static readonly object Gate = new();
    private static readonly Queue<string> Lines = new(MaxLines);
    private static bool _initialized;

    public static event Action<string>? LineCaptured;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            Trace.Listeners.Add(new BufferingTraceListener());
            _initialized = true;
        }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
        {
            return Lines.ToArray();
        }
    }

    private static void Append(string line)
    {
        lock (Gate)
        {
            while (Lines.Count >= MaxLines)
            {
                _ = Lines.Dequeue();
            }

            Lines.Enqueue(line);
        }

        LineCaptured?.Invoke(line);
    }

    private sealed class BufferingTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Append(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Append(message);
            }
        }
    }
}
