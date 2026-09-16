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
}
