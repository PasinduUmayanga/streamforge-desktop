namespace StreamForge.Core.Models;

public sealed class MediaStream
{
    public required Uri Url { get; init; }

    public MediaSourceType Type { get; init; }

    public string? Referer { get; init; }

    public string? Origin { get; init; }

    public string? UserAgent { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    public IReadOnlyList<StreamQuality> Qualities { get; init; }
        = [];
}
