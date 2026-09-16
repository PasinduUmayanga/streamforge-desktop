using System.Text.RegularExpressions;

namespace StreamForge.Infrastructure.Extraction;

public static partial class MediaUrlScanner
{
    public static IEnumerable<Uri> Scan(string text)
    {
        foreach (Match match in UrlRegex().Matches(text))
        {
            if (Uri.TryCreate(match.Value.Replace("\\u0026", "&"), UriKind.Absolute, out var uri)
                && MediaTypeDetector.IsCandidate(uri)
                && !MediaTypeDetector.ShouldIgnore(uri))
            {
                yield return uri;
            }
        }
    }

    [GeneratedRegex(@"https?:\/\/[^\s""'<>\\]+(?:\.m3u8|\.mpd|\.mp4)[^\s""'<>\\]*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
