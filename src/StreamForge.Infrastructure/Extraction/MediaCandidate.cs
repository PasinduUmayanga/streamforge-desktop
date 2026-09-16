using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

internal enum MediaCandidateSource
{
    NetworkUrl,
    ResponseContentType,
    VideoElement,
    JwPlayer
}

internal sealed class MediaCandidate
{
    public required Uri Url { get; init; }

    public required MediaSourceType Type { get; init; }

    public MediaCandidateSource Source { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    public string? ResourceType { get; init; }

    public string? ContentType { get; init; }

    public int? Status { get; init; }

    public bool IsMasterPlaylist { get; init; }

    public string? FrameUrl { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}
