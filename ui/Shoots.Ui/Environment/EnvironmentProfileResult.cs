// UI-only. Declarative. Non-executable. Not runtime-affecting.
namespace Shoots.UI.Environment;

public sealed record EnvironmentProfileResult(
    string ProfileName,
    IReadOnlyList<string> CreatedPaths,
    EnvironmentCapability DeclaredCapabilities,
    DateTimeOffset AppliedAtUtc
);
