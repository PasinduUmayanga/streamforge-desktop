using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

public static class MediaTypeDetector
{
    private static readonly string[] IgnoredHosts =
    [
        "doubleclick.net",
        "googletagmanager.com",
        "google-analytics.com"
    ];

    public static MediaSourceType Detect(Uri uri)
    {
        return Detect(uri, null);
    }

    public static MediaSourceType Detect(Uri uri, string? contentType)
    {
        var text = uri.AbsolutePath;

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

        return DetectContentType(contentType);
    }

    public static bool IsCandidate(Uri uri) => Detect(uri) != MediaSourceType.Unknown;

    public static bool IsCandidate(Uri uri, string? contentType) =>
        Detect(uri, contentType) != MediaSourceType.Unknown;

    public static MediaSourceType DetectContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return MediaSourceType.Unknown;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        if (mediaType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("audio/x-mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceType.Hls;
        }

        if (mediaType.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceType.Dash;
        }

        if (mediaType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceType.Mp4;
        }

        return MediaSourceType.Unknown;
    }

    public static bool ShouldIgnore(Uri uri)
    {
        var host = uri.Host;
        if (IgnoredHosts.Any(ignored =>
                host.Equals(ignored, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith($".{ignored}", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var path = uri.AbsolutePath;
        string[] ignoredExtensions =
        [
            ".jpg", ".jpeg", ".png", ".css", ".js", ".gif", ".svg"
        ];

        return ignoredExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLikelyAdvertisement(Uri uri)
    {
        var hostAndPath = $"{uri.Host}{uri.AbsolutePath}";
        string[] indicators =
        [
            "doubleclick",
            "googlesyndication",
            "/advert/",
            "/advertising/",
            "/preroll/",
            "/midroll/",
            "/vast/"
        ];

        return indicators.Any(indicator => hostAndPath.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }
}
