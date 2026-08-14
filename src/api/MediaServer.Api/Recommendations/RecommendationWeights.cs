namespace MediaServer.Api.Recommendations;

/// <summary>
/// Every number the ranking can be argued about, in one place.
/// </summary>
/// <remarks>
/// These were constants, and as constants they were assertions: each one is a claim about how much a
/// signal is worth, and none of them could be checked. Gathering them here changes no behaviour —
/// <see cref="Default"/> is exactly what was hard-coded — but it makes the claims measurable, which
/// is the difference between a tuned engine and a plausible one.
/// <para>
/// Nothing in the running app ever passes anything but <see cref="Default"/>. The offline evaluation
/// harness is the only caller that varies them, and it does so against a real history, because a
/// sweep over synthetic data would only measure the generator that produced it.
/// </para>
/// </remarks>
public sealed record RecommendationWeights
{
    /// <summary>What the app runs on. Every value is the one that was previously a constant.</summary>
    public static RecommendationWeights Default { get; } = new();

    /// <summary>What each star is worth as a seed, relative to <see cref="UnratedWeight"/>.</summary>
    public IReadOnlyDictionary<int, double> RatingWeights { get; init; } = new Dictionary<int, double>
    {
        [3] = 1.7,
        [4] = 4.0,
        [5] = 6.5,
    };

    /// <summary>The weight of an ordinary watch nobody rated — the unit every rating is priced in.</summary>
    public double UnratedWeight { get; init; } = 1.0;

    /// <summary>A favorite says something an ordinary play does not. Unrated titles only.</summary>
    public double FavoriteBoost { get; init; } = 1.5;

    /// <summary>Rewatching is the strongest signal a viewer gives without saying anything.</summary>
    public double RewatchBoost { get; init; } = 1.25;

    /// <summary>How many seeds are chosen purely by weight; the rest is the recency reserve.</summary>
    public int WeightedSeeds { get; init; } = 16;

    /// <summary>How much a candidate looking like what the viewer likes is worth.</summary>
    public double AffinityWeight { get; init; } = 0.6;

    /// <summary>How hard a resemblance to what the viewer rejected pushes back.</summary>
    public double AversionWeight { get; init; } = 0.8;

    /// <summary>How much the smoothed community score counts as a tiebreak.</summary>
    public double QualityWeight { get; init; } = 0.25;
}
