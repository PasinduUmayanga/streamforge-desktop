using StreamForge.Core.Models;

namespace StreamForge.Core.Interfaces;

public interface IFfmpegService
{
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);
}
