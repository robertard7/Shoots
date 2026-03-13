using System;
using System.IO;
using System.Text.Json;

namespace Shoots.UI.Services;

public sealed record GeneratedOutputValidationLink(
    string SourceRunId,
    string SourceRunPath,
    string SourcePath,
    string ValidationStatus,
    string ValidationSummary,
    string ValidationActionLabel,
    string? ValidationRunId,
    string? ValidationOutputFolder,
    string? FirstFailureText,
    DateTimeOffset RecordedUtc);

public static class GeneratedOutputValidationLinkService
{
    public const string FileName = "generated_output_validation.json";

    public static string PathForRun(string runPath)
        => System.IO.Path.Combine(runPath, FileName);

    public static GeneratedOutputValidationLink Load(string runPath)
    {
        var path = PathForRun(runPath);
        if (!File.Exists(path))
            return CreateDefault(string.Empty, runPath);

        try
        {
            var link = JsonSerializer.Deserialize<GeneratedOutputValidationLink>(File.ReadAllText(path), JsonOptions());
            return link ?? CreateDefault(string.Empty, runPath);
        }
        catch
        {
            return CreateDefault(string.Empty, runPath);
        }
    }

    public static void Save(GeneratedOutputValidationLink link)
    {
        if (link is null)
            throw new ArgumentNullException(nameof(link));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathForRun(link.SourceRunPath))!);
        File.WriteAllText(PathForRun(link.SourceRunPath), JsonSerializer.Serialize(link, JsonOptions()));
    }

    public static GeneratedOutputValidationLink CreateDefault(string runId, string runPath)
        => new(
            runId,
            runPath,
            runPath,
            "not_validated",
            "Generated output has not been validated.",
            "Generate only",
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
}
