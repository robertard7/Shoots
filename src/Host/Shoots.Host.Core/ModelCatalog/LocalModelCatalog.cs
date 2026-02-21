using System.Text.Json;
using Shoots.Host.Abstractions;

namespace Shoots.Host.Core.ModelCatalog;

public sealed class LocalModelCatalog : IModelCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _catalogPath;

    public LocalModelCatalog(string? catalogPath = null)
    {
        _catalogPath = catalogPath ?? Path.GetFullPath(Path.Combine(".state", "models.catalog.json"));
    }

    private string TemplatePath => Path.GetFullPath(Path.Combine("etc", "models.catalog.template.json"));

    public IReadOnlyList<ModelDescriptor> ListModels()
    {
        EnsureCatalogFile();

        try
        {
            var models = JsonSerializer.Deserialize<List<ModelDescriptor>>(File.ReadAllText(_catalogPath))
                ?? new List<ModelDescriptor>();

            if (models.Count == 0)
            {
                ResetCatalogToDefaults();
                models = LoadModelsOrFallback();
            }

            return models
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.ProviderId, StringComparer.Ordinal)
                .ThenBy(x => x.ModelId, StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            ResetCatalogToDefaults();
            return LoadModelsOrFallback();
        }
    }

    public ModelDescriptor ResolveDefaultModel()
        => ListModels().First();

    public void ResetCatalogToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);

        if (File.Exists(TemplatePath))
        {
            var template = File.ReadAllText(TemplatePath);
            _ = JsonSerializer.Deserialize<List<ModelDescriptor>>(template) ?? throw new JsonException("Template model catalog is invalid.");
            File.WriteAllText(_catalogPath, template);
            return;
        }

        File.WriteAllText(_catalogPath, JsonSerializer.Serialize(DefaultSeed(), JsonOptions));
    }

    private void EnsureCatalogFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        if (File.Exists(_catalogPath))
            return;

        ResetCatalogToDefaults();
    }

    private List<ModelDescriptor> LoadModelsOrFallback()
        => (JsonSerializer.Deserialize<List<ModelDescriptor>>(File.ReadAllText(_catalogPath)) ?? DefaultSeed())
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.ProviderId, StringComparer.Ordinal)
            .ThenBy(x => x.ModelId, StringComparer.Ordinal)
            .ToList();

    private static List<ModelDescriptor> DefaultSeed() =>
    [
        new ModelDescriptor("local.default", "provider.local", 0, IsRemote: false, SupportsTools: true),
        new ModelDescriptor("remote.assist", "provider.remote", 10, IsRemote: true, SupportsTools: true)
    ];
}
