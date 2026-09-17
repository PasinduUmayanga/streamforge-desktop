using System.Collections.Concurrent;

namespace StreamForge.Infrastructure.Extraction;

internal sealed class PlayerSeedCollector
{
    private const int MaximumSeeds = 80;
    private readonly ConcurrentDictionary<string, PlayerSeedLink> _seeds = new(StringComparer.Ordinal);
    private long _lastSeedTicks;

    public int Count => _seeds.Count;

    public DateTimeOffset? LastSeedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSeedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public bool Add(PlayerSeedLink seed)
    {
        if (_seeds.Count >= MaximumSeeds
            || (!seed.Url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !seed.Url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || MediaTypeDetector.ShouldIgnore(seed.Url))
        {
            return false;
        }

        if (!_seeds.TryAdd(seed.Url.AbsoluteUri, seed))
        {
            return false;
        }

        Interlocked.Exchange(ref _lastSeedTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        return true;
    }

    public IReadOnlyList<PlayerSeedLink> Snapshot() => _seeds.Values
        .OrderBy(SeedPriority)
        .ThenBy(seed => seed.Depth)
        .ToArray();

    private static int SeedPriority(PlayerSeedLink seed) => seed.Source switch
    {
        PlayerSeedSource.PlayerMetadata => 0,
        PlayerSeedSource.EmbedElement => 1,
        PlayerSeedSource.PlayerElement => 2,
        PlayerSeedSource.SeedResponse => 3,
        _ => 4
    };
}
