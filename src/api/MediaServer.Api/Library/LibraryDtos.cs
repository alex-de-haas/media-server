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
    Guid CatalogId,
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
    string? LibraryPath,
    UserItemDataDto? UserData,
    IReadOnlyList<MediaSourceDto> MediaSources,
    IReadOnlyList<SeasonSummaryDto>? Seasons);

public sealed record MediaSourceDto(
    Guid Id,
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
    int? Width,
    int? Height,
    string? HdrFormat,
    int? Channels,
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
