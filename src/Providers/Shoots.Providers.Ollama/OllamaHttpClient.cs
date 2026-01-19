using System;

namespace Shoots.Providers.Ollama;

public sealed class OllamaHttpClient
{
    private readonly string _stubResponse;

    public OllamaHttpClient(string stubResponse)
    {
        if (string.IsNullOrWhiteSpace(stubResponse))
            throw new ArgumentException("stub response is required", nameof(stubResponse));

        _stubResponse = stubResponse;
    }

    public string SendPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("prompt is required", nameof(prompt));

        return _stubResponse;
    }
}
