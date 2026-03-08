using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Shoots.UI.Intents;

public sealed class DeterministicIntentParser
{
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex StartNewProject = new("^(start a new project|create new workspace)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateProject = new("^create project called (?<name>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamedProject = new("new project(?: called| named)? (?<name>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateProjectInPath = new("^make a project in (?<path>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OpenProject = new("open project (?<path>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BuildPlan = new("build plan (?<path>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AddNote = new("add note (?<note>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IntentModel Parse(string text)
    {
        var raw = text ?? string.Empty;
        var normalized = Normalize(raw);
        var createdUtc = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();

        if (normalized == "start new project" || normalized == "new project")
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.CreateProject, 0.99, "exact keyword", new Dictionary<string, string>());
        }

        if (StartNewProject.IsMatch(normalized))
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.CreateProject, 0.98, "start/create workspace keyword", new Dictionary<string, string>());
        }

        var createMatch = CreateProject.Match(normalized);
        if (createMatch.Success)
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.CreateProject, 0.95, "create project pattern", new Dictionary<string, string>
            {
                ["name"] = createMatch.Groups["name"].Value.Trim()
            });
        }

        var namedMatch = NamedProject.Match(normalized);
        if (namedMatch.Success)
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.CreateProject, 0.95, "named project pattern", new Dictionary<string, string>
            {
                ["name"] = namedMatch.Groups["name"].Value.Trim()
            });
        }

        var openMatch = OpenProject.Match(normalized);
        if (openMatch.Success)
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.OpenProject, 0.95, "open project pattern", new Dictionary<string, string>
            {
                ["path"] = openMatch.Groups["path"].Value.Trim()
            });
        }

        var createPathMatch = CreateProjectInPath.Match(normalized);
        if (createPathMatch.Success)
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.CreateProject, 0.9, "create project in path pattern", new Dictionary<string, string>
            {
                ["path"] = createPathMatch.Groups["path"].Value.Trim()
            });
        }

        if (normalized == "run demo" || normalized == "run demo plan")
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.RunDemoPlan, 0.99, "run demo keyword", new Dictionary<string, string>());
        }

        var buildMatch = BuildPlan.Match(normalized);
        if (buildMatch.Success)
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.BuildFromPlanFile, 0.93, "build plan pattern", new Dictionary<string, string>
            {
                ["path"] = buildMatch.Groups["path"].Value.Trim()
            });
        }

        var noteMatch = AddNote.Match(normalized);
        if (noteMatch.Success)
        {
            return Create(intentId, createdUtc, raw, normalized, IntentKind.AddNote, 0.9, "add note pattern", new Dictionary<string, string>
            {
                ["note"] = noteMatch.Groups["note"].Value.Trim()
            });
        }

        return Create(intentId, createdUtc, raw, normalized, IntentKind.Unknown, 0.0, "no matching rule", new Dictionary<string, string>());
    }

    private static IntentModel Create(Guid intentId, DateTimeOffset createdUtc, string raw, string normalized, IntentKind kind, double confidence, string diagnostics, IReadOnlyDictionary<string, string> args)
        => new(intentId, createdUtc, raw, normalized, kind, args, confidence, diagnostics);

    private static string Normalize(string text)
        => MultiWhitespace.Replace(text.Trim().ToLowerInvariant(), " ");
}
