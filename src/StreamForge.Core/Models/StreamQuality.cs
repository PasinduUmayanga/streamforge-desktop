namespace StreamForge.Core.Models;

public sealed class StreamQuality
{
    public string Label { get; init; } = string.Empty;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public long? Bandwidth { get; init; }

    public required Uri Url { get; init; }
}
