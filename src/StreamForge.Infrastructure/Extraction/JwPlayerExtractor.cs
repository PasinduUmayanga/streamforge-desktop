using Microsoft.Playwright;
namespace StreamForge.Infrastructure.Extraction;

public sealed class JwPlayerExtractor
{
    public async Task<IReadOnlyList<Uri>> ExtractUrlsAsync(IFrame frame)
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

        var playlist = await frame.EvaluateAsync<object?>(script);
        if (playlist is null)
        {
            return [];
        }

        var json = System.Text.Json.JsonSerializer.Serialize(playlist);
        return MediaUrlScanner.Scan(json).DistinctBy(url => url.AbsoluteUri).ToArray();
    }
}
