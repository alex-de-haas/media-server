using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Profile;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The re-rank: relevance traded against variety, plus the caps that stop one franchise, one
/// director or one genre from owning the page.
/// </summary>
public sealed class RecommendationRerankerTests
{
    [Fact]
    public void WithNothingInCommonTheScoreOrderSurvives()
    {
        // Diversity that reorders titles which are not alike is not diversity, it is a shuffle.
        var ranked = new[]
        {
            Candidate("a", 0.9, "action"),
            Candidate("b", 0.8, "comedy"),
            Candidate("c", 0.7, "documentary"),
        };

        var result = Rerank(ranked, limit: 3);

        Assert.Equal(["a", "b", "c"], result.Select(entry => entry.Identity.TmdbId));
    }

    [Fact]
    public void ASlightlyWorseCandidateThatIsDifferentBeatsANearDuplicate()
    {
        // Three near-identical thrillers at the top is a list the viewer already has.
        var ranked = new[]
        {
            Candidate("first", 0.90, "thriller", "crime"),
            Candidate("clone", 0.88, "thriller", "crime"),
            Candidate("other", 0.80, "comedy"),
        };

        var result = Rerank(ranked, limit: 2);

        Assert.Equal(["first", "other"], result.Select(entry => entry.Identity.TmdbId));
    }

    [Fact]
    public void AFranchiseCannotMarchDownThePage()
    {
        var ranked = new[]
        {
            Candidate("saga-1", 0.99, "action"),
            Candidate("saga-2", 0.98, "action"),
            Candidate("saga-3", 0.97, "action"),
            Candidate("elsewhere", 0.10, "comedy"),
        };
        var grouping = new Dictionary<string, CandidateGrouping>
        {
            ["saga-1"] = new("saga", []),
            ["saga-2"] = new("saga", []),
            ["saga-3"] = new("saga", []),
        };

        var result = Rerank(ranked, limit: 4, grouping);

        Assert.Equal(2, result.Count(entry => entry.Identity.TmdbId.StartsWith("saga", StringComparison.Ordinal)));
        Assert.Contains(result, entry => entry.Identity.TmdbId == "elsewhere");
    }

    [Fact]
    public void OneDirectorCannotTakeThePageEither()
    {
        var ranked = new[]
        {
            Candidate("auteur-1", 0.99, "drama"),
            Candidate("auteur-2", 0.98, "drama"),
            Candidate("auteur-3", 0.97, "drama"),
        };
        var grouping = new Dictionary<string, CandidateGrouping>
        {
            ["auteur-1"] = new(null, ["nolan"]),
            ["auteur-2"] = new(null, ["nolan"]),
            ["auteur-3"] = new(null, ["nolan"]),
        };

        var result = Rerank(ranked, limit: 3, grouping);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NoGenreTakesMoreThanItsShareOnceTheListIsLongEnoughToHaveOne()
    {
        var ranked = new List<RankedCandidate>();
        for (var index = 0; index < 8; index++)
        {
            ranked.Add(Candidate($"horror-{index}", 0.9 - (index * 0.01), "horror"));
        }

        for (var index = 0; index < 4; index++)
        {
            ranked.Add(Candidate($"other-{index}", 0.5 - (index * 0.01), $"genre-{index}"));
        }

        var result = Rerank(ranked, limit: 10);

        var horror = result.Count(entry => entry.Identity.TmdbId.StartsWith("horror", StringComparison.Ordinal));
        Assert.True(horror <= (int)Math.Ceiling(RecommendationReranker.MaxGenreShare * result.Count));
        Assert.True(result.Count > horror);
    }

    [Fact]
    public void AShortListIsNotSubjectToTheGenreShare()
    {
        // Below five picks a share cap is an argument with itself: two of three is already 66%.
        var ranked = new[]
        {
            Candidate("a", 0.9, "horror"),
            Candidate("b", 0.8, "horror"),
        };

        Assert.Equal(2, Rerank(ranked, limit: 2).Count);
    }

    [Fact]
    public void RunningOutOfAllowedCandidatesStopsShortRatherThanBreakingACap()
    {
        // A short honest list beats a full one that is the franchise the caps exist to hold back.
        var ranked = new[]
        {
            Candidate("saga-1", 0.9, "action"),
            Candidate("saga-2", 0.8, "action"),
            Candidate("saga-3", 0.7, "action"),
        };
        var grouping = new Dictionary<string, CandidateGrouping>
        {
            ["saga-1"] = new("saga", []),
            ["saga-2"] = new("saga", []),
            ["saga-3"] = new("saga", []),
        };

        Assert.Equal(2, Rerank(ranked, limit: 3, grouping).Count);
    }

    [Fact]
    public void AnEmptyPoolAndAZeroLimitAreBothAnswers()
    {
        Assert.Empty(Rerank([], limit: 5));
        Assert.Empty(Rerank([Candidate("a", 0.5, "drama")], limit: 0));
    }

    private static IReadOnlyList<RankedCandidate> Rerank(
        IReadOnlyList<RankedCandidate> ranked,
        int limit,
        Dictionary<string, CandidateGrouping>? grouping = null) =>
        new RecommendationReranker().Rerank(
            ranked,
            limit,
            identity => grouping?.GetValueOrDefault(identity.TmdbId) ?? CandidateGrouping.None);

    private static RankedCandidate Candidate(string tmdbId, double score, params string[] genres)
    {
        var facets = new TitleFacets([.. genres.Select(genre => new WeightedFacet(FacetFamily.Genre, genre, 1))]);
        return new RankedCandidate(
            new RecommendationIdentity(RecommendationKind.Movie, tmdbId),
            new ScoredCandidate(
                score, 1, new TmdbRecommendedTitle(tmdbId, tmdbId, 2020, null), facets, ["seeds"]),
            score);
    }
}
