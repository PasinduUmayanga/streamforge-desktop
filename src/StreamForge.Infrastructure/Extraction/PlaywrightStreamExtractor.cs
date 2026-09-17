using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StreamForge.Core.Exceptions;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

public sealed class PlaywrightStreamExtractor(ILogger<PlaywrightStreamExtractor> logger) : IStreamExtractor
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumObservationTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BlobPlayerMinimumObservationTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan NetworkSettleTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InspectionInterval = TimeSpan.FromSeconds(1);
    private const string DooPlayPlayerActivationScript = """
        async () => {
            if (globalThis.__streamForgeDooPlayAttempted) return [];

            const optionElements = [...document.querySelectorAll(
                ".dooplay_player_option[data-post][data-type][data-nume], #playeroptions [data-post][data-type][data-nume]")];
            if (optionElements.length === 0) return [];

            let playerApi = globalThis.dtAjax?.player_api;
            if (!playerApi) {
                for (const script of document.scripts) {
                    const match = (script.textContent || "").match(
                        /["']player_api["']\s*:\s*["']([^"']+)["']/i);
                    if (match) {
                        playerApi = match[1].replace(/\\\//g, "/");
                        break;
                    }
                }
            }

            let apiUrl;
            try {
                apiUrl = new URL(playerApi, document.baseURI);
            } catch {
                return [];
            }

            if (!/^https?:$/.test(apiUrl.protocol) || apiUrl.origin !== location.origin) {
                return [];
            }

            Object.defineProperty(globalThis, "__streamForgeDooPlayAttempted", {
                value: true,
                configurable: false,
                enumerable: false
            });

            const embeds = [];
            const seenOptions = new Set();
            const apiBase = apiUrl.href.endsWith("/") ? apiUrl.href : `${apiUrl.href}/`;

            for (const element of optionElements.slice(0, 4)) {
                const post = element.dataset.post || "";
                const type = element.dataset.type || "";
                const number = element.dataset.nume || "";
                if (!/^[a-z0-9_-]+$/i.test(post)
                    || !/^[a-z0-9_-]+$/i.test(type)
                    || !/^[a-z0-9_-]+$/i.test(number)) {
                    continue;
                }

                const optionKey = `${post}/${type}/${number}`;
                if (seenOptions.has(optionKey)) continue;
                seenOptions.add(optionKey);

                try {
                    const endpoint = new URL(
                        `${encodeURIComponent(post)}/${encodeURIComponent(type)}/${encodeURIComponent(number)}`,
                        apiBase);
                    const response = await fetch(endpoint.href, {
                        credentials: "include",
                        headers: { Accept: "application/json" }
                    });
                    if (!response.ok) continue;

                    const payload = await response.json();
                    let embedValue = payload?.embed_url || payload?.embed || payload?.url;
                    if (typeof embedValue !== "string" || !embedValue.trim()) continue;

                    const iframeMatch = embedValue.match(/<iframe[^>]+src=["']([^"']+)["']/i);
                    if (iframeMatch) embedValue = iframeMatch[1];

                    const embedUrl = new URL(embedValue, document.baseURI);
                    if (!/^https?:$/.test(embedUrl.protocol)) continue;

                    const iframe = document.createElement("iframe");
                    iframe.src = embedUrl.href;
                    iframe.width = "640";
                    iframe.height = "360";
                    iframe.allow = "autoplay; fullscreen; encrypted-media";
                    iframe.dataset.streamForgePlayer = optionKey;
                    iframe.style.position = "absolute";
                    iframe.style.left = "-10000px";
                    iframe.style.top = "0";
                    document.body.appendChild(iframe);
                    embeds.push(embedUrl.href);
                } catch {}
            }

            return [...new Set(embeds)];
        }
        """;
    private const string MediaRequestCaptureScript = """
        (() => {
            if (globalThis.__streamForgeCaptureInstalled) return;

            Object.defineProperty(globalThis, "__streamForgeCaptureInstalled", {
                value: true,
                configurable: false,
                enumerable: false
            });

            const observed = [];
            Object.defineProperty(globalThis, "__streamForgeObservedMediaRequests", {
                value: observed,
                configurable: false,
                enumerable: false
            });

            const mediaUrlPattern = /\.(m3u8|mpd|mp4)(?:$|[?#])/i;
            const mediaTypePattern = /(?:mpegurl|dash\+xml|video\/mp4)/i;
            const record = (url, contentType, initiatorType) => {
                try {
                    const absoluteUrl = new URL(String(url), document.baseURI).href;
                    const type = contentType || "";
                    if (!mediaUrlPattern.test(absoluteUrl) && !mediaTypePattern.test(type)) return;

                    if (!observed.some(item => item.url === absoluteUrl && item.contentType === type)) {
                        observed.push({ url: absoluteUrl, contentType: type, initiatorType });
                    }
                } catch {}
            };

            const originalFetch = globalThis.fetch;
            if (typeof originalFetch === "function") {
                globalThis.fetch = async function(...args) {
                    const response = await originalFetch.apply(this, args);
                    try {
                        record(response.url, response.headers.get("content-type"), "fetch");
                    } catch {}
                    return response;
                };
            }

            const originalOpen = XMLHttpRequest.prototype.open;
            const originalSend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.open = function(method, url, ...args) {
                this.__streamForgeRequestUrl = url;
                return originalOpen.call(this, method, url, ...args);
            };
            XMLHttpRequest.prototype.send = function(...args) {
                this.addEventListener("loadend", () => {
                    try {
                        record(
                            this.responseURL || this.__streamForgeRequestUrl,
                            this.getResponseHeader("content-type"),
                            "xmlhttprequest");
                    } catch {}
                }, { once: true });
                return originalSend.apply(this, args);
            };
        })();
        """;
    private readonly JwPlayerExtractor _jwPlayerExtractor = new();
    private readonly KnownPlayerSourceExtractor _knownPlayerSourceExtractor = new();

    public async Task<MediaStream?> ExtractAsync(
        Uri pageUrl,
        IProgress<NetworkActivity>? activity,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ExtractionTimeout);

        var candidates = new MediaCandidateCollector();
        var responseTasks = new ConcurrentBag<Task>();

        ReportActivity(activity, "PAGE", "Opening page and monitoring network activity", pageUrl, important: true);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchChromiumAsync(playwright, activity);
        await using var context = await browser.NewContextAsync();
        await context.AddInitScriptAsync(MediaRequestCaptureScript);

        context.Request += (_, request) => CaptureRequest(request, candidates, activity);
        context.Response += (_, response) =>
        {
            var responseTask = CaptureResponseAsync(response, candidates, activity);
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
            ReportActivity(activity, "PAGE", "Navigation timed out; continuing with captured requests", pageUrl);
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
                    await InspectPlayersAsync(context, candidates, activity);
                    nextInspection = now + InspectionInterval;
                }

                var lastCandidateAt = candidates.LastCandidateAt;
                var minimumObservationTime = candidates.BlobPlayerObserved
                    ? BlobPlayerMinimumObservationTime
                    : MinimumObservationTime;
                var observedLongEnough = now - observationStarted >= minimumObservationTime;
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
            if (candidates.BlobPlayerObserved)
            {
                logger.LogInformation("A blob-backed player was detected, but no underlying HTTP media stream was found.");
            }
            else
            {
                logger.LogInformation("No supported media stream was detected.");
            }

            return null;
        }

        logger.LogInformation(
            "Detected media type {MediaType} from {DetectionSource}",
            best.Type,
            best.Source);

        ReportActivity(
            activity,
            "SELECTED",
            $"Selected {best.Type} download source",
            best.Url,
            best.Status,
            important: true);

        return CreateMediaStream(best);
    }

    private async Task<IBrowser> LaunchChromiumAsync(
        IPlaywright playwright,
        IProgress<NetworkActivity>? activity)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException exception) when (IsMissingBrowser(exception))
        {
            logger.LogInformation("Playwright Chromium was not found; installing it for the current user.");
            activity?.Report(new NetworkActivity
            {
                Category = "SETUP",
                Message = "Chromium is missing; installing it now",
                IsImportant = true
            });
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]));
            if (exitCode != 0)
            {
                throw new StreamForgeException(
                    "Playwright Chromium could not be installed. Run scripts\\setup.ps1 and try again.",
                    exception);
            }

            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
    }

    private static bool IsMissingBrowser(PlaywrightException exception)
    {
        return exception.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
    }

    private static void CaptureRequest(
        IRequest request,
        MediaCandidateCollector candidates,
        IProgress<NetworkActivity>? activity)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri)
            || MediaTypeDetector.ShouldIgnore(requestUri))
        {
            return;
        }

        var type = MediaTypeDetector.Detect(requestUri);
        if (ShouldReportRequest(request.ResourceType, type))
        {
            ReportActivity(
                activity,
                request.ResourceType.ToUpperInvariant(),
                $"{request.Method} request",
                requestUri,
                important: type != MediaSourceType.Unknown);
        }

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

    private static async Task CaptureResponseAsync(
        IResponse response,
        MediaCandidateCollector candidates,
        IProgress<NetworkActivity>? activity)
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
                await CaptureEmbeddedApiMediaAsync(response, responseUri, contentType, candidates, activity);
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

            ReportActivity(
                activity,
                "CANDIDATE",
                $"Detected {type} media response",
                responseUri,
                response.Status,
                important: true);
        }
        catch (PlaywrightException)
        {
            // The page or response may disappear while an asynchronous player is changing sources.
        }
    }

    private static async Task CaptureEmbeddedApiMediaAsync(
        IResponse response,
        Uri responseUri,
        string? contentType,
        MediaCandidateCollector candidates,
        IProgress<NetworkActivity>? activity)
    {
        if (!response.Ok
            || !IsApiResourceType(response.Request.ResourceType)
            || !IsInspectableTextResponse(contentType, response.Headers))
        {
            return;
        }

        string responseBody;
        try
        {
            responseBody = await response.TextAsync();
        }
        catch (PlaywrightException)
        {
            return;
        }

        var mediaUrls = EmbeddedMediaUrlExtractor.Extract(responseBody, responseUri);
        if (mediaUrls.Count == 0)
        {
            return;
        }

        var requestHeaders = HeaderSanitizer.Filter(await response.Request.AllHeadersAsync());
        foreach (var mediaUrl in mediaUrls)
        {
            var mediaType = MediaTypeDetector.Detect(mediaUrl);
            candidates.Add(new MediaCandidate
            {
                Url = mediaUrl,
                Type = mediaType,
                Source = MediaCandidateSource.BrowserObservation,
                Headers = requestHeaders,
                ResourceType = response.Request.ResourceType,
                ContentType = contentType,
                Status = response.Status,
                FrameUrl = response.Frame.Url
            });

            ReportActivity(
                activity,
                "CANDIDATE",
                $"Detected {mediaType} URL in player API response",
                mediaUrl,
                response.Status,
                important: true);
        }
    }

    private static bool IsApiResourceType(string resourceType)
    {
        return resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInspectableTextResponse(
        string? contentType,
        IReadOnlyDictionary<string, string> headers)
    {
        if (long.TryParse(GetHeader(headers, "content-length"), out var contentLength)
            && contentLength > 1_000_000)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(contentType)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase);
    }

    private async Task InspectPlayersAsync(
        IBrowserContext context,
        MediaCandidateCollector candidates,
        IProgress<NetworkActivity>? activity)
    {
        foreach (var page in context.Pages)
        {
            foreach (var frame in page.Frames)
            {
                await InspectFrameAsync(frame, candidates, activity);
            }
        }
    }

    private async Task InspectFrameAsync(
        IFrame frame,
        MediaCandidateCollector candidates,
        IProgress<NetworkActivity>? activity)
    {
        if (frame.IsDetached)
        {
            return;
        }

        try
        {
            var activatedEmbeds = await frame.EvaluateAsync<string[]>(DooPlayPlayerActivationScript);
            foreach (var embed in activatedEmbeds)
            {
                if (Uri.TryCreate(embed, UriKind.Absolute, out var embedUri) && IsHttpUri(embedUri))
                {
                    ReportActivity(
                        activity,
                        "PLAYER",
                        "Embedded player discovered from page server metadata",
                        embedUri,
                        important: true);
                }
            }

            var playerState = await frame.EvaluateAsync<JsonElement>("""
                () => {
                    const urls = [];
                    let hasBlobVideo = false;

                    for (const video of document.querySelectorAll("video")) {
                        if (video.currentSrc) {
                            urls.push(video.currentSrc);
                            hasBlobVideo ||= video.currentSrc.startsWith("blob:");
                        }
                        if (video.src) {
                            urls.push(video.src);
                            hasBlobVideo ||= video.src.startsWith("blob:");
                        }

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

                    const observedRequests = [
                        ...(globalThis.__streamForgeObservedMediaRequests || [])
                    ];

                    for (const entry of performance.getEntriesByType("resource")) {
                        if (/\.(m3u8|mpd|mp4)(?:$|[?#])/i.test(entry.name)) {
                            observedRequests.push({
                                url: entry.name,
                                contentType: "",
                                initiatorType: entry.initiatorType || "performance"
                            });
                        }
                    }

                    return {
                        urls: [...new Set(urls)],
                        userAgent: navigator.userAgent,
                        hasBlobVideo,
                        observedRequests
                    };
                }
                """);

            var userAgent = playerState.TryGetProperty("userAgent", out var userAgentElement)
                ? userAgentElement.GetString()
                : null;
            var frameHeaders = CreateFrameHeaders(frame.Url, userAgent);

            foreach (var source in await _knownPlayerSourceExtractor.ExtractAsync(frame))
            {
                AddPlayerCandidate(
                    source.Url.AbsoluteUri,
                    frame.Url,
                    frameHeaders,
                    MediaCandidateSource.KnownPlayer,
                    candidates);
                ReportActivity(
                    activity,
                    "PLAYER",
                    $"{source.Player} exposed a supported media source",
                    source.Url,
                    important: true);
            }

            if (playerState.TryGetProperty("hasBlobVideo", out var blobElement)
                && blobElement.ValueKind is JsonValueKind.True)
            {
                if (candidates.MarkBlobPlayerObserved())
                {
                    activity?.Report(new NetworkActivity
                    {
                        Category = "PLAYER",
                        Message = "Blob-backed video found; locating its underlying media API",
                        DisplayUrl = frame.Url,
                        IsImportant = true
                    });
                }
            }

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

            if (playerState.TryGetProperty("observedRequests", out var observedRequestsElement)
                && observedRequestsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var observedRequest in observedRequestsElement.EnumerateArray())
                {
                    var url = GetJsonString(observedRequest, "url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    AddObservedCandidate(
                        url,
                        GetJsonString(observedRequest, "contentType"),
                        GetJsonString(observedRequest, "initiatorType"),
                        frame.Url,
                        frameHeaders,
                        candidates);
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var mediaUri) || !IsHttpUri(mediaUri))
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

    private static void AddObservedCandidate(
        string url,
        string? contentType,
        string? initiatorType,
        string frameUrl,
        IReadOnlyDictionary<string, string> headers,
        MediaCandidateCollector candidates)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var mediaUri) || !IsHttpUri(mediaUri))
        {
            return;
        }

        var type = MediaTypeDetector.Detect(mediaUri, contentType);
        if (type == MediaSourceType.Unknown)
        {
            return;
        }

        candidates.Add(new MediaCandidate
        {
            Url = mediaUri,
            Type = type,
            Source = MediaCandidateSource.BrowserObservation,
            Headers = headers,
            ResourceType = initiatorType,
            ContentType = contentType,
            FrameUrl = frameUrl
        });
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsHttpUri(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldReportRequest(string resourceType, MediaSourceType mediaType)
    {
        return mediaType != MediaSourceType.Unknown
            || resourceType.Equals("document", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("media", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReportActivity(
        IProgress<NetworkActivity>? activity,
        string category,
        string message,
        Uri url,
        int? statusCode = null,
        bool important = false)
    {
        activity?.Report(new NetworkActivity
        {
            Category = category,
            Message = message,
            DisplayUrl = NetworkUrlSanitizer.ForDisplay(url),
            StatusCode = statusCode,
            IsImportant = important
        });
    }

    private static IReadOnlyDictionary<string, string> CreateFrameHeaders(string frameUrl, string? userAgent)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(frameUrl, UriKind.Absolute, out var frameUri))
        {
            headers["referer"] = frameUri.AbsoluteUri;
            headers["origin"] = frameUri.GetLeftPart(UriPartial.Authority);
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
