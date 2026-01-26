namespace Shoots.UI.ExecutionEnvironments;

public sealed record RootFsDescriptor(
    string Id,
    string DisplayName,
    string SourceType,
    string? DefaultUrl,
    string? Checksum,
    string? License,
    string? Notes);
