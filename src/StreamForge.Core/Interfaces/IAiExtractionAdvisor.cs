using StreamForge.Core.Models;

namespace StreamForge.Core.Interfaces;

public interface IAiExtractionAdvisor
{
    Task<AiExtractionSuggestion?> AdviseAsync(
        ExtractionDiagnostics diagnostics,
        StreamExtractionOptions options,
        CancellationToken cancellationToken);
}
