namespace MediaServer.Api.Data;

/// <summary>
/// One title's cached TMDb detail payload, per language.
/// </summary>
/// <remarks>
/// The preview surface asks about titles the instance does not hold, so there is no
/// <see cref="MediaItem"/> to hang a <see cref="MetadataRecord"/> on — hence a cache keyed by provider
/// identity instead. Shared across users on the same grounds as the recommendation caches: the row says
/// what TMDb says about a public title and records nobody's interest in it.
///
/// The raw payload is stored rather than a projection, so changing what the readers derive from it costs
/// no refetch. The TTL is enforced on read, and a stale row still answers when TMDb is unreachable.
/// </remarks>
public sealed class TmdbTitleDetailCacheEntry
{
    public Guid Id { get; set; }

    /// <summary>Movie or Series — TMDb's movie and tv id spaces overlap, so the kind is part of the key.</summary>
    public MediaKind Kind { get; set; }

    public required string TmdbId { get; set; }

    /// <summary>The metadata language the payload was fetched in (e.g. <c>en-US</c>).</summary>
    public required string Language { get; set; }

    /// <summary>The full TMDb detail document, as fetched.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}
