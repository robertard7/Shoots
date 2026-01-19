namespace Shoots.Providers.Ollama;

public sealed record OllamaProviderConfig(
    string Endpoint,
    string Model);
