namespace Shoots.Providers.Ollama;

public sealed record OllamaProviderSettings(
    string Endpoint,
    string Model);
