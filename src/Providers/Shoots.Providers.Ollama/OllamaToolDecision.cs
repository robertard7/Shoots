using System.Collections.Generic;

namespace Shoots.Providers.Ollama;

public sealed class OllamaToolDecision
{
    public string? ToolId { get; set; }
    public Dictionary<string, object?>? Args { get; set; }
}
