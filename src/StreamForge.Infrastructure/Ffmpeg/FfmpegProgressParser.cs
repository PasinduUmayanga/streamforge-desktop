using System.Globalization;
using System.Text.RegularExpressions;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Ffmpeg;

public sealed class FfmpegProgressParser
{
    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(?<duration>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;
    private readonly object _sync = new();
    private DateTimeOffset _lastSampleAt;
    private long _lastBytes;
    private double? _smoothedBytesPerSecond;
    private TimeSpan? _duration;
    private DateTimeOffset? _pausedAt;
    private TimeSpan _totalPausedTime;

    public FfmpegProgressParser(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = _timeProvider.GetUtcNow();
        _lastSampleAt = _startedAt;
    }

    public DownloadProgress? ParseLine(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        lock (_sync)
        {
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            _fields[key] = value;

            if (!key.Equals("progress", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _fields.TryGetValue("total_size", out var totalSizeValue);
            _fields.TryGetValue("out_time", out var outTimeValue);
            _fields.TryGetValue("speed", out var speedValue);

            long.TryParse(totalSizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes);
            var currentTime = ParseTime(outTimeValue);
            var speedFactor = ParseSpeedFactor(speedValue);
            var now = _timeProvider.GetUtcNow();
            var currentPauseTime = _pausedAt is { } pausedAt ? now - pausedAt : TimeSpan.Zero;
            var elapsed = now - _startedAt - _totalPausedTime - currentPauseTime;

            UpdateTransferSpeed(bytes, now);

            double? percentage = null;
            TimeSpan? estimatedTimeRemaining = null;
            if (_duration is { Ticks: > 0 } duration && currentTime is { } mediaTime)
            {
                percentage = Math.Clamp(mediaTime.TotalMilliseconds / duration.TotalMilliseconds * 100, 0, 100);
                if (speedFactor is > 0)
                {
                    var remainingMediaTime = duration - mediaTime;
                    estimatedTimeRemaining = remainingMediaTime <= TimeSpan.Zero
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(remainingMediaTime.TotalSeconds / speedFactor.Value);
                }
            }

            if (value.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                percentage = 100;
                estimatedTimeRemaining = TimeSpan.Zero;
            }

            return new DownloadProgress
            {
                Percentage = percentage,
                BytesDownloaded = bytes,
                BytesPerSecond = _smoothedBytesPerSecond,
                EstimatedTimeRemaining = estimatedTimeRemaining,
                Elapsed = elapsed,
                Speed = speedValue,
                CurrentTime = outTimeValue
            };
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            _pausedAt ??= _timeProvider.GetUtcNow();
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_pausedAt is not { } pausedAt)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            _totalPausedTime += now - pausedAt;
            _pausedAt = null;
            _lastSampleAt = now;
            _smoothedBytesPerSecond = null;
        }
    }

    public void ParseDiagnosticLine(string line)
    {
        var match = DurationPattern.Match(line);
        if (!match.Success)
        {
            return;
        }

        var duration = ParseTime(match.Groups["duration"].Value);
        if (duration is null)
        {
            return;
        }

        lock (_sync)
        {
            _duration = duration;
        }
    }

    private void UpdateTransferSpeed(long bytes, DateTimeOffset now)
    {
        var sampleDuration = now - _lastSampleAt;
        var byteDifference = bytes - _lastBytes;
        if (sampleDuration < TimeSpan.FromMilliseconds(200) || byteDifference < 0)
        {
            return;
        }

        var currentBytesPerSecond = byteDifference / sampleDuration.TotalSeconds;
        _smoothedBytesPerSecond = _smoothedBytesPerSecond is null
            ? currentBytesPerSecond
            : (_smoothedBytesPerSecond.Value * 0.65) + (currentBytesPerSecond * 0.35);

        _lastBytes = bytes;
        _lastSampleAt = now;
    }

    private static TimeSpan? ParseTime(string? value)
    {
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static double? ParseSpeedFactor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var numericValue = value.Trim().TrimEnd('x');
        return double.TryParse(numericValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
