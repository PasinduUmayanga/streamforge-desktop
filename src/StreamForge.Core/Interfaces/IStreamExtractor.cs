using StreamForge.Core.Models;

namespace StreamForge.Core.Interfaces;

public interface IStreamExtractor
{
    Task<MediaStream?> ExtractAsync(
        Uri pageUrl,
        IProgress<NetworkActivity>? activity,
        CancellationToken cancellationToken);
}
