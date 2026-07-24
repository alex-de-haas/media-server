namespace MediaServer.Api.Recommendations;

/// <summary>One provider's ranked contribution to the merged feed.</summary>
public sealed record RankedList(string ProviderKey, IReadOnlyList<RecommendationCandidate> Candidates);

/// <summary>A merged suggestion, and which sources put it there.</summary>
public sealed record FusedRecommendation(
    RecommendationIdentity Identity,
    string Title,
    int? Year,
    string? PosterUrl,
    /// <summary>Provider keys that suggested this, in registry order. More than one means agreement.</summary>
    IReadOnlyList<string> Sources,
    double Score);

/// <summary>
/// Merges several providers' ranked lists into one feed.
/// </summary>
/// <remarks>
/// Rank-based by necessity: Trakt returns an ordered list with no scores, TMDb returns vote metadata
/// on items, and the two scales are incommensurable — any arithmetic mixing them would be inventing a
/// common unit. Reciprocal rank fusion needs only position, which both lists genuinely have.
///
/// A title both engines chose gets a multiplicative boost. Two independent engines, built on
/// different data (this library's history versus a whole tracking community's), landing on the same
/// title is the strongest evidence available here — stronger than either one ranking it highly alone.
/// </remarks>
public static class RecommendationFusion
{
    /// <summary>
    /// RRF's damping constant. The standard k=60 keeps any single list from dominating through its
    /// top entry alone, which is exactly the failure a small feed would show first.
    /// </summary>
    internal const double RankDamping = 60.0;

    /// <summary>Per extra provider that agrees. Multiplicative, so agreement lifts a title without erasing rank.</summary>
    internal const double AgreementBoost = 1.5;

    public static IReadOnlyList<FusedRecommendation> Fuse(IReadOnlyList<RankedList> lists, int limit)
    {
        var merged = new Dictionary<RecommendationIdentity, Entry>();

        foreach (var list in lists)
        {
            foreach (var candidate in list.Candidates)
            {
                if (!merged.TryGetValue(candidate.Identity, out var entry))
                {
                    entry = new Entry(candidate);
                    merged[candidate.Identity] = entry;
                }

                entry.Add(list.ProviderKey, candidate, 1.0 / (RankDamping + candidate.Rank + 1));
            }
        }

        return [.. merged.Values
            .Select(entry => entry.ToResult())
            .OrderByDescending(result => result.Score)
            // Deterministic tail: without this, equally scored titles would shuffle between requests.
            .ThenBy(result => result.Identity.TmdbId, StringComparer.Ordinal)
            .Take(limit)];
    }

    private sealed class Entry(RecommendationCandidate first)
    {
        private readonly List<string> sources = [];
        private RecommendationCandidate best = first;
        private double score;

        public void Add(string providerKey, RecommendationCandidate candidate, double contribution)
        {
            // A provider appearing twice for one title (both of its kind lists, say) must not count as
            // agreement with itself.
            if (!sources.Contains(providerKey, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(providerKey);
            }

            score += contribution;

            // Prefer whichever source actually supplied artwork: Trakt carries none, so a title both
            // sources chose should still arrive with the poster TMDb gave it.
            if (best.PosterUrl is null && candidate.PosterUrl is not null)
            {
                best = candidate;
            }
        }

        public FusedRecommendation ToResult()
        {
            var boosted = score * Math.Pow(AgreementBoost, sources.Count - 1);
            return new FusedRecommendation(best.Identity, best.Title, best.Year, best.PosterUrl, sources, boosted);
        }
    }
}
