using StreamForge.Infrastructure.Streaming;

namespace StreamForge.Infrastructure.Tests;

public sealed class HlsPlaylistParserTests
{
    [Fact]
    public void ParseMasterPlaylist_ReturnsAutoAndVariantQualities()
    {
        var playlist = File.ReadAllText(Path.Combine("Fixtures", "master.m3u8"));
        var qualities = HlsPlaylistParser.ParseMasterPlaylist(playlist, new Uri("https://cdn.example.com/video/master.m3u8"));

        Assert.Equal(["Auto", "1080p", "720p", "480p"], qualities.Select(q => q.Label));
    }

    [Fact]
    public void ParseMasterPlaylist_ResolvesRelativeUrls()
    {
        var playlist = File.ReadAllText(Path.Combine("Fixtures", "master.m3u8"));
        var qualities = HlsPlaylistParser.ParseMasterPlaylist(playlist, new Uri("https://cdn.example.com/video/master.m3u8"));

        Assert.Equal("https://cdn.example.com/video/1080/index.m3u8", qualities.Single(q => q.Label == "1080p").Url.AbsoluteUri);
    }
}
