using System.Text.Json;
using Microsoft.Playwright;

namespace StreamForge.Infrastructure.Extraction;

internal sealed class KnownPlayerSourceExtractor
{
    private const string DiscoveryScript = """
        () => {
            const results = [];
            const mediaPattern = /\.(m3u8|mpd|mp4)(?:$|[?#])/i;
            const seenThisPass = new Set();
            const reported = globalThis.__streamForgeKnownPlayerSources
                || (globalThis.__streamForgeKnownPlayerSources = new Set());

            const addUrl = (player, value) => {
                if (typeof value !== "string" || !value.trim()) return;

                const normalized = value
                    .replace(/\\\//g, "/")
                    .replace(/\\u0026/gi, "&");
                try {
                    const url = new URL(normalized, document.baseURI).href;
                    if (!mediaPattern.test(url) || seenThisPass.has(url) || reported.has(url)) return;
                    seenThisPass.add(url);
                    reported.add(url);
                    results.push({ player, url });
                } catch {}
            };

            const addValue = (player, value, depth = 0, visited = new WeakSet()) => {
                if (depth > 6 || value == null) return;
                if (typeof value === "string") {
                    addUrl(player, value);
                    return;
                }
                if (Array.isArray(value)) {
                    for (const item of value.slice(0, 50)) addValue(player, item, depth + 1, visited);
                    return;
                }
                if (typeof value !== "object" || visited.has(value)) return;
                visited.add(value);

                for (const key of [
                    "src", "currentSrc", "file", "url", "uri", "source", "sources",
                    "playlist", "levels", "quality", "mediaFiles", "current", "manifest",
                    "manifestUri", "assetUri", "config", "option", "options", "video", "media"
                ]) {
                    try {
                        if (key in value) addValue(player, value[key], depth + 1, visited);
                    } catch {}
                }
            };

            const inspectedPlayers = new WeakSet();
            const inspectPlayer = (fallbackName, instance) => {
                if (!instance || (typeof instance !== "object" && typeof instance !== "function")) return;
                if (inspectedPlayers.has(instance)) return;
                inspectedPlayers.add(instance);

                let playerName = fallbackName;
                try {
                    const constructorName = instance.constructor?.name;
                    const normalized = (constructorName || "").toLowerCase();
                    if (normalized.includes("videojs")) playerName = "Video.js";
                    else if (normalized.includes("shaka")) playerName = "Shaka Player";
                    else if (normalized.includes("clappr")) playerName = "Clappr";
                    else if (normalized.includes("flowplayer")) playerName = "Flowplayer";
                    else if (normalized.includes("fluid")) playerName = "Fluid Player";
                    else if (normalized.includes("mediaelement")) playerName = "MediaElement.js";
                    else if (normalized.includes("openplayer")) playerName = "OpenPlayerJS";
                    else if (normalized.includes("artplayer")) playerName = "ArtPlayer";
                    else if (normalized.includes("dplayer")) playerName = "DPlayer";
                    else if (normalized.includes("plyr")) playerName = "Plyr";
                } catch {}

                for (const getter of [
                    "getAssetUri", "getManifestUri", "getSource", "getSrc", "currentSource",
                    "currentSources", "src", "mediaFiles", "current", "levels"
                ]) {
                    try {
                        if (typeof instance[getter] === "function") {
                            addValue(playerName, instance[getter]());
                        }
                    } catch {}
                }

                for (const getter of ["getMedia", "getElement"]) {
                    try {
                        if (typeof instance[getter] === "function") {
                            const nested = instance[getter]();
                            addValue(playerName, nested);
                            inspectPlayer(playerName, nested);
                        }
                    } catch {}
                }

                for (const property of [
                    "src", "currentSrc", "source", "sources", "url", "levels", "quality",
                    "playlist", "options", "option", "config", "video", "media"
                ]) {
                    try {
                        addValue(playerName, instance[property]);
                    } catch {}
                }

                try {
                    addValue(playerName, instance.core?.activeContainer?.playback?.src);
                    addValue(playerName, instance.core?.activeContainer?.options);
                } catch {}
            };

            try {
                if (typeof globalThis.videojs?.getPlayers === "function") {
                    for (const player of Object.values(globalThis.videojs.getPlayers())) {
                        inspectPlayer("Video.js", player);
                    }
                }
            } catch {}

            try {
                if (typeof globalThis.flowplayer === "function") {
                    inspectPlayer("Flowplayer", globalThis.flowplayer());
                    addValue("Flowplayer", globalThis.flowplayer.instances);
                }
            } catch {}

            try {
                for (const player of Object.values(globalThis.mejs?.players || {})) {
                    inspectPlayer("MediaElement.js", player);
                    inspectPlayer("MediaElement.js", player?.media);
                }
            } catch {}

            try {
                for (const player of globalThis.Artplayer?.instances || []) {
                    inspectPlayer("ArtPlayer", player);
                }
            } catch {}

            for (const [name, playerName] of [
                ["player", "Web player"], ["videoPlayer", "Web player"],
                ["mediaPlayer", "Web player"], ["hls", "HLS player"],
                ["hlsPlayer", "HLS player"], ["dashPlayer", "DASH player"],
                ["shakaPlayer", "Shaka Player"], ["plyr", "Plyr"],
                ["clapprPlayer", "Clappr"], ["flowplayerPlayer", "Flowplayer"],
                ["fluidPlayerInstance", "Fluid Player"], ["fluidPlayerPlayer", "Fluid Player"],
                ["openPlayer", "OpenPlayerJS"], ["openplayer", "OpenPlayerJS"],
                ["openPlayerInstance", "OpenPlayerJS"], ["art", "ArtPlayer"],
                ["artPlayer", "ArtPlayer"], ["artplayer", "ArtPlayer"],
                ["dp", "DPlayer"], ["dplayer", "DPlayer"], ["dPlayer", "DPlayer"],
                ["theoPlayer", "THEOplayer"], ["bitmovinPlayer", "Bitmovin Player"]
            ]) {
                try {
                    inspectPlayer(playerName, globalThis[name]);
                } catch {}
            }

            try {
                for (const element of document.querySelectorAll(
                    "[data-shaka-player-container], [data-shaka-player]")) {
                    const ui = element.ui || element.parentElement?.ui;
                    inspectPlayer("Shaka Player", ui?.getControls?.().getPlayer?.());
                }
            } catch {}

            const identifyElementPlayer = (element) => {
                try {
                    if (element.closest(".jwplayer, .jw-wrapper")) return "JW Player";
                    if (element.closest(".video-js")) return "Video.js";
                    if (element.closest("[data-shaka-player-container], [data-shaka-player]")) return "Shaka Player";
                    if (element.closest(".clappr-container")) return "Clappr";
                    if (element.closest(".flowplayer")) return "Flowplayer";
                    if (element.closest(".fluid_video_wrapper, [id^='fluid_video_wrapper']")) return "Fluid Player";
                    if (element.closest(".plyr")) return "Plyr";
                    if (element.closest(".mejs__container, mediaelementwrapper")) return "MediaElement.js";
                    if (element.closest(".op-player, .op-player__media")) return "OpenPlayerJS";
                    if (element.closest(".art-video-player, .artplayer-app")) return "ArtPlayer";
                    if (element.closest(".dplayer")) return "DPlayer";
                } catch {}
                return "HTML5 video";
            };

            for (const element of document.querySelectorAll(
                "video, source, [data-src], [data-source], [data-file], [data-video], [data-url], [data-hls], [data-mpd]")) {
                const playerName = identifyElementPlayer(element);
                for (const attribute of ["src", "data-src", "data-source", "data-file", "data-video", "data-url", "data-hls", "data-mpd"]) {
                    addUrl(playerName, element.getAttribute(attribute));
                }
            }

            let scannedCharacters = 0;
            for (const script of document.scripts) {
                if (scannedCharacters >= 1_000_000) break;
                const text = (script.textContent || "").slice(0, 250_000);
                scannedCharacters += text.length;
                const normalized = text.replace(/\\\//g, "/").replace(/\\u0026/gi, "&");
                const matches = normalized.match(/https?:\/\/[^\s"'<>]+?\.(?:m3u8|mpd|mp4)(?:[?#][^\s"'<>]*)?/gi) || [];
                for (const match of matches.slice(0, 50)) addUrl("Inline player configuration", match);
            }

            return results;
        }
        """;

    public async Task<IReadOnlyList<KnownPlayerSource>> ExtractAsync(IFrame frame)
    {
        var result = await frame.EvaluateAsync<JsonElement>(DiscoveryScript);
        return Parse(result);
    }

    internal static IReadOnlyList<KnownPlayerSource> Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sources = new Dictionary<string, KnownPlayerSource>(StringComparer.Ordinal);
        foreach (var item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                || !MediaTypeDetector.IsCandidate(url))
            {
                continue;
            }

            var player = item.TryGetProperty("player", out var playerElement)
                         && playerElement.ValueKind == JsonValueKind.String
                ? playerElement.GetString()
                : null;
            sources.TryAdd(url.AbsoluteUri, new KnownPlayerSource(
                string.IsNullOrWhiteSpace(player) ? "Web player" : player,
                url));
        }

        return [.. sources.Values];
    }
}

internal sealed record KnownPlayerSource(string Player, Uri Url);
