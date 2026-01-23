using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shoots.UI.Environment;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed class EnvironmentScriptLoader
{
    public const string FileName = ".shoots-env.json";
    public const int SupportedSchemaVersion = 1;

    public bool TryLoad(string directory, out EnvironmentScript? script, out string? error)
    {
        script = null;
        error = null;

        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "Directory is required.";
            return false;
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<EnvironmentScriptDocument>(json, JsonOptions());
            if (document is null)
            {
                error = "Script payload is invalid.";
                return false;
            }

            return document.TryCreateScript(out script, out error);
        }
        catch (JsonException)
        {
            error = "Script payload contains unknown or invalid fields.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static JsonSerializerOptions JsonOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling =
                System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };

    private sealed record EnvironmentScriptDocument(
        int SchemaVersion,
        string Name,
        string Description,
        string[]? DeclaredCapabilities,
        EnvironmentScriptStep[]? SandboxSteps)
    {
        public bool TryCreateScript(out EnvironmentScript? script, out string? error)
        {
            script = null;
            error = null;

            if (SchemaVersion != SupportedSchemaVersion)
            {
                error = $"Unsupported script schema version: {SchemaVersion}.";
                return false;
            }

            var caps = EnvironmentCapability.None;
            var unknownCaps = new System.Collections.Generic.List<string>();

            if (DeclaredCapabilities is not null)
            {
                foreach (var value in DeclaredCapabilities)
                {
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (Enum.TryParse<EnvironmentCapability>(value, true, out var parsed))
                    {
                        caps |= parsed;
                        continue;
                    }

                    unknownCaps.Add(value);
                }
            }

            if (unknownCaps.Count > 0)
            {
                error = $"Unknown capabilities: {string.Join(", ", unknownCaps)}.";
                return false;
            }

            var steps = new System.Collections.Generic.List<SandboxPreparationStep>();
            if (SandboxSteps is not null)
            {
                foreach (var step in SandboxSteps)
                {
                    if (!TryValidatePath(step.RelativePath, out var pathError))
                    {
                        error = pathError;
                        return false;
                    }

                    if (!Enum.TryParse<SandboxPreparationKind>(step.Kind, true, out var kind))
                    {
                        error = $"Unknown step kind: {step.Kind}.";
                        return false;
                    }

                    steps.Add(new SandboxPreparationStep(step.RelativePath, kind));
                }
            }

            script = new EnvironmentScript(
                Name ?? "Unnamed",
                Description ?? string.Empty,
                caps,
                steps);

            return true;
        }
    }

    private sealed record EnvironmentScriptStep(
        string RelativePath,
        string Kind);

    private static bool TryValidatePath(string? relativePath, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "Script step path is required.";
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = $"Script step path must be relative: {relativePath}.";
            return false;
        }

        var segments = relativePath.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (segments.Any(s => string.Equals(s, "..", StringComparison.Ordinal)))
        {
            error = $"Script step path cannot contain traversal segments: {relativePath}.";
            return false;
        }

        return true;
    }
}
