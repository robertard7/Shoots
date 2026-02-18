using System.Collections.Generic;

namespace Shoots.ProviderAdapters.Ollama;

public sealed class OllamaToolDecision
{
    public string? ToolId { get; set; }
    public Dictionary<string, object?>? Args { get; set; }
}
