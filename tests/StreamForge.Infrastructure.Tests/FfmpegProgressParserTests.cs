using StreamForge.Infrastructure.Ffmpeg;

namespace StreamForge.Infrastructure.Tests;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void ParseLine_ReturnsProgressForMachineReadableFields()
    {
        var parser = new FfmpegProgressParser();
        foreach (var line in File.ReadAllLines(Path.Combine("Fixtures", "ffmpeg-progress.txt")))
        {
            parser.ParseLine(line);
        }

        var progress = parser.ParseLine("progress=continue");

        Assert.NotNull(progress);
        Assert.Equal(471859200, progress.BytesDownloaded);
        Assert.Equal("5.2x", progress.Speed);
        Assert.Equal("00:03:24.000000", progress.CurrentTime);
    }

    [Fact]
    public void ParseLine_CalculatesTransferSpeedPercentageAndEstimatedTime()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var parser = new FfmpegProgressParser(timeProvider);
        parser.ParseDiagnosticLine("  Duration: 00:02:00.000000, start: 0.000000, bitrate: 2500 kb/s");

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        parser.ParseLine("total_size=20971520");
        parser.ParseLine("out_time=00:00:30.000000");
        parser.ParseLine("speed=2.0x");
        var progress = parser.ParseLine("progress=continue");

        Assert.NotNull(progress);
        Assert.Equal(25, progress.Percentage);
        Assert.Equal(10 * 1024 * 1024, progress.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(45), progress.EstimatedTimeRemaining);
        Assert.Equal(TimeSpan.FromSeconds(2), progress.Elapsed);
    }

    [Fact]
    public void ParseLine_ReportsCompletionAtEnd()
    {
        var parser = new FfmpegProgressParser();

        var progress = parser.ParseLine("progress=end");

        Assert.NotNull(progress);
        Assert.Equal(100, progress.Percentage);
        Assert.Equal(TimeSpan.Zero, progress.EstimatedTimeRemaining);
    }

    [Fact]
    public void PauseAndResume_ExcludePausedTimeAndResetSpeedSample()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var parser = new FfmpegProgressParser(timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        parser.ParseLine("total_size=1048576");
        parser.ParseLine("progress=continue");

        parser.Pause();
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        parser.Resume();

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        parser.ParseLine("total_size=2097152");
        var progress = parser.ParseLine("progress=continue");

        Assert.NotNull(progress);
        Assert.Equal(TimeSpan.FromSeconds(4), progress.Elapsed);
        Assert.Equal(512 * 1024, progress.BytesPerSecond);
    }

    private sealed class TestTimeProvider(DateTimeOffset currentTime) : TimeProvider
    {
        private DateTimeOffset _currentTime = currentTime;

        public override DateTimeOffset GetUtcNow() => _currentTime;

        public void Advance(TimeSpan duration) => _currentTime += duration;
    }
}
