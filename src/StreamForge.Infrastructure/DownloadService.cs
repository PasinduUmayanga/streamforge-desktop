using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure;

public sealed class DownloadService(IFfmpegService ffmpegService) : IDownloadService
{
    public Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        return ffmpegService.DownloadAsync(request, progress, cancellationToken);
    }
}
