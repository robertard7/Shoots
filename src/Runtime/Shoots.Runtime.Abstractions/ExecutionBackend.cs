using System;
using System.Collections.Generic;

namespace Shoots.Runtime.Abstractions;

public interface IExecutionBackend
{
    Task<ExecutionBackendSnapshot> PrepareAsync(
        RootFsDescriptor descriptor,
        CancellationToken ct = default);
}

public sealed record ExecutionBackendSnapshot(
    RootFsDescriptor Descriptor,
    string CachePath,
    RootFsProvenance Provenance
);

public sealed record RootFsDescriptor(
    string Id,
    string DisplayName,
    RootFsSourceType SourceType,
    string? DefaultUrl,
    string? Checksum,
    string License,
    string? Notes
);

public sealed record RootFsProvenance(
    string ProviderId,
    string Source,
    string? Checksum,
    DateTimeOffset PreparedUtc,
    IReadOnlyDictionary<string, string> Metadata
);

public enum RootFsSourceType
{
    Http,
    LocalPath
}
