using System.Text.RegularExpressions;

namespace StreamForge.Infrastructure.Extraction;

internal static partial class EmbeddedMediaUrlExtractor
{
    private const int MaximumResponseCharacters = 1_000_000;

    public static IReadOnlyList<Uri> Extract(string responseBody, Uri responseUrl)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return [];
        }

        var normalized = responseBody.Length > MaximumResponseCharacters
            ? responseBody[..MaximumResponseCharacters]
            : responseBody;
        normalized = normalized
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);

        var results = new Dictionary<string, Uri>(StringComparer.Ordinal);
        AddMatches(AbsoluteUrlPattern().Matches(normalized), responseUrl, results, decode: false);
        AddMatches(EncodedAbsoluteUrlPattern().Matches(normalized), responseUrl, results, decode: true);
        return [.. results.Values];
    }

    private static void AddMatches(
        MatchCollection matches,
        Uri responseUrl,
        IDictionary<string, Uri> results,
        bool decode)
    {
        foreach (Match match in matches)
        {
            var value = match.Value.TrimEnd(')', ']', '}', ',', ';');
            if (decode)
            {
                value = Uri.UnescapeDataString(value);
            }

            if (!Uri.TryCreate(responseUrl, value, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || MediaTypeDetector.Detect(uri) == Core.Models.MediaSourceType.Unknown)
            {
                continue;
            }

            results.TryAdd(uri.AbsoluteUri, uri);
        }
    }

    [GeneratedRegex("https?://[^\\s\\\"'<>]+?\\.(?:m3u8|mpd|mp4)(?:[?#][^\\s\\\"'<>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlPattern();

    [GeneratedRegex("https?%3A%2F%2F[^\\s\\\"'<>]+?\\.(?:m3u8|mpd|mp4)(?:%3F[^\\s\\\"'<>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedAbsoluteUrlPattern();
}
