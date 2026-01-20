using System;

namespace Shoots.Providers.Ollama;

public sealed class OllamaStubClient
{
    private readonly string _stubResponse;

    public OllamaStubClient(string stubResponse)
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
