using StreamForge.Core.Interfaces;

namespace StreamForge.Infrastructure.Ffmpeg;

public sealed class FfmpegLocator : IFfmpegLocator
{
    public string? Locate()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var bundled = Path.Combine(baseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var configured = Environment.GetEnvironmentVariable("STREAMFORGE_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "ffmpeg";
    }
}
