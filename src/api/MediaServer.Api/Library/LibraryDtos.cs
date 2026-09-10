using MediaServer.Api.Data;

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
    UserItemDataDto? UserData,
    // Enough to narrow a suggestion without fetching every title one by one — "an unwatched comedy
    // under two hours" is three fields, and without them a caller has to read the whole library to
    // answer it. Read from the metadata record the projection already loads, so they cost no query.
    IReadOnlyList<string>? Genres = null,
    long? RuntimeTicks = null,
    double? CommunityRating = null,
    IReadOnlyList<string>? VideoFormats = null);

/// <summary>Filters and window for a library search.</summary>
/// <remarks>
/// <see cref="Watched"/> is evaluated the way the library defines it rather than the way the column
/// reads: a movie carries its own played flag, but a series' state is a rollup over its episodes, so
/// filtering on a series row's flag would report a fully-watched series as unwatched.
/// </remarks>
public sealed record LibrarySearchQuery(
    Guid? CatalogId = null,
    MediaKind? Kind = null,
    string? Title = null,
    bool? Watched = null,
    /// <summary>What the title is about: matched against the synopsis and the provider's keywords.</summary>
    string? About = null,
    /// <summary>Genres the title must carry — all of them, not any. "an action comedy" is both.</summary>
    IReadOnlyList<string>? Genres = null,
    int? Limit = null,
    int? Offset = null);

/// <summary>One page of library items, with the window that produced it.</summary>
public sealed record LibrarySearchPage(
    IReadOnlyList<LibraryItemDto> Items,
    int Total,
    int Limit,
    int Offset);

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
    // Last episode covered when one file holds a consecutive range (S01E01-E02); null for a single episode.
    int? IndexNumberEnd,
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
    IReadOnlyList<string> Keywords,
    IReadOnlyList<CrewMemberDto>? Crew = null);

/// <summary>A TV network/distributor with its (optional) logo, surfaced on series detail.</summary>
public sealed record NetworkDto(string Name, string? LogoUrl);

/// <summary>A production company/studio with its (optional) logo.</summary>
public sealed record StudioDto(string Name, string? LogoUrl);

/// <summary>A stored crew credit with its person portrait and production role.</summary>
public sealed record CrewMemberDto(Guid Id, string Provider, string ProviderId, string Name,
    string? Job, string? Department, string? ProfileUrl);

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
    IReadOnlyList<MediaStreamDto> Streams,
    Remux.IndexingStatus? Indexing = null);

public sealed record MediaStreamDto(
    // A sidecar is a file of its own and can be merged in or removed on its own, so it has to be
    // addressable; an embedded track carries the same id harmlessly.
    Guid Id,
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
    // What this one track costs, in bits per second — the figure the convert dialog sizes a re-encode
    // against. Null when the file states none, and deliberately not filled in from the source's overall
    // rate: a caller has to be able to tell "not recorded" from "measured".
    int? Bitrate,
    bool IsDefault,
    bool IsForced,
    bool IsExternal,
    // A sidecar's own file name (with extension), null for an embedded track. Sidecars often carry neither
    // codec nor language, so the name is what actually tells two dubs apart on screen — and it is the file
    // an operator sees in the folder.
    string? FileName,
    // The Dolby Vision configuration record's fields, beside the flat HdrFormat: what tells a dual-layer
    // profile 7 (which Apple TV and Infuse play as HDR10) from a single-layer 8.1 (which they play as Dolby
    // Vision). Null for anything that is not Dolby Vision, and for a row probed before it was recorded.
    DolbyVisionDto? DolbyVision = null,
    // Server-defined nominal resolution; null for non-video streams or unknown dimensions.
    string? ResolutionLabel = null,
    Remux.IndexingStatus? Indexing = null);

/// <summary>A Dolby Vision configuration record as a client reads it: the profile (5, 7 or 8), its level, the
/// base-layer compatibility id (1 is HDR10, 2 SDR, 4 HLG, 6 the HDR10 a UHD Blu-ray carries under profile
/// 7) and whether an enhancement layer is present — the mark of profile 7's dual layer.</summary>
public sealed record DolbyVisionDto(int Profile, int Level, int BlCompatibilityId, bool EnhancementLayer);

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
    // The season row this episode belongs to. The Episodes tab groups by SeasonNumber, so this is what
    // gives a group an id to act on (deleting the whole season).
    Guid? SeasonId,
    int? SeasonNumber,
    int? EpisodeNumber,
    // Last episode covered by this file when it holds a consecutive range (a "double episode"): the row is
    // one item numbered 1 with EpisodeNumberEnd 2, and no separate row exists for episode 2. Null otherwise.
    // Title stays the first episode's — the provider has no combined title and none is invented.
    int? EpisodeNumberEnd,
    string Title,
    string? Overview,
    long? RuntimeTicks,
    string? PosterUrl,
    UserItemDataDto? UserData,
    // What is on disk for this episode — the Episodes tab's summary line. Null when it has no source.
    EpisodeMediaSummaryDto? Media,
    DateTimeOffset? AirDate = null,
    // The episode's still — the frame the provider files under the backdrop role — as a remote URL, ranked
    // by the backdrop rule (textless first). Null for an episode never enriched with one; the web row then
    // shows a placeholder rather than the show's art. The Apple client reads the same frame through the
    // authenticated native image route instead (NativeEpisodeDto.Still).
    string? StillUrl = null);

/// <summary>
/// An episode's media at a glance. The codec, height, dynamic range and size are the <b>default</b>
/// version's — the file a player starts on — while <see cref="VersionCount"/> counts every version
/// and <see cref="VideoFormats"/> lists dynamic-range formats across all versions. The
/// picture is chosen by the rule every other surface uses, so a cover a muxer wrote as a video track is
/// passed over. Everything a version card shows is a click away (<c>GET /api/library/{episodeId}</c>);
/// this is what a season reads at a glance.
/// </summary>
public sealed record EpisodeMediaSummaryDto(
    int VersionCount,
    string? VideoCodec,
    int? Height,
    string? HdrFormat,
    DolbyVisionDto? DolbyVision,
    long SizeBytes,
    IReadOnlyList<string>? VideoFormats = null);

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
    UserItemDataDto? UserData,
    // The selected poster can belong to the episode even when navigation targets its series.
    Guid? PosterItemId = null);
