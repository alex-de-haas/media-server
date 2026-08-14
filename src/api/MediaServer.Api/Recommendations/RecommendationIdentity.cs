namespace MediaServer.Api.Recommendations;

/// <summary>What a recommendation is about: a whole movie or a whole series, never one episode.</summary>
/// <remarks>
/// "Watch this next" is a title-level answer. An episode-level recommendation would either duplicate
/// Next Up (which already knows where the user is in a series) or suggest starting mid-season.
/// </remarks>
public enum RecommendationKind
{
    Movie,
    Series,
}

/// <summary>
/// A recommended title, addressed the way everything here addresses one: by TMDb id.
/// </summary>
/// <remarks>
/// The shared coordinate between what TMDb returns, what the generators pool, and what the local
/// library holds — it is what lets two generators recognize the same title, and what maps a candidate
/// onto a library item so a card can offer Play rather than Track.
/// <para>
/// Kind is part of the identity because TMDb's movie and tv id spaces overlap: a film and a show can
/// share a number and are not the same title.
/// </para>
/// </remarks>
public readonly record struct RecommendationIdentity(RecommendationKind Kind, string TmdbId)
{
    public override string ToString() => $"{Kind}:{TmdbId}";
}
