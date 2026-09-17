namespace StreamForge.Infrastructure.Extraction;

internal static class CandidateRanker
{
    public static MediaCandidate? ChooseBest(IEnumerable<MediaCandidate> candidates)
    {
        return candidates
            .OrderByDescending(Score)
            .ThenBy(candidate => candidate.CapturedAt)
            .FirstOrDefault();
    }

    internal static int SourceScore(MediaCandidateSource source) => source switch
    {
        MediaCandidateSource.JwPlayer => 80,
        MediaCandidateSource.KnownPlayer => 75,
        MediaCandidateSource.VideoElement => 70,
        MediaCandidateSource.SeedProbe => 60,
        MediaCandidateSource.BrowserObservation => 55,
        MediaCandidateSource.ResponseContentType => 40,
        _ => 20
    };

    internal static int Score(MediaCandidate candidate)
    {
        var score = candidate.Type switch
        {
            Core.Models.MediaSourceType.Hls => 300,
            Core.Models.MediaSourceType.Dash => 400,
            Core.Models.MediaSourceType.Mp4 => 200,
            _ => 0
        };

        score += SourceScore(candidate.Source);
        score += candidate.IsMasterPlaylist ? 200 : 0;
        score += candidate.Status is >= 200 and < 400 ? 25 : 0;
        score -= candidate.Status is >= 400 ? 150 : 0;

        if (candidate.ResourceType?.Equals("media", StringComparison.OrdinalIgnoreCase) == true)
        {
            score += 35;
        }
        else if (candidate.ResourceType?.Equals("fetch", StringComparison.OrdinalIgnoreCase) == true
                 || candidate.ResourceType?.Equals("xhr", StringComparison.OrdinalIgnoreCase) == true)
        {
            score += 10;
        }

        if (candidate.Url.AbsolutePath.Contains("master", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (MediaTypeDetector.IsLikelyAdvertisement(candidate.Url))
        {
            score -= 500;
        }

        return score;
    }
}
