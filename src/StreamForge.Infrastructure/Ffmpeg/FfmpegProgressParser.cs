using System.Globalization;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Ffmpeg;

public sealed class FfmpegProgressParser
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public DownloadProgress? ParseLine(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        _fields[key] = value;

        if (!key.Equals("progress", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("total_size", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("out_time", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("speed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        _fields.TryGetValue("total_size", out var totalSizeValue);
        _fields.TryGetValue("out_time", out var outTime);
        _fields.TryGetValue("speed", out var speed);

        long.TryParse(totalSizeValue, CultureInfo.InvariantCulture, out var bytes);

        return new DownloadProgress
        {
            BytesDownloaded = bytes,
            Elapsed = DateTimeOffset.UtcNow - _startedAt,
            Speed = speed,
            CurrentTime = outTime
        };
    }
}
