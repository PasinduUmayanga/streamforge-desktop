using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Ffmpeg;

public static class FfmpegArgumentBuilder
{
    public static IReadOnlyList<string> Build(DownloadRequest request)
    {
        var stream = request.Stream;
        var inputUrl = request.Quality?.Url ?? stream.Url;
        var args = new List<string>
        {
            "-progress",
            "pipe:1",
            "-nostats"
        };

        if (!string.IsNullOrWhiteSpace(stream.UserAgent))
        {
            args.Add("-user_agent");
            args.Add(stream.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(stream.Referer))
        {
            args.Add("-referer");
            args.Add(stream.Referer);
        }

        if (!string.IsNullOrWhiteSpace(stream.Origin))
        {
            args.Add("-headers");
            args.Add($"Origin: {stream.Origin}");
        }

        args.Add("-i");
        args.Add(inputUrl.AbsoluteUri);
        args.Add("-c");
        args.Add("copy");
        args.Add(request.OutputPath);

        return args;
    }
}
