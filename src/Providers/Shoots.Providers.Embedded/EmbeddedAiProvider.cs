using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Shoots.Contracts.Core;
using Shoots.Providers.Abstractions;
using Shoots.Runtime.Abstractions;

namespace Shoots.Providers.Embedded;

public sealed class EmbeddedAiProvider : IAiProvider, IAiProviderHealth, IAiProviderCapabilities
{
    public static readonly ProviderId EmbeddedProviderId = new(ProviderRegistry.EmbeddedProviderId);

    public ProviderId ProviderId => EmbeddedProviderId;

    public IReadOnlyList<string> Capabilities { get; } = new[]
    {
        "chat",
        "narration",
        "intent-analysis",
        "surface-explanation",
        "plan-review"
    };

    public AiProviderHealthStatus Status => AiProviderHealthStatus.Online;

    public string Detail => "Embedded deterministic provider (rule-driven).";

    public AiResponse Invoke(AiRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var summary = BuildDeterministicSummary(request.Prompt);
        var schema = string.IsNullOrWhiteSpace(request.OutputSchema) ? "none" : request.OutputSchema.Trim();
        var payload = $"{ProviderRegistry.EmbeddedProviderId}\nsummary={summary}\nschema={schema}";
        return new AiResponse(payload);
    }

    private static string BuildDeterministicSummary(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "no prompt provided";

        var trimmed = prompt.Trim();
        var lines = trimmed
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(6)
            .ToList();

        var builder = new StringBuilder();
        builder.Append("prompt-lines=");
        builder.Append(lines.Count);

        if (lines.Count > 0)
        {
            builder.Append("; first=");
            builder.Append(lines[0]);
        }

        return builder.ToString();
    }
}
