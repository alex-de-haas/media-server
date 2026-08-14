namespace MediaServer.Api.Recommendations;

/// <summary>
/// Why a card is in the feed, as data rather than as a sentence.
/// </summary>
/// <remarks>
/// Structured on purpose. The server knows <em>what</em> produced a candidate; only the client knows
/// how its own surface phrases things, how much room a line has, and what language the reader wants.
/// Shipping a composed English sentence would put all three of those decisions in the wrong place.
/// <para>
/// The contributions are computed either way, so keeping the argmax costs nothing and is the
/// difference between a list and an explanation.
/// </para>
/// </remarks>
/// <param name="Kind">Which sort of explanation this is; see the constants below.</param>
/// <param name="Detail">The thing to name — a seed title, a person, a franchise. Null for reasons with nothing to name.</param>
/// <param name="Rating">The stars the viewer gave the seed, when they gave any. The most convincing reason available.</param>
public sealed record RecommendationReason(string Kind, string? Detail = null, int? Rating = null)
{
    /// <summary>Because you watched <c>Detail</c>.</summary>
    public const string Seed = "seed";

    /// <summary>Because you rated <c>Detail</c> <c>Rating</c> stars.</summary>
    public const string RatedSeed = "rated-seed";

    /// <summary>Because it is part of <c>Detail</c>, a franchise already started.</summary>
    public const string Franchise = "franchise";

    /// <summary>Because of <c>Detail</c>, someone whose work this viewer keeps choosing.</summary>
    public const string Person = "person";

    /// <summary>Because it is already in the library and matches what this viewer likes.</summary>
    public const string InLibrary = "in-library";

    /// <summary>Because it matches the viewer's taste, without anything having linked it to a title.</summary>
    public const string Taste = "taste";
}
