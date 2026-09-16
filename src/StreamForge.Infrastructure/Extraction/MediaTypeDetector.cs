using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

public static class MediaTypeDetector
{
    public static MediaSourceType Detect(Uri uri)
    {
        var text = uri.AbsoluteUri;

        if (text.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceType.Hls;
        }

        if (text.Contains(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceType.Dash;
        }

        if (text.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceType.Mp4;
        }

        return MediaSourceType.Unknown;
    }

    public static bool IsCandidate(Uri uri) => Detect(uri) != MediaSourceType.Unknown;

    public static bool ShouldIgnore(Uri uri)
    {
        var value = uri.AbsoluteUri.ToLowerInvariant();
        string[] ignored =
        [
            ".jpg", ".jpeg", ".png", ".css", ".js", ".gif", ".svg",
            "analytics", "ads", "doubleclick", "googletagmanager"
        ];

        return ignored.Any(value.Contains);
    }
}
