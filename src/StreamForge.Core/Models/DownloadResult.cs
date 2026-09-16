namespace StreamForge.Core.Models;

public sealed class DownloadResult
{
    public bool Succeeded { get; init; }

    public bool Cancelled { get; init; }

    public int? ExitCode { get; init; }

    public string? ErrorMessage { get; init; }
}
