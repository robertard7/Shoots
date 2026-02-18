namespace Shoots.ProviderAdapters.Ollama;

/// <summary>
/// Serialized configuration input. Use OllamaProviderSettings for validated runtime usage.
/// </summary>
public sealed record OllamaProviderConfig(
    string Endpoint,
    string Model);
