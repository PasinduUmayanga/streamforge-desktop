namespace StreamForge.Core.Models;

public sealed class StreamExtractionResult
{
    public MediaStream? Stream { get; init; }

    public AnalysisFailureReason FailureReason { get; init; }

    public required string Message { get; init; }

    public bool AiAdvisorUsed { get; init; }

    public string? AiDiagnostic { get; init; }

    public bool Succeeded => Stream is not null;
}
