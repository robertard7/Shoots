using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core.AI;
using Shoots.Runtime.Ui.Abstractions;

namespace Shoots.UI.Services;

public sealed class AiSurfaceRegistry : IAiSurfaceRegistry
{
    private readonly Dictionary<string, AiSurfaceRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public static AiSurfaceRegistry Current { get; } = new();

    public IReadOnlyList<AiSurfaceRegistration> RegisteredSurfaces
        => _registrations.Values.ToList();

    public void Register(AiSurfaceRegistration registration)
    {
        ValidateRegistration(registration);
        _registrations[registration.SurfaceId] = registration;
    }

    public void Register(IAiHelpSurface surface)
    {
        if (surface is null)
            throw new ArgumentNullException(nameof(surface));

        var intents = surface.SupportedIntents
            .Select(intent => $"{intent.Type}:{intent.Scope}")
            .ToList();

        var registration = new AiSurfaceRegistration(
            surface.SurfaceId,
            surface.SurfaceKind,
            surface.SurfaceKind,
            intents,
            new[]
            {
                AiNarrationHook.Plan,
                AiNarrationHook.State,
                AiNarrationHook.Error,
                AiNarrationHook.Result
            },
            $"IAiHelpSurface:{surface.GetType().Name}");

        Register(registration);
    }

    public void Register(IEnumerable<IAiHelpSurface> surfaces)
    {
        if (surfaces is null)
            throw new ArgumentNullException(nameof(surfaces));

        foreach (var surface in surfaces)
            Register(surface);
    }

    public void AssertRequiredSurfacesRegistered(IEnumerable<string> requiredSurfaceIds)
    {
        if (requiredSurfaceIds is null)
            throw new ArgumentNullException(nameof(requiredSurfaceIds));

        var missing = requiredSurfaceIds
            .Where(id => !_registrations.ContainsKey(id))
            .ToList();

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            $"AI surface registration missing for: {string.Join(", ", missing)}.");
    }

    private static void ValidateRegistration(AiSurfaceRegistration registration)
    {
        if (registration is null)
            throw new ArgumentNullException(nameof(registration));

        if (string.IsNullOrWhiteSpace(registration.SurfaceId))
            throw new InvalidOperationException("AI surface registration requires a SurfaceId.");

        if (string.IsNullOrWhiteSpace(registration.SurfaceKind))
            throw new InvalidOperationException("AI surface registration requires a SurfaceKind.");

        if (string.IsNullOrWhiteSpace(registration.DisplayName))
            throw new InvalidOperationException("AI surface registration requires a DisplayName.");

        if (registration.DeclaredIntents is null || registration.DeclaredIntents.Count == 0)
            throw new InvalidOperationException("AI surface registration requires declared intents.");

        if (registration.NarrationHooks is null || registration.NarrationHooks.Count == 0)
            throw new InvalidOperationException("AI surface registration requires narration hooks.");

        if (string.IsNullOrWhiteSpace(registration.SnapshotProvider))
            throw new InvalidOperationException("AI surface registration requires a snapshot provider.");
    }
}
