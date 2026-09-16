using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

public sealed class PlaywrightStreamExtractor(ILogger<PlaywrightStreamExtractor> logger) : IStreamExtractor
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumObservationTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NetworkSettleTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InspectionInterval = TimeSpan.FromSeconds(1);
    private readonly JwPlayerExtractor _jwPlayerExtractor = new();

    public async Task<MediaStream?> ExtractAsync(Uri pageUrl, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ExtractionTimeout);

        var candidates = new MediaCandidateCollector();
        var responseTasks = new ConcurrentBag<Task>();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        await using var context = await browser.NewContextAsync();

        context.Request += (_, request) => CaptureRequest(request, candidates);
        context.Response += (_, response) =>
        {
            var responseTask = CaptureResponseAsync(response, candidates);
            responseTasks.Add(responseTask);
        };

        var page = await context.NewPageAsync();
        logger.LogInformation("Page analysis started for {PageHost}", pageUrl.Host);

        try
        {
            await page.GotoAsync(pageUrl.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)NavigationTimeout.TotalMilliseconds
            });
        }
        catch (PlaywrightException exception) when (
            exception.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Page navigation timed out; inspecting requests captured during loading.");
        }

        var observationStarted = DateTimeOffset.UtcNow;
        var nextInspection = DateTimeOffset.MinValue;

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextInspection)
                {
                    await InspectPlayersAsync(context, candidates);
                    nextInspection = now + InspectionInterval;
                }

                var lastCandidateAt = candidates.LastCandidateAt;
                var observedLongEnough = now - observationStarted >= MinimumObservationTime;
                var networkSettled = lastCandidateAt is not null && now - lastCandidateAt >= NetworkSettleTime;

                if (candidates.Count > 0 && observedLongEnough && networkSettled)
                {
                    break;
                }

                await Task.Delay(250, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The extraction timeout is an expected no-result condition.
        }

        cancellationToken.ThrowIfCancellationRequested();
        await AwaitResponseTasksAsync(responseTasks, cancellationToken);

        var best = CandidateRanker.ChooseBest(candidates.Snapshot());
        if (best is null)
        {
            logger.LogInformation("No supported media stream was detected.");
            return null;
        }

        logger.LogInformation(
            "Detected media type {MediaType} from {DetectionSource}",
            best.Type,
            best.Source);

        return CreateMediaStream(best);
    }

    private static void CaptureRequest(IRequest request, MediaCandidateCollector candidates)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri)
            || MediaTypeDetector.ShouldIgnore(requestUri))
        {
            return;
        }

        var type = MediaTypeDetector.Detect(requestUri);
        if (type == MediaSourceType.Unknown)
        {
            return;
        }

        candidates.Add(new MediaCandidate
        {
            Url = requestUri,
            Type = type,
            Source = MediaCandidateSource.NetworkUrl,
            Headers = HeaderSanitizer.Filter(request.Headers),
            ResourceType = request.ResourceType,
            FrameUrl = request.Frame.Url
        });
    }

    private static async Task CaptureResponseAsync(IResponse response, MediaCandidateCollector candidates)
    {
        try
        {
            if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri)
                || MediaTypeDetector.ShouldIgnore(responseUri))
            {
                return;
            }

            var contentType = GetHeader(response.Headers, "content-type");
            var type = MediaTypeDetector.Detect(responseUri, contentType);
            if (type == MediaSourceType.Unknown)
            {
                return;
            }

            var allRequestHeaders = await response.Request.AllHeadersAsync();
            var isMasterPlaylist = false;
            if (type == MediaSourceType.Hls && response.Ok)
            {
                try
                {
                    var manifest = await response.TextAsync();
                    isMasterPlaylist = manifest.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
                }
                catch (PlaywrightException)
                {
                    // A redirect or closed response can prevent body access. URL and MIME detection still apply.
                }
            }

            candidates.Add(new MediaCandidate
            {
                Url = responseUri,
                Type = type,
                Source = MediaTypeDetector.DetectContentType(contentType) == MediaSourceType.Unknown
                    ? MediaCandidateSource.NetworkUrl
                    : MediaCandidateSource.ResponseContentType,
                Headers = HeaderSanitizer.Filter(allRequestHeaders),
                ResourceType = response.Request.ResourceType,
                ContentType = contentType,
                Status = response.Status,
                IsMasterPlaylist = isMasterPlaylist,
                FrameUrl = response.Frame.Url
            });
        }
        catch (PlaywrightException)
        {
            // The page or response may disappear while an asynchronous player is changing sources.
        }
    }

    private async Task InspectPlayersAsync(IBrowserContext context, MediaCandidateCollector candidates)
    {
        foreach (var page in context.Pages)
        {
            foreach (var frame in page.Frames)
            {
                await InspectFrameAsync(frame, candidates);
            }
        }
    }

    private async Task InspectFrameAsync(IFrame frame, MediaCandidateCollector candidates)
    {
        if (frame.IsDetached)
        {
            return;
        }

        try
        {
            var playerState = await frame.EvaluateAsync<JsonElement>("""
                () => {
                    const urls = [];

                    for (const video of document.querySelectorAll("video")) {
                        if (video.currentSrc) urls.push(video.currentSrc);
                        if (video.src) urls.push(video.src);

                        for (const source of video.querySelectorAll("source")) {
                            if (source.src) urls.push(source.src);
                        }

                        video.muted = true;
                        video.play().catch(() => {});
                    }

                    try {
                        if (typeof jwplayer !== "undefined") {
                            jwplayer().play(true);
                        }
                    } catch {}

                    return {
                        urls: [...new Set(urls)],
                        userAgent: navigator.userAgent
                    };
                }
                """);

            var userAgent = playerState.TryGetProperty("userAgent", out var userAgentElement)
                ? userAgentElement.GetString()
                : null;
            var frameHeaders = CreateFrameHeaders(frame.Url, userAgent);

            if (playerState.TryGetProperty("urls", out var urlsElement))
            {
                foreach (var urlElement in urlsElement.EnumerateArray())
                {
                    var url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        AddPlayerCandidate(
                            url,
                            frame.Url,
                            frameHeaders,
                            MediaCandidateSource.VideoElement,
                            candidates);
                    }
                }
            }

            foreach (var url in await _jwPlayerExtractor.ExtractUrlsAsync(frame))
            {
                AddPlayerCandidate(url.AbsoluteUri, frame.Url, frameHeaders, MediaCandidateSource.JwPlayer, candidates);
            }
        }
        catch (PlaywrightException)
        {
            // Frames can navigate or detach while their player is being inspected.
        }
    }

    private static void AddPlayerCandidate(
        string url,
        string frameUrl,
        IReadOnlyDictionary<string, string> headers,
        MediaCandidateSource source,
        MediaCandidateCollector candidates)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var mediaUri))
        {
            return;
        }

        var type = MediaTypeDetector.Detect(mediaUri);
        if (type == MediaSourceType.Unknown)
        {
            return;
        }

        candidates.Add(new MediaCandidate
        {
            Url = mediaUri,
            Type = type,
            Source = source,
            Headers = headers,
            FrameUrl = frameUrl
        });
    }

    private static IReadOnlyDictionary<string, string> CreateFrameHeaders(string frameUrl, string? userAgent)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(frameUrl, UriKind.Absolute, out var frameUri))
        {
            headers["referer"] = frameUri.AbsoluteUri;
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            headers["user-agent"] = userAgent;
        }

        return headers;
    }

    private static MediaStream CreateMediaStream(MediaCandidate candidate)
    {
        var headers = HeaderSanitizer.Filter(candidate.Headers);
        return new MediaStream
        {
            Url = candidate.Url,
            Type = candidate.Type,
            Referer = GetHeader(headers, "referer") ?? candidate.FrameUrl,
            Origin = GetHeader(headers, "origin"),
            UserAgent = GetHeader(headers, "user-agent"),
            Headers = headers
        };
    }

    private static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static async Task AwaitResponseTasksAsync(
        ConcurrentBag<Task> responseTasks,
        CancellationToken cancellationToken)
    {
        var tasks = responseTasks.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            // Long-lived responses must not keep analysis open beyond the observation window.
        }
    }

}
