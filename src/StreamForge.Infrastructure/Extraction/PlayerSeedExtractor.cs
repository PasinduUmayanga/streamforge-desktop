using System.Text.Json;
using Microsoft.Playwright;

namespace StreamForge.Infrastructure.Extraction;

internal sealed class PlayerSeedExtractor
{
    private const string DiscoveryScript = """
        () => {
            const results = [];
            const seen = new Set();
            const contextPattern = /(player|video|stream|server|embed|episode|source|quality)/i;
            const staticAssetPattern = /\.(?:jpe?g|png|gif|svg|css|js|woff2?)(?:$|[?#])/i;

            const add = (value, source) => {
                if (typeof value !== "string" || !value.trim()) return;
                try {
                    const url = new URL(value.replace(/\\\//g, "/"), document.baseURI);
                    if (!/^https?:$/.test(url.protocol)
                        || staticAssetPattern.test(url.href)
                        || seen.has(url.href)
                        || url.href === document.URL) return;
                    seen.add(url.href);
                    results.push({ url: url.href, source });
                } catch {}
            };

            for (const element of document.querySelectorAll("iframe[src], video[src], video source[src]")) {
                const source = element.tagName === "IFRAME" ? "EmbedElement" : "PlayerElement";
                add(element.getAttribute("src"), source);
            }

            const metadataAttributes = [
                "data-file", "data-video", "data-stream", "data-manifest", "data-source",
                "data-embed", "data-embed-url", "data-player", "data-url", "data-src"
            ];
            for (const attribute of metadataAttributes) {
                for (const element of document.querySelectorAll(`[${attribute}]`)) {
                    const context = `${element.id || ""} ${element.className || ""} ${element.parentElement?.id || ""} ${element.parentElement?.className || ""}`;
                    if ((attribute === "data-url" || attribute === "data-src")
                        && !contextPattern.test(context)) continue;
                    add(element.getAttribute(attribute), "PlayerMetadata");
                }
            }

            return results.slice(0, 40);
        }
        """;

    public async Task<IReadOnlyList<DiscoveredPlayerSeed>> ExtractAsync(IFrame frame)
    {
        var result = await frame.EvaluateAsync<JsonElement>(DiscoveryScript);
        return Parse(result);
    }

    internal static IReadOnlyList<DiscoveredPlayerSeed> Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new Dictionary<string, DiscoveredPlayerSeed>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || MediaTypeDetector.ShouldIgnore(uri))
            {
                continue;
            }

            var sourceText = item.TryGetProperty("source", out var sourceElement)
                ? sourceElement.GetString()
                : null;
            Enum.TryParse(sourceText, ignoreCase: true, out PlayerSeedSource source);
            results.TryAdd(uri.AbsoluteUri, new DiscoveredPlayerSeed(uri, source));
        }

        return [.. results.Values];
    }
}

internal sealed record DiscoveredPlayerSeed(Uri Url, PlayerSeedSource Source);
