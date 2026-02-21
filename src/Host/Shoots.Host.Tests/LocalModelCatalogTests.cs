using Shoots.Host.Core.ModelCatalog;
using Xunit;

namespace Shoots.Host.Tests;

public sealed class LocalModelCatalogTests
{
    [Fact]
    public void Missing_catalog_is_seeded_and_default_is_deterministic()
    {
        var root = CreateTempRoot();
        var catalogPath = Path.Combine(root, ".state", "models.catalog.json");
        var catalog = new LocalModelCatalog(catalogPath);

        var models = catalog.ListModels();
        Assert.NotEmpty(models);
        Assert.True(File.Exists(catalogPath));
        Assert.Equal(models[0].ModelId, catalog.ResolveDefaultModel().ModelId);
    }

    [Fact]
    public void Invalid_catalog_json_can_be_reset_to_defaults()
    {
        var root = CreateTempRoot();
        var catalogPath = Path.Combine(root, ".state", "models.catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllText(catalogPath, "{ invalid");

        var catalog = new LocalModelCatalog(catalogPath);
        var models = catalog.ListModels();

        Assert.NotEmpty(models);
        Assert.Equal(models[0].ModelId, catalog.ResolveDefaultModel().ModelId);
    }

    [Fact]
    public void Model_list_order_is_stable_by_priority_provider_and_model()
    {
        var root = CreateTempRoot();
        var catalogPath = Path.Combine(root, ".state", "models.catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllText(catalogPath, """
[
  {"ModelId":"b","ProviderId":"z","Priority":0,"IsRemote":false,"SupportsTools":true},
  {"ModelId":"a","ProviderId":"a","Priority":0,"IsRemote":false,"SupportsTools":true},
  {"ModelId":"c","ProviderId":"b","Priority":10,"IsRemote":true,"SupportsTools":true}
]
""");

        var catalog = new LocalModelCatalog(catalogPath);
        var models = catalog.ListModels().Select(x => x.ModelId).ToArray();

        Assert.Equal(new[] { "a", "b", "c" }, models);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "shoots-host-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
