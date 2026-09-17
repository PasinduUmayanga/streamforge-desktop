using StreamForge.Infrastructure.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;
using System.Text.Json;

namespace StreamForge.Infrastructure.Tests;

public sealed class ExtractionTests
{
    [Fact]
    public async Task ExtractAsync_UsesDirectSignedHlsUrlWithoutBrowserPageAnalysis()
    {
        var extractor = new PlaywrightStreamExtractor(
            NullLogger<PlaywrightStreamExtractor>.Instance,
            new NoopAiExtractionAdvisor());
        var signedUrl = new Uri("https://cdn.example.com/cdn/hls/abc/master.m3u8?md5=secret&expires=1789643664");
        var steps = new List<IdentificationStepUpdate>();
        var activities = new List<NetworkActivity>();

        var result = await extractor.ExtractAsync(
            signedUrl,
            new StreamExtractionOptions(),
            new Progress<NetworkActivity>(activities.Add),
            new Progress<IdentificationStepUpdate>(steps.Add),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(MediaSourceType.Hls, result.Stream!.Type);
        Assert.Equal(signedUrl.AbsoluteUri, result.Stream.Url.AbsoluteUri);
        Assert.Contains(steps, step =>
            step.Key == IdentificationStepKeys.SelectCandidate
            && step.Status == IdentificationStepStatus.Success);
        Assert.Contains(activities, activity => activity.Category == "DIRECT");
    }

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

    [Fact]
    public void NetworkUrlSanitizer_RedactsQueryValuesButKeepsNames()
    {
        var result = NetworkUrlSanitizer.ForDisplay(
            new Uri("https://cdn.example.com/master.m3u8?token=very-secret&quality=1080"));

        Assert.Equal(
            "https://cdn.example.com/master.m3u8?token=<redacted>&quality=<redacted>",
            result);
        Assert.DoesNotContain("very-secret", result);
    }

    [Fact]
    public void EmbeddedMediaUrlExtractor_FindsJsonEscapedAndEncodedMediaUrls()
    {
        const string response = """
            {
              "file": "https:\/\/cdn.example.com\/video\/master.m3u8?token=abc",
              "fallback": "https%3A%2F%2Fcdn.example.com%2Fvideo%2Ffallback.mp4%3Ftoken%3Dxyz"
            }
            """;

        var results = EmbeddedMediaUrlExtractor.Extract(
            response,
            new Uri("https://player.example.com/api/source"));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, uri => uri.AbsolutePath.EndsWith("master.m3u8", StringComparison.Ordinal));
        Assert.Contains(results, uri => uri.AbsolutePath.EndsWith("fallback.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedMediaUrlExtractor_IgnoresNonMediaUrls()
    {
        var results = EmbeddedMediaUrlExtractor.Extract(
            "{\"poster\":\"https://cdn.example.com/poster.jpg\"}",
            new Uri("https://player.example.com/api/source"));

        Assert.Empty(results);
    }

    [Fact]
    public void PlayerSeedExtractor_ParsesAndDeduplicatesPlayerLinks()
    {
        using var document = JsonDocument.Parse("""
            [
              { "url": "https://player.example.com/embed/123", "source": "EmbedElement" },
              { "url": "https://player.example.com/embed/123", "source": "PlayerMetadata" },
              { "url": "https://cdn.example.com/playback?id=42", "source": "PlayerMetadata" },
              { "url": "https://cdn.example.com/poster.jpg", "source": "PlayerMetadata" },
              { "url": "blob:https://player.example.com/id", "source": "PlayerElement" }
            ]
            """);

        var results = PlayerSeedExtractor.Parse(document.RootElement);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, seed => seed.Source == PlayerSeedSource.EmbedElement);
        Assert.Contains(results, seed => seed.Url.AbsolutePath == "/playback");
    }

    [Fact]
    public void SeedResponseUrlExtractor_FindsNestedEmbedAndApiLinks()
    {
        const string response = """
            <iframe src="/embed/server-2"></iframe>
            <img src="/images/poster.jpg">
            <script>window.player = { "embed_url": "https:\/\/player.example.net\/watch\/abc" };</script>
            """;

        var results = SeedResponseUrlExtractor.Extract(
            response,
            new Uri("https://page.example.com/api/player"));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, uri => uri.AbsoluteUri == "https://page.example.com/embed/server-2");
        Assert.Contains(results, uri => uri.AbsoluteUri == "https://player.example.net/watch/abc");
        Assert.DoesNotContain(results, uri => uri.AbsolutePath.EndsWith("poster.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public void PlayerSeedCollector_DeduplicatesExactSeedLinks()
    {
        var collector = new PlayerSeedCollector();
        var seed = new PlayerSeedLink
        {
            Url = new Uri("https://player.example.com/embed/123?server=one"),
            Source = PlayerSeedSource.EmbedElement
        };

        Assert.True(collector.Add(seed));
        Assert.False(collector.Add(seed));
        Assert.Single(collector.Snapshot());
    }

    [Fact]
    public void KnownPlayerSourceExtractor_ParsesAndDeduplicatesSupportedSources()
    {
        using var document = JsonDocument.Parse("""
            [
              { "player": "Video.js", "url": "https://cdn.example.com/master.m3u8?token=abc" },
              { "player": "Duplicate", "url": "https://cdn.example.com/master.m3u8?token=abc" },
              { "player": "Shaka Player", "url": "https://cdn.example.com/manifest.mpd" },
              { "player": "Fluid Player", "url": "https://cdn.example.com/fluid/master.m3u8" },
              { "player": "MediaElement.js", "url": "https://cdn.example.com/mediaelement/video.mp4" },
              { "player": "OpenPlayerJS", "url": "https://cdn.example.com/openplayer/master.m3u8" },
              { "player": "ArtPlayer", "url": "https://cdn.example.com/artplayer/master.m3u8" },
              { "player": "DPlayer", "url": "https://cdn.example.com/dplayer/master.m3u8" },
              { "player": "Plyr", "url": "blob:https://example.com/not-downloadable" },
              { "player": "Invalid", "url": "https://cdn.example.com/poster.jpg" }
            ]
            """);

        var results = KnownPlayerSourceExtractor.Parse(document.RootElement);

        Assert.Equal(7, results.Count);
        Assert.Contains(results, source => source.Player == "Video.js" && source.Url.AbsolutePath.EndsWith("master.m3u8"));
        Assert.Contains(results, source => source.Player == "Shaka Player" && source.Url.AbsolutePath.EndsWith("manifest.mpd"));
        Assert.Contains(results, source => source.Player == "Fluid Player");
        Assert.Contains(results, source => source.Player == "MediaElement.js");
        Assert.Contains(results, source => source.Player == "OpenPlayerJS");
        Assert.Contains(results, source => source.Player == "ArtPlayer");
        Assert.Contains(results, source => source.Player == "DPlayer");
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
    public void MediaTypeDetector_DoesNotTreatBlobPlaybackUrlAsDownloadableMedia()
    {
        var blobUrl = new Uri("blob:https://player.example.com/348fe413-0bbd-467e-8c62-f699f8588a63");

        Assert.Equal(MediaSourceType.Unknown, MediaTypeDetector.Detect(blobUrl));
    }

    [Fact]
    public void CandidateCollector_TracksBlobBackedPlayerObservation()
    {
        var collector = new MediaCandidateCollector();

        collector.MarkBlobPlayerObserved();

        Assert.True(collector.BlobPlayerObserved);
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
    public void CandidateCollector_PreservesAuthoritativeNetworkHeadersForObservedBlobSource()
    {
        var collector = new MediaCandidateCollector();
        var uri = new Uri("https://cdn.example.com/master.m3u8");
        collector.Add(new MediaCandidate
        {
            Url = uri,
            Type = MediaSourceType.Hls,
            Source = MediaCandidateSource.ResponseContentType,
            Headers = new Dictionary<string, string> { ["referer"] = "https://actual.example/player" }
        });
        collector.Add(new MediaCandidate
        {
            Url = uri,
            Type = MediaSourceType.Hls,
            Source = MediaCandidateSource.BrowserObservation,
            Headers = new Dictionary<string, string> { ["referer"] = "https://fallback.example/frame" }
        });

        var result = Assert.Single(collector.Snapshot());

        Assert.Equal("https://actual.example/player", result.Headers["referer"]);
        Assert.Equal(MediaCandidateSource.BrowserObservation, result.Source);
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
    public void CandidateRanker_PrefersBrowserObservedMediaOverRawNetworkUrl()
    {
        var network = Candidate(
            new Uri("https://cdn.example.com/preload.mp4"),
            MediaSourceType.Mp4,
            MediaCandidateSource.NetworkUrl);
        var observed = Candidate(
            new Uri("https://cdn.example.com/player-video.mp4"),
            MediaSourceType.Mp4,
            MediaCandidateSource.BrowserObservation);

        Assert.Same(observed, CandidateRanker.ChooseBest([network, observed]));
    }

    [Fact]
    public void CandidateRanker_PrefersKnownPlayerSourceOverRawNetworkUrl()
    {
        var network = Candidate(
            new Uri("https://cdn.example.com/preload.mp4"),
            MediaSourceType.Mp4,
            MediaCandidateSource.NetworkUrl);
        var player = Candidate(
            new Uri("https://cdn.example.com/movie.mp4"),
            MediaSourceType.Mp4,
            MediaCandidateSource.KnownPlayer);

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

    private sealed class NoopAiExtractionAdvisor : IAiExtractionAdvisor
    {
        public Task<AiExtractionSuggestion?> AdviseAsync(
            ExtractionDiagnostics diagnostics,
            StreamExtractionOptions options,
            CancellationToken cancellationToken) => Task.FromResult<AiExtractionSuggestion?>(null);
    }
}
