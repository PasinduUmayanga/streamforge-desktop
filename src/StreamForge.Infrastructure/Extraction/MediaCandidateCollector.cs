using System.Collections.Concurrent;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

internal sealed class MediaCandidateCollector
{
    private readonly ConcurrentDictionary<string, MediaCandidate> _candidates =
        new(StringComparer.Ordinal);
    private long _lastCandidateTicks;

    public int Count => _candidates.Count;

    public DateTimeOffset? LastCandidateAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastCandidateTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Add(MediaCandidate candidate)
    {
        if (candidate.Type == MediaSourceType.Unknown || MediaTypeDetector.ShouldIgnore(candidate.Url))
        {
            return;
        }

        var changed = false;
        _candidates.AddOrUpdate(
            candidate.Url.AbsoluteUri,
            _ =>
            {
                changed = true;
                return candidate;
            },
            (_, existing) =>
            {
                var merged = Merge(existing, candidate);
                changed = HasMaterialDifference(existing, merged);
                return merged;
            });

        if (changed)
        {
            Interlocked.Exchange(ref _lastCandidateTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        }
    }

    public IReadOnlyList<MediaCandidate> Snapshot() => [.. _candidates.Values];

    private static MediaCandidate Merge(MediaCandidate existing, MediaCandidate incoming)
    {
        var preferred = CandidateRanker.SourceScore(incoming.Source) > CandidateRanker.SourceScore(existing.Source)
            ? incoming
            : existing;

        return new MediaCandidate
        {
            Url = existing.Url,
            Type = existing.Type != MediaSourceType.Unknown ? existing.Type : incoming.Type,
            Source = preferred.Source,
            Headers = MergeHeaders(existing.Headers, incoming.Headers),
            ResourceType = incoming.ResourceType ?? existing.ResourceType,
            ContentType = incoming.ContentType ?? existing.ContentType,
            Status = incoming.Status ?? existing.Status,
            IsMasterPlaylist = existing.IsMasterPlaylist || incoming.IsMasterPlaylist,
            FrameUrl = preferred.FrameUrl ?? incoming.FrameUrl ?? existing.FrameUrl,
            CapturedAt = existing.CapturedAt <= incoming.CapturedAt ? existing.CapturedAt : incoming.CapturedAt
        };
    }

    private static IReadOnlyDictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string> incoming)
    {
        var result = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var header in incoming)
        {
            result[header.Key] = header.Value;
        }

        return result;
    }

    private static bool HasMaterialDifference(MediaCandidate existing, MediaCandidate merged)
    {
        return existing.Type != merged.Type
            || existing.Source != merged.Source
            || existing.Status != merged.Status
            || existing.IsMasterPlaylist != merged.IsMasterPlaylist
            || !string.Equals(existing.ResourceType, merged.ResourceType, StringComparison.Ordinal)
            || !string.Equals(existing.ContentType, merged.ContentType, StringComparison.Ordinal)
            || !HeadersEqual(existing.Headers, merged.Headers);
    }

    private static bool HeadersEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        return left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}
