using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StreamForge.Core.Exceptions;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

public sealed class PlaywrightStreamExtractor(
    ILogger<PlaywrightStreamExtractor> logger,
    IAiExtractionAdvisor aiExtractionAdvisor) : IStreamExtractor
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumObservationTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BlobPlayerMinimumObservationTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SeedProbeObservationTime = TimeSpan.FromSeconds(8);
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
    private readonly PlayerSeedExtractor _playerSeedExtractor = new();

    public async Task<StreamExtractionResult> ExtractAsync(
        Uri pageUrl,
        StreamExtractionOptions options,
        IProgress<NetworkActivity>? activity,
        IProgress<IdentificationStepUpdate>? steps,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ExtractionTimeout);

        var candidates = new MediaCandidateCollector();
        var seeds = new PlayerSeedCollector();
        var diagnostics = new ExtractionDiagnosticCollector();
        var responseTasks = new ConcurrentBag<Task>();
        var extractionTimedOut = false;

        ReportStep(
            steps,
            IdentificationStepKeys.OpenPage,
            1,
            "Open video page",
            IdentificationStepStatus.Identifying,
            "Starting Chromium and loading the supplied page.");
        ReportActivity(activity, "PAGE", "Opening page and monitoring network activity", pageUrl, important: true);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchChromiumAsync(playwright, activity);
        await using var context = await browser.NewContextAsync();
        await context.AddInitScriptAsync(MediaRequestCaptureScript);

        context.Request += (_, request) => CaptureRequest(request, candidates, seeds, activity);
        context.Response += (_, response) =>
        {
            var responseTask = CaptureResponseAsync(response, candidates, diagnostics, activity);
            responseTasks.Add(responseTask);
        };

        var page = await context.NewPageAsync();
        logger.LogInformation("Page analysis started for {PageHost}", pageUrl.Host);

        ReportStep(
            steps,
            IdentificationStepKeys.MonitorNetwork,
            2,
            "Monitor media network calls",
            IdentificationStepStatus.Identifying,
            "Watching document, fetch, XHR, and media requests.");

        try
        {
            await page.GotoAsync(pageUrl.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)NavigationTimeout.TotalMilliseconds
            });
            ReportStep(
                steps,
                IdentificationStepKeys.OpenPage,
                1,
                "Open video page",
                IdentificationStepStatus.Success,
                "The page DOM loaded successfully.");
        }
        catch (PlaywrightException exception) when (
            exception.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Page navigation timed out; inspecting requests captured during loading.");
            ReportActivity(activity, "PAGE", "Navigation timed out; continuing with captured requests", pageUrl);
            ReportStep(
                steps,
                IdentificationStepKeys.OpenPage,
                1,
                "Open video page",
                IdentificationStepStatus.Success,
                "Navigation timed out, but the loaded DOM and captured requests remain available.");
        }

        ReportStep(
            steps,
            IdentificationStepKeys.DiscoverSeeds,
            3,
            "Collect player and embed seed links",
            IdentificationStepStatus.Identifying,
            "Scanning frames and player metadata for follow-up URLs.");
        ReportStep(
            steps,
            IdentificationStepKeys.InspectPlayers,
            4,
            "Inspect player APIs and video elements",
            IdentificationStepStatus.Identifying,
            "Checking HTML5 video elements and supported player APIs.");

        var observationStarted = DateTimeOffset.UtcNow;
        var nextInspection = DateTimeOffset.MinValue;

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextInspection)
                {
                    await InspectPlayersAsync(context, candidates, seeds, activity);
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

                var seedsSettled = seeds.LastSeedAt is { } lastSeedAt
                    && now - lastSeedAt >= NetworkSettleTime;
                if (candidates.Count == 0
                    && seeds.Count > 0
                    && now - observationStarted >= SeedProbeObservationTime
                    && seedsSettled)
                {
                    break;
                }

                await Task.Delay(250, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The extraction timeout is an expected no-result condition.
            extractionTimedOut = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await AwaitResponseTasksAsync(responseTasks, cancellationToken);

        ReportStep(
            steps,
            IdentificationStepKeys.MonitorNetwork,
            2,
            "Monitor media network calls",
            candidates.Count > 0 ? IdentificationStepStatus.Success : IdentificationStepStatus.Skipped,
            candidates.Count > 0
                ? $"Captured {candidates.Count} standard media candidate(s)."
                : "No direct HLS, DASH, or MP4 request was captured.");
        ReportStep(
            steps,
            IdentificationStepKeys.DiscoverSeeds,
            3,
            "Collect player and embed seed links",
            seeds.Count > 0 ? IdentificationStepStatus.Success : IdentificationStepStatus.Skipped,
            seeds.Count > 0
                ? $"Collected {seeds.Count} unique player, embed, or API seed link(s)."
                : "The page exposed no additional seed links.");
        ReportStep(
            steps,
            IdentificationStepKeys.InspectPlayers,
            4,
            "Inspect player APIs and video elements",
            candidates.Count > 0 || candidates.BlobPlayerObserved
                ? IdentificationStepStatus.Success
                : IdentificationStepStatus.Skipped,
            candidates.BlobPlayerObserved
                ? "A blob-backed player was found; its underlying requests and seed links were inspected."
                : candidates.Count > 0
                    ? "Player inspection exposed one or more standard media candidates."
                    : "No supported player API exposed a direct media source.");

        var candidatesBeforeSeedProbe = candidates.Count;
        if (candidates.Count == 0 && seeds.Count > 0)
        {
            ReportStep(
                steps,
                IdentificationStepKeys.ProbeSeeds,
                5,
                "Probe discovered seed links",
                IdentificationStepStatus.Identifying,
                "Following a bounded set of likely player and API links.");
            await ProbeSeedLinksAsync(context, seeds, candidates, diagnostics, activity, cancellationToken);
            ReportStep(
                steps,
                IdentificationStepKeys.ProbeSeeds,
                5,
                "Probe discovered seed links",
                candidates.Count > candidatesBeforeSeedProbe
                    ? IdentificationStepStatus.Success
                    : IdentificationStepStatus.Skipped,
                candidates.Count > candidatesBeforeSeedProbe
                    ? $"Seed probing discovered {candidates.Count - candidatesBeforeSeedProbe} media candidate(s)."
                    : "Seed links were checked, but none produced a supported media source.");
        }
        else
        {
            ReportStep(
                steps,
                IdentificationStepKeys.ProbeSeeds,
                5,
                "Probe discovered seed links",
                IdentificationStepStatus.Skipped,
                candidates.Count > 0
                    ? "A direct media candidate was already available."
                    : "No seed links were available to probe.");
        }

        ReportStep(
            steps,
            IdentificationStepKeys.SelectCandidate,
            6,
            "Validate and select download source",
            IdentificationStepStatus.Identifying,
            "Ranking detected media candidates and rejecting unsupported results.");
        var best = CandidateRanker.ChooseBest(candidates.Snapshot());
        var extractionDiagnostics = diagnostics.CreateDiagnostics(candidates.BlobPlayerObserved, extractionTimedOut);
        AiExtractionSuggestion? aiSuggestion = null;
        var aiAdvisorAttempted = options.EnableLocalAiAdvisor && extractionDiagnostics.Responses.Count > 0;

        if (best is null && aiAdvisorAttempted)
        {
            ReportStep(
                steps,
                IdentificationStepKeys.AiFallback,
                7,
                "Optional local AI fallback",
                IdentificationStepStatus.Identifying,
                "Classifying sanitized response structures with the configured local model.");
            activity?.Report(new NetworkActivity
            {
                Category = "AI",
                Message = "Asking the local AI advisor to classify sanitized response structures",
                IsImportant = true
            });

            aiSuggestion = await aiExtractionAdvisor.AdviseAsync(extractionDiagnostics, options, cancellationToken);
            if (aiSuggestion is not null)
            {
                best = await ValidateAiSuggestionAsync(
                    context,
                    diagnostics,
                    aiSuggestion,
                    activity,
                    cancellationToken);
            }

            ReportStep(
                steps,
                IdentificationStepKeys.AiFallback,
                7,
                "Optional local AI fallback",
                best is not null ? IdentificationStepStatus.Success : IdentificationStepStatus.Skipped,
                best is not null
                    ? "A model suggestion passed deterministic media validation."
                    : "The local model produced no candidate that passed deterministic validation.");
        }
        else
        {
            ReportStep(
                steps,
                IdentificationStepKeys.AiFallback,
                7,
                "Optional local AI fallback",
                IdentificationStepStatus.Skipped,
                options.EnableLocalAiAdvisor
                    ? "No suitable sanitized response structure required AI classification."
                    : "Local AI diagnostics are disabled.");
        }

        if (best is null)
        {
            ReportStep(
                steps,
                IdentificationStepKeys.SelectCandidate,
                6,
                "Validate and select download source",
                IdentificationStepStatus.Failed,
                "No supported, verifiable, non-DRM media source was found.");
            if (candidates.BlobPlayerObserved)
            {
                logger.LogInformation("A blob-backed player was detected, but no underlying HTTP media stream was found.");
            }
            else
            {
                logger.LogInformation("No supported media stream was detected.");
            }

            var failureReason = extractionDiagnostics.FailureReason;
            return new StreamExtractionResult
            {
                FailureReason = failureReason,
                Message = CreateFailureMessage(failureReason),
                AiAdvisorUsed = aiAdvisorAttempted,
                AiDiagnostic = aiAdvisorAttempted
                    ? aiSuggestion?.Explanation ?? "The local AI advisor was unavailable or found no verifiable media candidate."
                    : null
            };
        }

        logger.LogInformation(
            "Detected media type {MediaType} from {DetectionSource}",
            best.Type,
            best.Source);

        ReportStep(
            steps,
            IdentificationStepKeys.SelectCandidate,
            6,
            "Validate and select download source",
            IdentificationStepStatus.Success,
            $"Selected a {best.Type} source detected through {best.Source}.");

        ReportActivity(
            activity,
            "SELECTED",
            $"Selected {best.Type} download source",
            best.Url,
            best.Status,
            important: true);

        return new StreamExtractionResult
        {
            Stream = CreateMediaStream(best),
            FailureReason = AnalysisFailureReason.None,
            Message = aiSuggestion is null
                ? $"Detected {best.Type} stream."
                : $"Detected {best.Type} stream after deterministic validation of a local AI suggestion.",
            AiAdvisorUsed = aiSuggestion is not null,
            AiDiagnostic = aiSuggestion?.Explanation
        };
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
        PlayerSeedCollector seeds,
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
            if ((request.ResourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                    || request.ResourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
                && request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                && IsLikelyPlayerApiSeed(requestUri))
            {
                var added = seeds.Add(new PlayerSeedLink
                {
                    Url = requestUri,
                    Source = PlayerSeedSource.NetworkApi,
                    Headers = HeaderSanitizer.Filter(request.Headers),
                    FrameUrl = request.Frame.Url
                });
                if (added)
                {
                    ReportActivity(activity, "SEED", "Collected likely player API link", requestUri);
                }
            }

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
        ExtractionDiagnosticCollector diagnostics,
        IProgress<NetworkActivity>? activity)
    {
        try
        {
            if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri)
                || MediaTypeDetector.ShouldIgnore(responseUri))
            {
                return;
            }

            diagnostics.ObserveResponse(
                responseUri,
                response.Status,
                response.Request.ResourceType,
                response.Headers);

            var contentType = GetHeader(response.Headers, "content-type");
            var type = MediaTypeDetector.Detect(responseUri, contentType);
            if (type == MediaSourceType.Unknown)
            {
                await CaptureEmbeddedApiMediaAsync(
                    response,
                    responseUri,
                    contentType,
                    candidates,
                    diagnostics,
                    activity);
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
        ExtractionDiagnosticCollector diagnostics,
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

        var allRequestHeaders = await response.Request.AllHeadersAsync();
        diagnostics.ObserveInspectableResponse(
            responseUri,
            response.Request.Method,
            response.Status,
            response.Request.ResourceType,
            contentType,
            responseBody,
            allRequestHeaders);

        var mediaUrls = EmbeddedMediaUrlExtractor.Extract(responseBody, responseUri);
        if (mediaUrls.Count == 0)
        {
            return;
        }

        var requestHeaders = HeaderSanitizer.Filter(allRequestHeaders);
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

    private static bool IsLikelyPlayerApiSeed(Uri uri)
    {
        var value = $"{uri.Host}{uri.AbsolutePath}";
        string[] indicators =
        [
            "player", "video", "stream", "source", "embed", "episode", "playback",
            "manifest", "playlist", "ajax", "/api/"
        ];
        return indicators.Any(indicator => value.Contains(indicator, StringComparison.OrdinalIgnoreCase));
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
        PlayerSeedCollector seeds,
        IProgress<NetworkActivity>? activity)
    {
        foreach (var page in context.Pages)
        {
            foreach (var frame in page.Frames)
            {
                await InspectFrameAsync(frame, candidates, seeds, activity);
            }
        }
    }

    private async Task InspectFrameAsync(
        IFrame frame,
        MediaCandidateCollector candidates,
        PlayerSeedCollector seeds,
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
                    seeds.Add(new PlayerSeedLink
                    {
                        Url = embedUri,
                        Source = PlayerSeedSource.EmbedElement,
                        Headers = CreateFrameHeaders(frame.Url, null),
                        FrameUrl = frame.Url
                    });
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

            foreach (var discoveredSeed in await _playerSeedExtractor.ExtractAsync(frame))
            {
                var added = seeds.Add(new PlayerSeedLink
                {
                    Url = discoveredSeed.Url,
                    Source = discoveredSeed.Source,
                    Headers = frameHeaders,
                    FrameUrl = frame.Url
                });
                if (!added)
                {
                    continue;
                }

                var seedType = MediaTypeDetector.Detect(discoveredSeed.Url);
                if (seedType != MediaSourceType.Unknown)
                {
                    candidates.Add(new MediaCandidate
                    {
                        Url = discoveredSeed.Url,
                        Type = seedType,
                        Source = MediaCandidateSource.BrowserObservation,
                        Headers = frameHeaders,
                        ResourceType = "player-seed",
                        FrameUrl = frame.Url
                    });
                }

                ReportActivity(
                    activity,
                    "SEED",
                    $"Collected {discoveredSeed.Source} link",
                    discoveredSeed.Url,
                    important: seedType != MediaSourceType.Unknown);
            }

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

    private static async Task ProbeSeedLinksAsync(
        IBrowserContext context,
        PlayerSeedCollector seeds,
        MediaCandidateCollector candidates,
        ExtractionDiagnosticCollector diagnostics,
        IProgress<NetworkActivity>? activity,
        CancellationToken cancellationToken)
    {
        const int maximumProbes = 8;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(TimeSpan.FromSeconds(12));

        var initialSeeds = seeds.Snapshot()
            .Where(seed => MediaTypeDetector.Detect(seed.Url) == MediaSourceType.Unknown)
            .Take(maximumProbes)
            .ToArray();
        var queue = new Queue<PlayerSeedLink>(initialSeeds);
        var queuedUrls = new HashSet<string>(initialSeeds.Select(seed => seed.Url.AbsoluteUri), StringComparer.Ordinal);
        var probes = 0;

        while (queue.Count > 0 && probes < maximumProbes && !probeCts.IsCancellationRequested)
        {
            var seed = queue.Dequeue();
            probes++;
            IAPIResponse? response = null;
            try
            {
                var probeHeaders = CreateSeedProbeHeaders(seed.Headers);
                response = await context.APIRequest.GetAsync(seed.Url.AbsoluteUri, new APIRequestContextOptions
                {
                    Headers = probeHeaders,
                    Timeout = 4_000
                });

                var responseUri = Uri.TryCreate(response.Url, UriKind.Absolute, out var redirectedUri)
                    ? redirectedUri
                    : seed.Url;
                ReportActivity(
                    activity,
                    "SEED",
                    response.Ok ? "Probed player seed link" : "Seed link returned an error",
                    responseUri,
                    response.Status,
                    important: response.Ok);

                if (!response.Ok)
                {
                    continue;
                }

                var contentType = GetHeader(response.Headers, "content-type");
                var detectedType = MediaTypeDetector.DetectContentType(contentType);
                var canReadBody = !long.TryParse(GetHeader(response.Headers, "content-length"), out var contentLength)
                    || contentLength <= 1_000_000;
                byte[]? body = null;
                string? bodyText = null;
                if (canReadBody && IsInspectableSeedResponse(contentType, detectedType))
                {
                    body = await response.BodyAsync();
                    detectedType = detectedType == MediaSourceType.Unknown
                        ? DetectMediaBody(body)
                        : detectedType;
                    bodyText = System.Text.Encoding.UTF8.GetString(
                        body.AsSpan(0, Math.Min(body.Length, 1_000_000)));
                }

                if (detectedType != MediaSourceType.Unknown)
                {
                    if (body is not null
                        && detectedType is MediaSourceType.Hls or MediaSourceType.Dash
                        && ContainsDrmIndicator(body))
                    {
                        activity?.Report(new NetworkActivity
                        {
                            Category = "DRM",
                            Message = "A DRM-marked seed response was rejected",
                            DisplayUrl = NetworkUrlSanitizer.ForDisplay(responseUri),
                            IsImportant = true
                        });
                        continue;
                    }

                    candidates.Add(new MediaCandidate
                    {
                        Url = responseUri,
                        Type = detectedType,
                        Source = MediaCandidateSource.SeedProbe,
                        Headers = HeaderSanitizer.Filter(seed.Headers),
                        ResourceType = "seed-probe",
                        ContentType = contentType,
                        Status = response.Status,
                        IsMasterPlaylist = detectedType == MediaSourceType.Hls
                            && bodyText?.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) == true,
                        FrameUrl = seed.FrameUrl
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(bodyText))
                {
                    continue;
                }

                diagnostics.ObserveInspectableResponse(
                    responseUri,
                    "GET",
                    response.Status,
                    "seed-probe",
                    contentType,
                    bodyText,
                    seed.Headers);

                foreach (var mediaUrl in EmbeddedMediaUrlExtractor.Extract(bodyText, responseUri))
                {
                    var mediaType = MediaTypeDetector.Detect(mediaUrl);
                    candidates.Add(new MediaCandidate
                    {
                        Url = mediaUrl,
                        Type = mediaType,
                        Source = MediaCandidateSource.SeedProbe,
                        Headers = HeaderSanitizer.Filter(seed.Headers),
                        ResourceType = "seed-response",
                        Status = response.Status,
                        FrameUrl = seed.FrameUrl ?? responseUri.AbsoluteUri
                    });
                    ReportActivity(
                        activity,
                        "CANDIDATE",
                        $"Found {mediaType} URL through a seed response",
                        mediaUrl,
                        response.Status,
                        important: true);
                }

                if (seed.Depth >= 1)
                {
                    continue;
                }

                foreach (var nestedUrl in SeedResponseUrlExtractor.Extract(bodyText, responseUri))
                {
                    var nestedSeed = new PlayerSeedLink
                    {
                        Url = nestedUrl,
                        Source = PlayerSeedSource.SeedResponse,
                        Headers = seed.Headers,
                        FrameUrl = seed.FrameUrl ?? responseUri.AbsoluteUri,
                        Depth = seed.Depth + 1
                    };
                    if (seeds.Add(nestedSeed)
                        && queuedUrls.Add(nestedUrl.AbsoluteUri)
                        && queue.Count + probes < maximumProbes)
                    {
                        queue.Enqueue(nestedSeed);
                        ReportActivity(
                            activity,
                            "SEED",
                            "Collected nested player link from seed response",
                            nestedUrl);
                    }
                }
            }
            catch (PlaywrightException)
            {
                // A seed can expire, redirect unexpectedly, or reject an independent probe.
            }
            finally
            {
                if (response is not null)
                {
                    await response.DisposeAsync();
                }
            }
        }
    }

    private static Dictionary<string, string> CreateSeedProbeHeaders(IReadOnlyDictionary<string, string> source)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json, text/html, application/vnd.apple.mpegurl, application/dash+xml, video/mp4, */*",
            ["Range"] = "bytes=0-999999"
        };
        foreach (var name in new[] { "user-agent", "referer", "origin" })
        {
            var value = GetHeader(source, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                headers[name] = value;
            }
        }

        return headers;
    }

    private static bool IsInspectableSeedResponse(string? contentType, MediaSourceType detectedType)
    {
        return detectedType != MediaSourceType.Mp4
            && (string.IsNullOrWhiteSpace(contentType)
                || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("text", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<MediaCandidate?> ValidateAiSuggestionAsync(
        IBrowserContext context,
        ExtractionDiagnosticCollector diagnostics,
        AiExtractionSuggestion suggestion,
        IProgress<NetworkActivity>? activity,
        CancellationToken cancellationToken)
    {
        const double minimumConfidence = 0.65;
        if (suggestion.Confidence < minimumConfidence
            || suggestion.ExpectedType == MediaSourceType.Unknown
            || string.IsNullOrWhiteSpace(suggestion.JsonPointer)
            || !diagnostics.TryResolveUrl(
                suggestion.ObservationId,
                suggestion.JsonPointer,
                out var candidateUrl,
                out var requestHeaders,
                out var responseUrl)
            || candidateUrl is null
            || !IsHttpUri(candidateUrl))
        {
            return null;
        }

        IAPIResponse? probe = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe = await context.APIRequest.GetAsync(candidateUrl.AbsoluteUri, new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Accept"] = "application/vnd.apple.mpegurl, application/dash+xml, video/mp4, */*",
                    ["Range"] = "bytes=0-65535"
                },
                Timeout = 8_000
            });

            if (!probe.Ok)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var contentType = GetHeader(probe.Headers, "content-type");
            var contentTypeMedia = MediaTypeDetector.DetectContentType(contentType);
            if (long.TryParse(GetHeader(probe.Headers, "content-length"), out var contentLength)
                && contentLength > 1_000_000)
            {
                return null;
            }

            var probeBody = await probe.BodyAsync();
            var bodyMedia = DetectMediaBody(probeBody);
            var detectedType = bodyMedia != MediaSourceType.Unknown ? bodyMedia : contentTypeMedia;
            if (detectedType == MediaSourceType.Unknown || detectedType != suggestion.ExpectedType)
            {
                return null;
            }

            if (detectedType is MediaSourceType.Hls or MediaSourceType.Dash
                && ContainsDrmIndicator(probeBody))
            {
                activity?.Report(new NetworkActivity
                {
                    Category = "DRM",
                    Message = "The AI-suggested manifest contains a DRM indicator and was rejected",
                    IsImportant = true
                });
                return null;
            }

            ReportActivity(
                activity,
                "AI",
                $"Local AI suggestion confirmed as {detectedType} by a media probe",
                candidateUrl,
                probe.Status,
                important: true);

            return new MediaCandidate
            {
                Url = candidateUrl,
                Type = detectedType,
                Source = MediaCandidateSource.BrowserObservation,
                Headers = requestHeaders,
                ResourceType = "ai-advised",
                ContentType = contentType,
                Status = probe.Status,
                IsMasterPlaylist = detectedType == MediaSourceType.Hls
                    && System.Text.Encoding.UTF8.GetString(probeBody)
                        .Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase),
                FrameUrl = responseUrl?.AbsoluteUri
            };
        }
        catch (PlaywrightException)
        {
            return null;
        }
        finally
        {
            if (probe is not null)
            {
                await probe.DisposeAsync();
            }
        }
    }

    private static MediaSourceType DetectMediaBody(byte[] body)
    {
        if (body.Length >= 8
            && body[4] == (byte)'f'
            && body[5] == (byte)'t'
            && body[6] == (byte)'y'
            && body[7] == (byte)'p')
        {
            return MediaSourceType.Mp4;
        }

        var text = System.Text.Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 65_536)));
        if (text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            return MediaSourceType.Hls;
        }

        return text.Contains("<MPD", StringComparison.OrdinalIgnoreCase)
            ? MediaSourceType.Dash
            : MediaSourceType.Unknown;
    }

    private static bool ContainsDrmIndicator(byte[] body)
    {
        var text = System.Text.Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 1_000_000)));
        string[] indicators =
        [
            "com.widevine.alpha",
            "com.microsoft.playready",
            "skd://",
            "urn:mpeg:dash:mp4protection",
            "SAMPLE-AES"
        ];

        return indicators.Any(indicator => text.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateFailureMessage(AnalysisFailureReason reason) => reason switch
    {
        AnalysisFailureReason.AuthenticationRequired =>
            "The page or media endpoint requires an authenticated browser session. StreamForge does not bypass sign-in.",
        AnalysisFailureReason.BotChallenge =>
            "The site presented a bot challenge. StreamForge does not automate CAPTCHA or challenge bypasses.",
        AnalysisFailureReason.AuthorizationExpired =>
            "A media or player request was forbidden, possibly because its short-lived authorization expired. Retry analysis while the page is active.",
        AnalysisFailureReason.DrmProtected =>
            "This stream appears to use DRM protection and cannot be processed by StreamForge.",
        AnalysisFailureReason.UnsupportedProtocol =>
            "A blob-backed player was found, but no supported underlying HLS, DASH, or MP4 source was exposed.",
        AnalysisFailureReason.Timeout =>
            "Analysis timed out before a supported media stream was detected.",
        _ => "No supported media stream was detected."
    };

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

    private static void ReportStep(
        IProgress<IdentificationStepUpdate>? steps,
        string key,
        int order,
        string title,
        IdentificationStepStatus status,
        string detail)
    {
        steps?.Report(new IdentificationStepUpdate
        {
            Key = key,
            Order = order,
            Title = title,
            Status = status,
            Detail = detail
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
