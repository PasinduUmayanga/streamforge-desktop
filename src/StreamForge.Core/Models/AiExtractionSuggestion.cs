namespace StreamForge.Core.Models;

public sealed class AiExtractionSuggestion
{
    public int ObservationId { get; init; }

    public string JsonPointer { get; init; } = string.Empty;

    public MediaSourceType ExpectedType { get; init; }

    public double Confidence { get; init; }

    public AnalysisFailureReason FailureReason { get; init; } = AnalysisFailureReason.NoSupportedMedia;

    public string Explanation { get; init; } = string.Empty;
}
