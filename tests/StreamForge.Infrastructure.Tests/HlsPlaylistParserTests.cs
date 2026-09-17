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

    [Fact]
    public void ParseMasterPlaylist_CarriesSignedMasterQueryToRelativeVariantUrls()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080
            1080/index.m3u8
            """;

        var qualities = HlsPlaylistParser.ParseMasterPlaylist(
            playlist,
            new Uri("https://cdn.example.com/video/master.m3u8?md5=abc&expires=123"));

        Assert.Equal(
            "https://cdn.example.com/video/1080/index.m3u8?md5=abc&expires=123",
            qualities.Single(q => q.Label == "1080p").Url.AbsoluteUri);
    }
}
