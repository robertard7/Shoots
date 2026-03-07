using System.Text.Json.Serialization;

namespace Shoots.UI.Builder;

public sealed record BaselinePolicy(
    string PlanHash,
    string ExpectedManifestHash,
    string CatalogHash,
    string EnvironmentPolicy
);
