using Microsoft.Extensions.Logging;
using StreamForge.Core.Exceptions;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Streaming;

public sealed class HlsStreamAnalyzer(HttpClient httpClient, ILogger<HlsStreamAnalyzer> logger) : IStreamAnalyzer
{
    public bool CanAnalyze(MediaStream stream) => stream.Type == MediaSourceType.Hls;

    public async Task<MediaStream> AnalyzeAsync(MediaStream stream, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        ApplyHeader(request, "User-Agent", stream.UserAgent);
        ApplyHeader(request, "Referer", stream.Referer);
        ApplyHeader(request, "Origin", stream.Origin);

        foreach (var header in stream.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new StreamForgeException($"HLS playlist could not be loaded. Server returned {(int)response.StatusCode}.");
        }

        var playlist = await response.Content.ReadAsStringAsync(cancellationToken);
        if (HlsPlaylistParser.IsDrmProtected(playlist))
        {
            throw new StreamForgeException("This stream appears to use DRM protection and cannot be processed by StreamForge.");
        }

        if (!HlsPlaylistParser.IsMasterPlaylist(playlist))
        {
            logger.LogInformation("Detected HLS media playlist without variants.");
            return stream.WithQualities([new StreamQuality { Label = "Auto", Url = stream.Url }]);
        }

        var qualities = HlsPlaylistParser.ParseMasterPlaylist(playlist, stream.Url);
        logger.LogInformation("Detected {QualityCount} HLS qualities.", qualities.Count);
        return stream.WithQualities(qualities);
    }

    private static void ApplyHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

file static class MediaStreamCloneExtensions
{
    public static MediaStream WithQualities(this MediaStream stream, IReadOnlyList<StreamQuality> qualities)
    {
        return new MediaStream
        {
            Url = stream.Url,
            Type = stream.Type,
            Referer = stream.Referer,
            Origin = stream.Origin,
            UserAgent = stream.UserAgent,
            Headers = stream.Headers,
            Qualities = qualities
        };
    }
}
