using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

public sealed class PlaywrightStreamExtractor(ILogger<PlaywrightStreamExtractor> logger) : IStreamExtractor
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);
    private readonly JwPlayerExtractor _jwPlayerExtractor = new();

    public async Task<MediaStream?> ExtractAsync(Uri pageUrl, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ExtractionTimeout);

        var candidates = new List<MediaStream>();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        page.Request += (_, request) =>
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri)
                || MediaTypeDetector.ShouldIgnore(requestUri)
                || !MediaTypeDetector.IsCandidate(requestUri))
            {
                return;
            }

            var headers = HeaderSanitizer.Filter(request.Headers);
            candidates.Add(new MediaStream
            {
                Url = requestUri,
                Type = MediaTypeDetector.Detect(requestUri),
                Referer = headers.TryGetValue("referer", out var referer) ? referer : null,
                Origin = headers.TryGetValue("origin", out var origin) ? origin : null,
                UserAgent = headers.TryGetValue("user-agent", out var userAgent) ? userAgent : null,
                Headers = headers
            });
        };

        logger.LogInformation("Page analysis started for {PageHost}", pageUrl.Host);

        await page.GotoAsync(pageUrl.AbsoluteUri, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = (float)ExtractionTimeout.TotalMilliseconds
        });

        await TryStartPlaybackAsync(page);

        var stopAt = DateTimeOffset.UtcNow + ExtractionTimeout;
        while (!timeoutCts.IsCancellationRequested && DateTimeOffset.UtcNow < stopAt)
        {
            var best = ChooseBest(candidates);
            if (best is not null)
            {
                logger.LogInformation("Detected media type {MediaType}", best.Type);
                return best;
            }

            var jw = await _jwPlayerExtractor.ExtractAsync(page, new Dictionary<string, string>());
            if (jw is not null)
            {
                logger.LogInformation("Detected media type {MediaType} via JW Player", jw.Type);
                return jw;
            }

            await page.WaitForTimeoutAsync(500);
        }

        logger.LogInformation("No supported media stream was detected.");
        return null;
    }

    private static MediaStream? ChooseBest(IReadOnlyCollection<MediaStream> candidates)
    {
        return candidates.FirstOrDefault(x => x.Type == MediaSourceType.Hls)
            ?? candidates.FirstOrDefault(x => x.Type == MediaSourceType.Dash)
            ?? candidates.FirstOrDefault(x => x.Type == MediaSourceType.Mp4);
    }

    private static async Task TryStartPlaybackAsync(IPage page)
    {
        await page.EvaluateAsync("""
            () => {
                const video = document.querySelector("video");

                if (video) {
                    video.muted = true;
                    video.play().catch(() => {});
                }

                try {
                    if (typeof jwplayer !== "undefined") {
                        jwplayer().play(true);
                    }
                } catch {}
            }
            """);
    }
}
