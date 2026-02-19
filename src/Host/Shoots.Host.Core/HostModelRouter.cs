using Shoots.Host.Abstractions;

namespace Shoots.Host.Core;

public sealed class HostModelRouter
{
    private readonly IModelCatalog _catalog;
    private readonly IProviderPolicy _policy;

    public HostModelRouter(IModelCatalog catalog, IProviderPolicy policy)
    {
        _catalog = catalog;
        _policy = policy;
    }

    public ProviderSelection Select(ProviderSelectionRequest request)
    {
        var models = _catalog.ListModels()
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.ProviderId, StringComparer.Ordinal)
            .ThenBy(x => x.ModelId, StringComparer.Ordinal)
            .ToList();

        return _policy.Select(request, models);
    }
}

public sealed class DeterministicProviderPolicy : IProviderPolicy
{
    public ProviderSelection Select(ProviderSelectionRequest request, IReadOnlyList<ModelDescriptor> models)
    {
        var filtered = models.Where(m =>
                (request.Policy.AllowRemote || !m.IsRemote) &&
                (request.Policy.AllowLocal || m.IsRemote))
            .ToList();

        if (request.PreferredModelId is not null)
        {
            var preferred = filtered.FirstOrDefault(x => string.Equals(x.ModelId, request.PreferredModelId, StringComparison.Ordinal));
            if (preferred is not null)
                return new ProviderSelection(preferred.ProviderId, preferred.ModelId, preferred.IsRemote);
        }

        var chosen = filtered.FirstOrDefault()
            ?? throw new InvalidOperationException("No model matched provider policy.");

        return new ProviderSelection(chosen.ProviderId, chosen.ModelId, chosen.IsRemote);
    }
}
