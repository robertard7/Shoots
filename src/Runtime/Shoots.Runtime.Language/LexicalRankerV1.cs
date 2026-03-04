using System.Text.RegularExpressions;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Language;

public sealed class LexicalRankerV1
{
    public IReadOnlyList<RetrievalHit> Rank(string queryText, IReadOnlyList<RepoSliceFile> files)
    {
        var queryTokens = Tokenize(queryText);
        if (queryTokens.Count == 0)
        {
            return Array.Empty<RetrievalHit>();
        }

        var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
        var tokenizedFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var fileTokens = Tokenize(file.Excerpt).ToHashSet(StringComparer.Ordinal);
            tokenizedFiles[file.RelPath] = fileTokens;
            foreach (var token in fileTokens)
            {
                docFreq[token] = docFreq.TryGetValue(token, out var count) ? count + 1 : 1;
            }
        }

        var docCount = Math.Max(files.Count, 1);
        var hits = new List<RetrievalHit>();
        foreach (var file in files)
        {
            var tokens = tokenizedFiles[file.RelPath];
            long score = 0;
            var reasons = new List<string>();

            foreach (var token in queryTokens)
            {
                if (!tokens.Contains(token))
                {
                    continue;
                }

                reasons.Add("token.match");
                var idf = docFreq.TryGetValue(token, out var df) ? (docCount - df + 1) : 1;
                score += idf * 1000L;
            }

            if (score <= 0)
            {
                continue;
            }

            if (reasons.Count > 0)
            {
                reasons.Add("idf.boost");
            }

            hits.Add(new RetrievalHit
            {
                Path = file.RelPath,
                Score = score,
                ReasonCodes = reasons.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                SliceRef = $"slice/files/{file.RelPath.Replace('/', '_')}.txt",
                Excerpt = file.Excerpt
            });
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return Regex.Split(value.ToLowerInvariant(), "[^a-z0-9_]+")
            .Where(x => x.Length >= 2)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }
}
