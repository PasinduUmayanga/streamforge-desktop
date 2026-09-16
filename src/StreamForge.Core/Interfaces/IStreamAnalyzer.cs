using StreamForge.Core.Models;

namespace StreamForge.Core.Interfaces;

public interface IStreamAnalyzer
{
    bool CanAnalyze(MediaStream stream);

    Task<MediaStream> AnalyzeAsync(MediaStream stream, CancellationToken cancellationToken);
}
