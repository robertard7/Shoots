namespace Shoots.UI.Environment;

public sealed record EnvironmentProfileResult(
    string ProfileName,
    IReadOnlyList<string> CreatedPaths,
    EnvironmentCapability DeclaredCapabilities,
    DateTimeOffset AppliedAtUtc
);
