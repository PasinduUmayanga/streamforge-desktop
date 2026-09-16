using Microsoft.Playwright;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

public sealed class JwPlayerExtractor
{
    public async Task<MediaStream?> ExtractAsync(IPage page, IReadOnlyDictionary<string, string> headers)
    {
        const string script = """
            () => {
                try {
                    if (typeof jwplayer === "undefined") {
                        return null;
                    }

                    const player = jwplayer();

                    if (!player) {
                        return null;
                    }

                    return player.getPlaylist();
                } catch {
                    return null;
                }
            }
            """;

        var playlist = await page.EvaluateAsync<object?>(script);
        if (playlist is null)
        {
            return null;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(playlist);
        foreach (var url in MediaUrlScanner.Scan(json))
        {
            var type = MediaTypeDetector.Detect(url);
            if (type != MediaSourceType.Unknown)
            {
                return new MediaStream
                {
                    Url = url,
                    Type = type,
                    Headers = HeaderSanitizer.Filter(headers)
                };
            }
        }

        return null;
    }
}
