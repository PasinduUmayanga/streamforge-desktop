using StreamForge.Infrastructure.Extraction;
using StreamForge.Core.Models;

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

    [Theory]
    [InlineData("application/vnd.apple.mpegurl", MediaSourceType.Hls)]
    [InlineData("application/x-mpegURL; charset=utf-8", MediaSourceType.Hls)]
    [InlineData("application/dash+xml", MediaSourceType.Dash)]
    [InlineData("video/mp4", MediaSourceType.Mp4)]
    public void MediaTypeDetector_DetectsExtensionlessResponses(string contentType, MediaSourceType expected)
    {
        var result = MediaTypeDetector.Detect(
            new Uri("https://cdn.example.com/playback?id=123&token=signed"),
            contentType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MediaTypeDetector_DoesNotTreatQueryFileNamesAsStaticAssets()
    {
        var uri = new Uri("https://cdn.example.com/master.m3u8?poster=cover.jpg");

        Assert.False(MediaTypeDetector.ShouldIgnore(uri));
        Assert.Equal(MediaSourceType.Hls, MediaTypeDetector.Detect(uri));
    }

    [Fact]
    public void CandidateCollector_DeduplicatesAndEnrichesTheExactSignedUrl()
    {
        var collector = new MediaCandidateCollector();
        var uri = new Uri("https://cdn.example.com/playback?id=123&token=abc%2Bdef");

        collector.Add(Candidate(uri, MediaSourceType.Hls, MediaCandidateSource.NetworkUrl));
        collector.Add(new MediaCandidate
        {
            Url = uri,
            Type = MediaSourceType.Hls,
            Source = MediaCandidateSource.ResponseContentType,
            Status = 200,
            ContentType = "application/vnd.apple.mpegurl",
            IsMasterPlaylist = true
        });

        var result = Assert.Single(collector.Snapshot());
        Assert.Equal(uri.AbsoluteUri, result.Url.AbsoluteUri);
        Assert.Equal(200, result.Status);
        Assert.True(result.IsMasterPlaylist);
    }

    [Fact]
    public void CandidateRanker_PrefersMasterHlsOverDashAndMp4()
    {
        var mp4 = Candidate(new Uri("https://cdn.example.com/video.mp4"), MediaSourceType.Mp4);
        var dash = Candidate(new Uri("https://cdn.example.com/manifest.mpd"), MediaSourceType.Dash);
        var hls = new MediaCandidate
        {
            Url = new Uri("https://cdn.example.com/master.m3u8"),
            Type = MediaSourceType.Hls,
            Source = MediaCandidateSource.ResponseContentType,
            IsMasterPlaylist = true,
            Status = 200
        };

        Assert.Same(hls, CandidateRanker.ChooseBest([mp4, dash, hls]));
    }

    [Fact]
    public void CandidateRanker_PrefersPlayerSourceForSameMediaType()
    {
        var network = Candidate(
            new Uri("https://cdn.example.com/preload.mp4"),
            MediaSourceType.Mp4,
            MediaCandidateSource.NetworkUrl);
        var player = Candidate(
            new Uri("https://cdn.example.com/movie.mp4"),
            MediaSourceType.Mp4,
            MediaCandidateSource.VideoElement);

        Assert.Same(player, CandidateRanker.ChooseBest([network, player]));
    }

    [Fact]
    public void CandidateRanker_PenalizesKnownAdvertisementPaths()
    {
        var advertisement = new MediaCandidate
        {
            Url = new Uri("https://ads.example.com/vast/master.m3u8"),
            Type = MediaSourceType.Hls,
            Source = MediaCandidateSource.ResponseContentType,
            IsMasterPlaylist = true,
            Status = 200
        };
        var video = Candidate(
            new Uri("https://cdn.example.com/movie.mp4"),
            MediaSourceType.Mp4,
            MediaCandidateSource.VideoElement);

        Assert.Same(video, CandidateRanker.ChooseBest([advertisement, video]));
    }

    private static MediaCandidate Candidate(
        Uri uri,
        MediaSourceType type,
        MediaCandidateSource source = MediaCandidateSource.ResponseContentType)
    {
        return new MediaCandidate
        {
            Url = uri,
            Type = type,
            Source = source,
            Status = 200
        };
    }
}
