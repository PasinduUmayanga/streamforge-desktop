namespace StreamForge.Core.Models;

public sealed class DownloadProgress
{
    public double? Percentage { get; init; }

    public long BytesDownloaded { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? Speed { get; init; }

    public double? BytesPerSecond { get; init; }

    public TimeSpan? EstimatedTimeRemaining { get; init; }

    public string? CurrentTime { get; init; }
}
