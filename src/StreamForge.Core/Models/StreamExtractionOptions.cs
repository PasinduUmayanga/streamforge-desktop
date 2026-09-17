namespace StreamForge.Core.Models;

public sealed class StreamExtractionOptions
{
    public bool EnableLocalAiAdvisor { get; init; }

    public Uri LocalAiEndpoint { get; init; } = new("http://localhost:11434");

    public string LocalAiModel { get; init; } = "qwen3-coder-next";
}
