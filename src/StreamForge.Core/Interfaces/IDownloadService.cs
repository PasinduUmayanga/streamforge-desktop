using StreamForge.Core.Models;

namespace StreamForge.Core.Interfaces;

public interface IDownloadService
{
    bool IsPaused { get; }

    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);

    bool TryPause();

    bool TryResume();
}
