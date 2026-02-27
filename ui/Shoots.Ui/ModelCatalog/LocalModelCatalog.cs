using System.IO;
using System.Linq;
using System.Text.Json;
using Shoots.Host.Abstractions;

namespace Shoots.Host.Core.ModelCatalog;

public sealed class LocalModelCatalog : IModelCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _catalogPath;
    private readonly string _localOverridePath;
    private readonly string _templatePath;

    public LocalModelCatalog(string? catalogPath = null, string? localOverridePath = null, string? templatePath = null)
    {
        _catalogPath = catalogPath;
        _localOverridePath = localOverridePath ?? Path.GetFullPath(Path.Combine("etc", "models.catalog.local.json"));
        _templatePath = templatePath ?? Path.GetFullPath(Path.Combine("etc", "models.catalog.template.json"));
    }

    public IReadOnlyList<ModelDescriptor> ListModels()
    {
        if (_catalogPath is null)
        {
            var resolvedPath = ResolveCatalogPath();
            if (resolvedPath is null)
                return DefaultSeed();

            var models = JsonSerializer.Deserialize<List<ModelDescriptor>>(File.ReadAllText(resolvedPath))
                ?? new List<ModelDescriptor>();

            return Sort(models);
        }

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

            return Sort(models);
        }
        catch (JsonException)
        {
            ResetCatalogToDefaults();
            return LoadModelsOrFallback();
        }
    }

    public ModelDescriptor ResolveDefaultModel()
        => ListModels().First();

    public ModelDescriptor ResolveEffectiveModel(string? modelId)
    {
        var models = ListModels();
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var selected = models.FirstOrDefault(m => string.Equals(m.ModelId, modelId, StringComparison.Ordinal));
            if (selected is not null)
                return selected;
        }

        return ResolveDefaultModel();
    }

    public void ResetCatalogToDefaults()
    {
        if (_catalogPath is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);

        if (File.Exists(_templatePath))
        {
            var template = File.ReadAllText(_templatePath);
            _ = JsonSerializer.Deserialize<List<ModelDescriptor>>(template) ?? throw new JsonException("Template model catalog is invalid.");
            File.WriteAllText(_catalogPath, template);
            return;
        }

        File.WriteAllText(_catalogPath, JsonSerializer.Serialize(DefaultSeed(), JsonOptions));
    }

    private void EnsureCatalogFile()
    {
        if (_catalogPath is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        if (File.Exists(_catalogPath))
            return;

        ResetCatalogToDefaults();
    }

    private List<ModelDescriptor> LoadModelsOrFallback()
        => Sort(JsonSerializer.Deserialize<List<ModelDescriptor>>(File.ReadAllText(_catalogPath!)) ?? DefaultSeed());

    private string? ResolveCatalogPath()
    {
        if (File.Exists(_localOverridePath))
            return _localOverridePath;
        if (File.Exists(_templatePath))
            return _templatePath;

        return null;
    }

    private static List<ModelDescriptor> Sort(IEnumerable<ModelDescriptor> models)
        => models
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
