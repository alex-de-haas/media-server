namespace MediaServer.Api.Library;

// UI-facing DTOs for the internal `/api/library` surface. Serialized camelCase by the global JSON
// options (distinct from the Jellyfin PascalCase surface). Client ids are the internal Guid `Id`;
// `PublicId` is included only for clients that need the stable Jellyfin id. These DTOs are projected
// from the domain by LibraryReadService and never reference any Jellyfin type beyond the shared
// surface-neutral UserItemDataDto.

/// <summary>A browsable top-level library item (movie or series) for grids.</summary>
public sealed record LibraryItemDto(
    Guid Id,
    string? PublicId,
    Guid CatalogId,
    string Kind,
    string Title,
    int? Year,
    string? PosterUrl,
    UserItemDataDto? UserData);

/// <summary>Full detail for a movie or series detail page.</summary>
public sealed record LibraryDetailDto(
    Guid Id,
    string? PublicId,
    // TMDb id (movie id for a movie, series id for a series) — lets the UI build an Infuse library deep link.
    string? TmdbId,
    Guid CatalogId,
    // The catalog this item lives in — its name and root host path — shown on the media tab so an operator
    // can see where it sits on disk.
    string CatalogName,
    string CatalogRoot,
    string Kind,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? Tagline,
    IReadOnlyList<string> Genres,
    string? OfficialRating,
    double? CommunityRating,
    long? RuntimeTicks,
    int? IndexNumber,
    int? ParentIndexNumber,
    string? PosterUrl,
    string? BackdropUrl,
    // Title artwork (TMDb "logo": the styled title as a transparent PNG), language-matched when available.
    string? LogoUrl,
    string? LibraryPath,
    // The catalog-root-relative folder that holds this title's files ("Inception (2010)", "The Show (2020)");
    // null when the item has no file yet. Shown on the media/episodes tab.
    string? ContentPath,
    UserItemDataDto? UserData,
    // The source pinned to play by default (first in MediaSources); null when no preference is set.
    Guid? DefaultSourceId,
    IReadOnlyList<MediaSourceDto> MediaSources,
    IReadOnlyList<SeasonSummaryDto>? Seasons,
    // Distributor/network logos (Netflix, Apple TV+, …) for series; null for movies.
    IReadOnlyList<NetworkDto>? Networks,
    // Production status (Released, Ended, Returning Series, …) from TMDb.
    string? Status,
    // Number of community ratings backing CommunityRating (qualifies a high score from few votes).
    int? VoteCount,
    // Total seasons/episodes per TMDb (series only); distinct from the locally-held SeasonSummary counts.
    int? SeasonCount,
    int? EpisodeCount,
    // Franchise/collection this movie belongs to (e.g. "The Lord of the Rings Collection"); null otherwise.
    string? CollectionName,
    string? Homepage,
    // IMDb id (tt…) for cross-linking; from TMDb external_ids.
    string? ImdbId,
    // Best YouTube trailer URL (official trailer preferred), or null when none is available.
    string? TrailerUrl,
    // Top-billed cast, in TMDb order.
    IReadOnlyList<CastMemberDto> Cast,
    // Director(s) for movies (from crew).
    IReadOnlyList<string> Directors,
    // Creator(s) for series (from created_by).
    IReadOnlyList<string> Creators,
    // Production companies / studios with their (optional) logos.
    IReadOnlyList<StudioDto> Studios,
    // TMDb keyword tags.
    IReadOnlyList<string> Keywords);

/// <summary>A TV network/distributor with its (optional) logo, surfaced on series detail.</summary>
public sealed record NetworkDto(string Name, string? LogoUrl);

/// <summary>A production company/studio with its (optional) logo.</summary>
public sealed record StudioDto(string Name, string? LogoUrl);

/// <summary>
/// A cast member: the stable person identity (<see cref="Provider"/> + <see cref="ProviderId"/>) so the UI
/// can link to the person page, the actor name, the character they play (when known), and a profile photo url.
/// Cast is read from the Person join, so the identity is always present.
/// </summary>
public sealed record CastMemberDto(string Provider, string ProviderId, string Name, string? Character, string? ProfileUrl);

public sealed record MediaSourceDto(
    Guid Id,
    string? VersionName,
    // The on-disk file name (with extension). Read-only in the UI — shown so an operator can tell sources
    // apart; the editable label is VersionName.
    string FileName,
    string Container,
    long SizeBytes,
    int? Bitrate,
    long DurationTicks,
    IReadOnlyList<MediaStreamDto> Streams);

public sealed record MediaStreamDto(
    string Type,
    int Index,
    string? Codec,
    string? Language,
    string? DisplayTitle,
    string? Title,
    int? Width,
    int? Height,
    string? HdrFormat,
    int? Channels,
    // Secondary technical specs surfaced under each track: codec profile (e.g. "High"), video frame rate,
    // colour bit depth, audio sample rate (Hz). Whatever the probe captured; null when unknown.
    string? Profile,
    double? FrameRate,
    int? BitDepth,
    int? SampleRate,
    bool IsDefault,
    bool IsForced,
    bool IsExternal);

/// <summary>One season of a series, with its episode count and watched rollup.</summary>
public sealed record SeasonSummaryDto(
    Guid Id,
    string? PublicId,
    int? SeasonNumber,
    string Title,
    int EpisodeCount,
    UserItemDataDto? UserData);

public sealed record EpisodeDto(
    Guid Id,
    string? PublicId,
    // The owning series' TMDb id — an episode carries its series identity; used for the Infuse deep link.
    string? SeriesTmdbId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Title,
    string? Overview,
    long? RuntimeTicks,
    string? PosterUrl,
    UserItemDataDto? UserData);

/// <summary>
/// A leaf item (movie or episode) for the Home rails (Continue Watching / Next Up). Navigation resolves
/// to a detail page: movies link to themselves; episodes link to their series. For an episode, <see
/// cref="Title"/> is the series name and <see cref="Subtitle"/> is "S01E03 · Episode title".
/// </summary>
public sealed record LibraryRailItemDto(
    Guid Id,
    string Kind,
    Guid NavId,
    string NavKind,
    string Title,
    string? Subtitle,
    string? PosterUrl,
    UserItemDataDto? UserData);
