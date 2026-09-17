using StreamForge.Core.Models;

namespace StreamForge.Core.Interfaces;

public interface IStreamExtractor
{
    Task<StreamExtractionResult> ExtractAsync(
        Uri pageUrl,
        StreamExtractionOptions options,
        IProgress<NetworkActivity>? activity,
        IProgress<IdentificationStepUpdate>? steps,
        CancellationToken cancellationToken);
}
