namespace Shoots.Providers.Ollama;

/// <summary>
/// Validated runtime settings for the Ollama provider.
/// </summary>
public sealed record OllamaProviderSettings(
    string Endpoint,
    string Model);

public static class OllamaProviderSettingsFactory
{
    public static OllamaProviderSettings FromConfig(OllamaProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new ArgumentException("endpoint is required", nameof(config));
        if (string.IsNullOrWhiteSpace(config.Model))
            throw new ArgumentException("model is required", nameof(config));

        return new OllamaProviderSettings(config.Endpoint, config.Model);
    }
}
