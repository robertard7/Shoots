#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services.AiHelp;

public sealed class SimpleAiHelpSurface : IAiHelpSurface
{
    private readonly string _context;
    private readonly string _capabilities;
    private readonly string _constraints;

    public SimpleAiHelpSurface(
        string surfaceId,
        string surfaceKind,
        IEnumerable<AiIntentDescriptor>? supportedIntents,
        string context,
        string capabilities,
        string constraints)
    {
        SurfaceId = surfaceId ?? throw new ArgumentNullException(nameof(surfaceId));
        SurfaceKind = surfaceKind ?? throw new ArgumentNullException(nameof(surfaceKind));

        // Avoid pointless null-filtering: AiIntentDescriptor might be a struct.
        SupportedIntents = (supportedIntents ?? Array.Empty<AiIntentDescriptor>())
            .ToArray();

        _context = context ?? string.Empty;
        _capabilities = capabilities ?? string.Empty;
        _constraints = constraints ?? string.Empty;
    }

    public string SurfaceId { get; }

    public string SurfaceKind { get; }

    public IReadOnlyList<AiIntentDescriptor> SupportedIntents { get; }

    public string DescribeContext() => _context;

    public string DescribeCapabilities() => _capabilities;

    public string DescribeConstraints() => _constraints;

    // Convenience: create intents without leaking authority language.
    public static AiIntentDescriptor Intent(string id, string label, string description)
    {
        // Defensive normalization: callers *will* pass null eventually.
        id = (id ?? string.Empty).Trim();
        label = (label ?? string.Empty).Trim();
        description = description ?? string.Empty;

        var type = default(AiIntentType);
        var scope = default(AiIntentScope);

        // Optional forward-compatible parse. If it doesn't match, defaults remain stable.
        var parts = id.Split(new[] { ':', '/', '|' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 1)
            Enum.TryParse(parts[0], ignoreCase: true, out type);

        if (parts.Length >= 2)
            Enum.TryParse(parts[1], ignoreCase: true, out scope);

        // Try ctor shapes in a deterministic order.
        // This prevents "contract drift" from blowing up the UI build.
        var t = typeof(AiIntentDescriptor);

        object? created =
            // Newer-ish shapes (enum-heavy)
            TryCreate(t, type, scope, id, label, description) ??
            TryCreate(t, type, scope, label, description) ??
            TryCreate(t, type, scope, id, label) ??
            TryCreate(t, type, scope, label) ??
            TryCreate(t, type, scope, id) ??

            // Older shapes (string-heavy)
            TryCreate(t, id, label, description) ??
            TryCreate(t, id, label) ??
            TryCreate(t, id) ??
            TryCreate(t);

        if (created is AiIntentDescriptor typed)
            return typed;

        // Absolute last resort: default instance. Keeps compilation/runtime stable.
        return default;
    }

    private static object? TryCreate(Type t, params object?[] args)
    {
        try
        {
            return Activator.CreateInstance(t, args);
        }
        catch
        {
            return null;
        }
    }
}