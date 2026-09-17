namespace StreamForge.Core.Models;

public sealed class NetworkActivity
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Category { get; init; } = "NETWORK";

    public required string Message { get; init; }

    public string? DisplayUrl { get; init; }

    public int? StatusCode { get; init; }

    public bool IsImportant { get; init; }

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    public string StatusDisplay => StatusCode?.ToString() ?? string.Empty;
}
