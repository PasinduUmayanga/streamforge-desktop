using System.Net;
using System.Text.RegularExpressions;

namespace StreamForge.Infrastructure.Extraction;

internal static partial class SeedResponseUrlExtractor
{
    private const int MaximumResponseCharacters = 1_000_000;

    public static IReadOnlyList<Uri> Extract(string responseBody, Uri responseUrl)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return [];
        }

        var normalized = WebUtility.HtmlDecode(responseBody.Length > MaximumResponseCharacters
            ? responseBody[..MaximumResponseCharacters]
            : responseBody)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);

        var results = new Dictionary<string, Uri>(StringComparer.Ordinal);
        AddMatches(AttributeUrlPattern().Matches(normalized), responseUrl, results);
        AddMatches(JsonUrlPattern().Matches(normalized), responseUrl, results);
        return [.. results.Values.Take(20)];
    }

    private static void AddMatches(MatchCollection matches, Uri responseUrl, IDictionary<string, Uri> results)
    {
        foreach (Match match in matches)
        {
            var value = match.Groups["url"].Value;
            if (!Uri.TryCreate(responseUrl, value, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || MediaTypeDetector.ShouldIgnore(uri)
                || uri.GetLeftPart(UriPartial.Path).Equals(
                    responseUrl.GetLeftPart(UriPartial.Path),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.TryAdd(uri.AbsoluteUri, uri);
        }
    }

    [GeneratedRegex("(?:src|data-(?:src|url|file|video|stream|source|embed))\\s*=\\s*[\"'](?<url>(?:https?:)?//[^\"']+|/[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeUrlPattern();

    [GeneratedRegex("[\"'](?:file|url|src|source|stream|manifest|embed|embed_url|player)[\"']\\s*:\\s*[\"'](?<url>(?:https?:)?//[^\"']+|/[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex JsonUrlPattern();
}
