using StreamForge.Infrastructure.Extraction;

namespace StreamForge.Infrastructure.Tests;

public sealed class ExtractionTests
{
    [Fact]
    public void HeaderSanitizer_RemovesSensitiveHeaders()
    {
        var filtered = HeaderSanitizer.Filter(new Dictionary<string, string>
        {
            ["Authorization"] = "secret",
            ["Cookie"] = "secret",
            ["User-Agent"] = "agent"
        });

        Assert.DoesNotContain("Authorization", filtered.Keys);
        Assert.DoesNotContain("Cookie", filtered.Keys);
        Assert.Equal("agent", filtered["User-Agent"]);
    }

    [Theory]
    [InlineData("https://cdn.example.com/master.m3u8")]
    [InlineData("https://cdn.example.com/manifest.mpd")]
    [InlineData("https://cdn.example.com/video.mp4")]
    public void MediaTypeDetector_DetectsSupportedTypes(string url)
    {
        Assert.NotEqual(Core.Models.MediaSourceType.Unknown, MediaTypeDetector.Detect(new Uri(url)));
    }
}
