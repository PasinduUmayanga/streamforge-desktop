namespace StreamForge.Core.Models;

public sealed class ExtractionDiagnostics
{
    public IReadOnlyList<SanitizedResponseObservation> Responses { get; init; } = [];

    public AnalysisFailureReason FailureReason { get; init; } = AnalysisFailureReason.NoSupportedMedia;

    public bool BlobPlayerObserved { get; init; }

    public bool TimedOut { get; init; }
}

public sealed class SanitizedResponseObservation
{
    public int Id { get; init; }

    public required string Endpoint { get; init; }

    public string Method { get; init; } = "GET";

    public int StatusCode { get; init; }

    public string? ResourceType { get; init; }

    public string? ContentType { get; init; }

    public IReadOnlyList<SanitizedResponseField> Fields { get; init; } = [];
}

public sealed class SanitizedResponseField
{
    public required string JsonPointer { get; init; }

    public required string ValueType { get; init; }

    public string? UrlShape { get; init; }
}
