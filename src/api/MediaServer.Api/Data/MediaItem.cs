namespace MediaServer.Api.Data;

/// <summary>
/// Unified, hierarchical media item (matches Jellyfin <c>BaseItem</c>). The internal <see cref="Id"/>
/// is a <see cref="Guid"/>; the Jellyfin-facing <see cref="PublicId"/> is a separate stable string
/// derived from the canonical provider identity so client ids survive rescans.
/// </summary>
public sealed class MediaItem
{
    public Guid Id { get; set; }

    /// <summary>Unique, stable across rescans, exposed to Jellyfin. Null until published.</summary>
    public string? PublicId { get; set; }

    public Guid CatalogId { get; set; }

    public MediaKind Kind { get; set; }

    /// <summary>Self-FK: Season → Series, Episode → Season.</summary>
    public Guid? ParentId { get; set; }

    public Guid? SeriesId { get; set; }

    public Guid? SeasonId { get; set; }

    public required string Title { get; set; }

    public string? OriginalTitle { get; set; }

    /// <summary>Always stored, language-independent.</summary>
    public string? OriginalLanguage { get; set; }

    public int? Year { get; set; }

    public int? IndexNumber { get; set; }

    /// <summary>Set when one file holds two consecutive episodes.</summary>
    public int? IndexNumberEnd { get; set; }

    public int? ParentIndexNumber { get; set; }

    /// <summary>The franchise/collection this movie belongs to (TMDb <c>belongs_to_collection</c>); null for
    /// non-movies and movies outside any collection. One-to-many: a movie has at most one collection.</summary>
    public Guid? CollectionId { get; set; }

    /// <summary>Relative to catalog root; null for Series/Season containers.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>The <see cref="MediaSource"/> a player should default to when a multi-version title is played
    /// without an explicit source pick. Clients (Infuse) treat <c>MediaSources[0]</c> as the default, so this
    /// only controls ordering. Null = no preference (fall back to natural order). Not a hard FK: a deleted
    /// source just makes this stale and ordering falls back.</summary>
    public Guid? DefaultSourceId { get; set; }

    /// <summary>Canonical provider, e.g. <c>tmdb</c>.</summary>
    public string? IdentityProvider { get; set; }

    /// <summary>Movie id or series/show id from the canonical provider.</summary>
    public string? IdentityProviderId { get; set; }

    public int? IdentitySeasonNumber { get; set; }

    public int? IdentityEpisodeNumber { get; set; }

    /// <summary>Provider dictionary, e.g. <c>{ "tmdb": "27205" }</c>. Stored as a JSON column.</summary>
    public Dictionary<string, string> Providers { get; set; } = new();

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Catalog? Catalog { get; set; }

    public MovieCollection? Collection { get; set; }

    public ICollection<MediaSource> Sources { get; set; } = new List<MediaSource>();
}
