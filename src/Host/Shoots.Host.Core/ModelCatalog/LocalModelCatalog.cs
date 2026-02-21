using System.Text.Json;
using Shoots.Host.Abstractions;

namespace Shoots.Host.Core.ModelCatalog;

public sealed class LocalModelCatalog : IModelCatalog
{
    private readonly string _catalogPath;

    public LocalModelCatalog(string? catalogPath = null)
    {
        _catalogPath = catalogPath ?? Path.GetFullPath(Path.Combine(".state", "models.catalog.json"));
    }

    private string TemplatePath => Path.GetFullPath(Path.Combine("etc", "models.catalog.template.json"));

    public IReadOnlyList<ModelDescriptor> ListModels()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        if (!File.Exists(_catalogPath))
        {
            if (File.Exists(TemplatePath))
            {
                File.Copy(TemplatePath, _catalogPath, overwrite: false);
            }
            else
            {
                var seed = new[]
                {
                    new ModelDescriptor("local.default", "provider.local", 0, IsRemote: false, SupportsTools: true),
                    new ModelDescriptor("remote.assist", "provider.remote", 10, IsRemote: true, SupportsTools: true)
                };
                File.WriteAllText(_catalogPath, JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true }));
                return seed;
            }
        }

        var models = JsonSerializer.Deserialize<List<ModelDescriptor>>(File.ReadAllText(_catalogPath))
            ?? new List<ModelDescriptor>();

        return models
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.ProviderId, StringComparer.Ordinal)
            .ThenBy(x => x.ModelId, StringComparer.Ordinal)
            .ToList();
    }

    public ModelDescriptor ResolveDefaultModel()
        => ListModels().First();
}
