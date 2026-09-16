namespace StreamForge.Core.Models;

public sealed class DownloadRequest
{
    public required MediaStream Stream { get; init; }

    public StreamQuality? Quality { get; init; }

    public required string OutputPath { get; init; }
}
