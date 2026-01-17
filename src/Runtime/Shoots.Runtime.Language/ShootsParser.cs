using Shoots.Runtime.Abstractions;
using System.Text;

namespace Shoots.Runtime.Language;

public static class ShootsParser
{
    public static RuntimeRequest Parse(string input, RuntimeContext context)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("parse_error: empty command");

        var tokens = Tokenize(input);
        if (tokens.Count == 0)
            throw new ArgumentException("parse_error: no tokens");

        var commandId = tokens[0];

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < tokens.Count; i++)
        {
            var part = tokens[i];
            var idx = part.IndexOf('=');

            // allow bare args to be ignored (future-proofing)
            if (idx <= 0)
                continue;

            var key = part[..idx].Trim();
            var value = part[(idx + 1)..];

            if (key.Length == 0)
                continue;

            args[key] = value;
        }

        return new RuntimeRequest(commandId, args, context);
    }

    private static List<string> Tokenize(string input)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
            }
            else
            {
                sb.Append(c);
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (sb.Length == 0) return;
            result.Add(sb.ToString());
            sb.Clear();
        }
    }
}
