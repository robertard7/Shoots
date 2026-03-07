using System.Collections.Generic;

namespace Shoots.UI.Builder;

public sealed record RunVerificationResult(
    bool Valid,
    bool ManifestValid,
    bool ArtifactsValid,
    bool EnvironmentValid,
    bool NarratorValid,
    bool BundleValid,
    bool CatalogValid,
    bool TranscriptValid,
    IReadOnlyList<string> Errors
);
