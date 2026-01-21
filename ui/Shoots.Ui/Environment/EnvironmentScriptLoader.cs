using System.IO;
using System.Text.Json;

namespace Shoots.UI.Environment;

// UI-only. Declarative. Non-executable. Not runtime-affecting.
public sealed class EnvironmentScriptLoader
{
    public const string FileName = ".shoots-env.json";

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
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<EnvironmentScriptDocument>(json, JsonOptions());
            if (document is null)
            {
                error = "Script payload is invalid.";
                return false;
            }

            script = document.ToScript();
            return true;
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
            PropertyNameCaseInsensitive = true
        };

    private sealed record EnvironmentScriptDocument(
        string Name,
        string Description,
        string[]? DeclaredCapabilities,
        EnvironmentScriptStep[]? SandboxSteps)
    {
        public EnvironmentScript ToScript()
        {
            var caps = EnvironmentCapability.None;
            if (DeclaredCapabilities is not null)
            {
                foreach (var value in DeclaredCapabilities)
                {
                    if (Enum.TryParse<EnvironmentCapability>(value, true, out var parsed))
                        caps |= parsed;
                }
            }

            var steps = new List<SandboxPreparationStep>();
            if (SandboxSteps is not null)
            {
                foreach (var step in SandboxSteps)
                {
                    var kind = Enum.Parse<SandboxPreparationKind>(step.Kind, true);
                    steps.Add(new SandboxPreparationStep(step.RelativePath, kind));
                }
            }

            return new EnvironmentScript(
                Name ?? "Unnamed",
                Description ?? string.Empty,
                caps,
                steps);
        }
    }

    private sealed record EnvironmentScriptStep(
        string RelativePath,
        string Kind);
}
