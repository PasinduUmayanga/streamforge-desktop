namespace StreamForge.Infrastructure.Extraction;

internal enum PlayerSeedSource
{
    NetworkApi,
    PlayerElement,
    EmbedElement,
    PlayerMetadata,
    SeedResponse
}

internal sealed class PlayerSeedLink
{
    public required Uri Url { get; init; }

    public PlayerSeedSource Source { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    public string? FrameUrl { get; init; }

    public int Depth { get; init; }
}
