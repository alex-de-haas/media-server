using MediaServer.Api.Recommendations;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// Fusion is where the feature's central claim lives: that two independent engines agreeing on a
/// title means more than either one loving it alone.
/// </summary>
public sealed class RecommendationFusionTests
{
    private static RecommendationCandidate Candidate(string tmdbId, int rank, string? poster = null) =>
        new(new RecommendationIdentity(RecommendationKind.Movie, tmdbId), $"Title {tmdbId}", 2024, poster, rank);

    private static RankedList List(string provider, params RecommendationCandidate[] candidates) =>
        new(provider, candidates);

    [Fact]
    public void ATitleBothProvidersChoseOutranksOneOnlyTheTopOfAnotherListCarries()
    {
        // The whole point of fusing rather than concatenating: "agreed" beats "ranked first once".
        var fused = RecommendationFusion.Fuse(
            [
                List("library", Candidate("solo", 0), Candidate("shared", 5)),
                List("trakt", Candidate("shared", 5)),
            ],
            10);

        Assert.Equal("shared", fused[0].Identity.TmdbId);
        Assert.Equal(["library", "trakt"], fused[0].Sources);
    }

    [Fact]
    public void WithinOneProviderPositionStillDecides()
    {
        var fused = RecommendationFusion.Fuse(
            [List("library", Candidate("first", 0), Candidate("second", 1), Candidate("third", 2))],
            10);

        Assert.Equal(["first", "second", "third"], fused.Select(entry => entry.Identity.TmdbId));
    }

    [Fact]
    public void OneProviderMeansTheMergeIsIdentity()
    {
        // The common case — no Trakt connection — must not be distorted by machinery meant for two.
        var only = new[] { Candidate("a", 0), Candidate("b", 1), Candidate("c", 2) };

        var fused = RecommendationFusion.Fuse([List("library", only)], 10);

        Assert.Equal(
            only.Select(candidate => candidate.Identity.TmdbId),
            fused.Select(entry => entry.Identity.TmdbId));
        Assert.All(fused, entry => Assert.Equal("library", Assert.Single(entry.Sources)));
    }

    [Fact]
    public void NoProvidersMeansAnEmptyFeedRatherThanAnError()
    {
        Assert.Empty(RecommendationFusion.Fuse([], 10));
        Assert.Empty(RecommendationFusion.Fuse([List("library")], 10));
    }

    [Fact]
    public void OneProviderListingATitleTwiceIsNotAgreementWithItself()
    {
        // Trakt returns movies and shows as separate lists; a title reaching the merge twice from one
        // source must not be credited as two engines agreeing.
        var fused = RecommendationFusion.Fuse(
            [List("trakt", Candidate("dup", 0), Candidate("dup", 1), Candidate("other", 0))],
            10);

        var duplicate = Assert.Single(fused, entry => entry.Identity.TmdbId == "dup");
        Assert.Equal("trakt", Assert.Single(duplicate.Sources));
    }

    [Fact]
    public void AgreementSurvivesAWeakPositionInBothLists()
    {
        // Two engines quietly agreeing near the bottom is still a stronger signal than one shouting.
        var fused = RecommendationFusion.Fuse(
            [
                List("library", Candidate("loud", 0), Candidate("quiet", 30)),
                List("trakt", Candidate("quiet", 30)),
            ],
            10);

        Assert.Equal("quiet", fused[0].Identity.TmdbId);
    }

    [Fact]
    public void ThePosterFromWhicheverSourceHadOneIsKept()
    {
        // Trakt carries no artwork, so a jointly recommended title must still show TMDb's poster
        // regardless of which list reached the merge first.
        var traktFirst = RecommendationFusion.Fuse(
            [
                List("trakt", Candidate("shared", 0)),
                List("library", Candidate("shared", 0, poster: "https://img/p.jpg")),
            ],
            10);

        Assert.Equal("https://img/p.jpg", Assert.Single(traktFirst).PosterUrl);
    }

    [Fact]
    public void TheLimitIsHonoured()
    {
        var many = Enumerable.Range(0, 30).Select(index => Candidate($"c{index}", index)).ToArray();

        Assert.Equal(5, RecommendationFusion.Fuse([List("library", many)], 5).Count);
    }

    [Fact]
    public void EquallyScoredTitlesComeBackInAStableOrder()
    {
        // Without a deterministic tiebreak the feed would reshuffle between identical requests.
        var lists = new[] { List("library", Candidate("b", 0)), List("trakt", Candidate("a", 0)) };

        var first = RecommendationFusion.Fuse(lists, 10).Select(entry => entry.Identity.TmdbId);
        var second = RecommendationFusion.Fuse(lists, 10).Select(entry => entry.Identity.TmdbId);

        Assert.Equal(first, second);
        Assert.Equal(["a", "b"], first);
    }

    [Fact]
    public void KindIsPartOfIdentitySoAMovieAndAShowSharingAnIdDoNotMerge()
    {
        // TMDb numbers movies and shows separately; treating id alone as the key would fuse unrelated
        // titles into one card.
        var movie = new RecommendationCandidate(
            new RecommendationIdentity(RecommendationKind.Movie, "42"), "A Movie", 2020, null, 0);
        var series = new RecommendationCandidate(
            new RecommendationIdentity(RecommendationKind.Series, "42"), "A Show", 2021, null, 0);

        var fused = RecommendationFusion.Fuse([List("library", movie), List("trakt", series)], 10);

        Assert.Equal(2, fused.Count);
        Assert.All(fused, entry => Assert.Single(entry.Sources));
    }
}
