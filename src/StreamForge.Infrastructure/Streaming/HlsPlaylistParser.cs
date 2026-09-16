using System.Globalization;
using System.Text.RegularExpressions;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Streaming;

public static partial class HlsPlaylistParser
{
    public static IReadOnlyList<StreamQuality> ParseMasterPlaylist(string playlist, Uri playlistUri)
    {
        var lines = playlist
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var qualities = new List<StreamQuality>
        {
            new()
            {
                Label = "Auto",
                Url = playlistUri
            }
        };

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = line["#EXT-X-STREAM-INF:".Length..];
            var bandwidth = ParseLongAttribute(attributes, "BANDWIDTH");
            var (width, height) = ParseResolution(attributes);
            var streamLine = FindNextUriLine(lines, index + 1);

            if (streamLine is null)
            {
                continue;
            }

            var streamUri = new Uri(playlistUri, streamLine);
            qualities.Add(new StreamQuality
            {
                Label = height is null ? FormatBandwidthLabel(bandwidth) : $"{height}p",
                Width = width,
                Height = height,
                Bandwidth = bandwidth,
                Url = streamUri
            });
        }

        return qualities
            .GroupBy(q => q.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(q => q.Height ?? int.MaxValue)
            .ThenByDescending(q => q.Bandwidth ?? long.MaxValue)
            .ToArray();
    }

    public static bool IsMasterPlaylist(string playlist)
    {
        return playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDrmProtected(string playlist)
    {
        return playlist.Contains("widevine", StringComparison.OrdinalIgnoreCase)
            || playlist.Contains("playready", StringComparison.OrdinalIgnoreCase)
            || playlist.Contains("fairplay", StringComparison.OrdinalIgnoreCase)
            || playlist.Contains("cenc", StringComparison.OrdinalIgnoreCase)
            || playlist.Contains("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindNextUriLine(IReadOnlyList<string> lines, int start)
    {
        for (var index = start; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith('#'))
            {
                return lines[index];
            }
        }

        return null;
    }

    private static long? ParseLongAttribute(string attributes, string name)
    {
        var match = Regex.Match(attributes, $@"(?:^|,){name}=(\d+)", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static (int? Width, int? Height) ParseResolution(string attributes)
    {
        var match = ResolutionRegex().Match(attributes);
        if (!match.Success)
        {
            return (null, null);
        }

        var width = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return (width, height);
    }

    private static string FormatBandwidthLabel(long? bandwidth)
    {
        return bandwidth is null ? "Variant" : $"{bandwidth / 1_000_000d:0.#} Mbps";
    }

    [GeneratedRegex(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();
}
