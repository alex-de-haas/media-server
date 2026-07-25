using MediaServer.Api.Library;

namespace MediaServer.Api.Metadata;

/// <summary>
/// What the preview dialog shows about one title, held or not.
/// </summary>
/// <remarks>
/// Field names and types deliberately match <see cref="LibraryDetailDto"/> wherever they overlap, so the
/// web layer formats a preview with the helpers it formats a detail page with. It carries no playback,
/// version or file information: those exist only for a title the instance holds, and that title has a
/// full detail page — <see cref="InLibrary"/> and <see cref="MediaItemId"/> are how the preview links to it.
/// </remarks>
public sealed record TitlePreviewDto(
    string Provider,
    string ProviderId,
    string Kind,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? Tagline,
    IReadOnlyList<string> Genres,
    string? PosterUrl,
    string? BackdropUrl,
    string? OfficialRating,
    double? CommunityRating,
    int? VoteCount,
    long? RuntimeTicks,
    // Production status (Released, Ended, Returning Series, …).
    string? Status,
    // Totals per the provider (series only); the instance may hold fewer, or none.
    int? SeasonCount,
    int? EpisodeCount,
    IReadOnlyList<string> Directors,
    IReadOnlyList<string> Creators,
    IReadOnlyList<CastMemberDto> Cast,
    string? TrailerUrl,
    string? ImdbId,
    string? Homepage,
    // True when a published movie/series with this identity exists here; MediaItemId then links to it.
    bool InLibrary,
    Guid? MediaItemId);
