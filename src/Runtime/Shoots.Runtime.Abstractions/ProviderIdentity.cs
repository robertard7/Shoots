namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Immutable provider identity.
/// </summary>
public readonly record struct ProviderId(string Value);

/// <summary>
/// Provider authority classification.
/// </summary>
public enum ProviderKind
{
    Local = 0,
    Remote = 1,
    Delegated = 2
}

/// <summary>
/// Declarative provider capabilities.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Plan = 1,
    Execute = 2,
    Artifacts = 4
}
