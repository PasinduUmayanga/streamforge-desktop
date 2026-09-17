using StreamForge.Core.Models;
using StreamForge.Infrastructure.Ffmpeg;

namespace StreamForge.Infrastructure.Tests;

public sealed class FfmpegArgumentBuilderTests
{
    [Fact]
    public void Build_AddsHeadersAndUsesSelectedQualityUrl()
    {
        var request = new DownloadRequest
        {
            Stream = new MediaStream
            {
                Url = new Uri("https://cdn.example.com/master.m3u8"),
                Type = MediaSourceType.Hls,
                UserAgent = "Test Agent",
                Referer = "https://example.com/",
                Origin = "https://example.com"
            },
            Quality = new StreamQuality
            {
                Label = "1080p",
                Url = new Uri("https://cdn.example.com/1080/index.m3u8")
            },
            OutputPath = @"C:\Videos\video.mp4"
        };

        var args = FfmpegArgumentBuilder.Build(request);

        Assert.Contains("-user_agent", args);
        Assert.Contains("Test Agent", args);
        Assert.Contains("-referer", args);
        Assert.Contains("https://example.com/", args);
        Assert.Contains("Origin: https://example.com", args);
        Assert.Contains("https://cdn.example.com/1080/index.m3u8", args);
        Assert.Equal(@"C:\Videos\video.mp4", args[^1]);
    }

    [Fact]
    public void Build_AddsReconnectOptionsBeforeInput()
    {
        var request = new DownloadRequest
        {
            Stream = new MediaStream
            {
                Url = new Uri("https://cdn.example.com/master.m3u8"),
                Type = MediaSourceType.Hls
            },
            Quality = new StreamQuality
            {
                Label = "Auto",
                Url = new Uri("https://cdn.example.com/master.m3u8")
            },
            OutputPath = @"C:\Videos\video.mp4"
        };

        var args = FfmpegArgumentBuilder.Build(request);
        var inputIndex = args.ToList().IndexOf("-i");

        Assert.True(inputIndex > 0);
        Assert.True(args.ToList().IndexOf("-reconnect") < inputIndex);
        Assert.Contains("-reconnect_streamed", args);
        Assert.Contains("-reconnect_delay_max", args);
    }
}
