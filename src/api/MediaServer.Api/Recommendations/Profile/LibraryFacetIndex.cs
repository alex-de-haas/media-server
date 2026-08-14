using System.Collections.Concurrent;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Profile;

/// <summary>
/// How common each facet is across the whole library, and the damping that follows from it.
/// </summary>
/// <remarks>
/// Without this every profile looks alike. "Drama" appears on a third of any library, so an
/// undamped profile would report that every user loves drama — true, useless, and identical for
/// everyone. The point of a profile is what distinguishes <em>this</em> viewer, which is what
/// inverse document frequency measures: a facet is interesting in proportion to how rarely the
/// library holds it.
/// </remarks>
public sealed class LibraryFacetIndex(
    int documentCount,
    IReadOnlyDictionary<(FacetFamily, string), int> frequencies,
    IReadOnlyDictionary<Guid, TitleFacets> byItem)
{
    public int DocumentCount { get; } = documentCount;

    /// <summary>
    /// The facets themselves, kept rather than discarded.
    /// </summary>
    /// <remarks>
    /// Building the index already parses every title's raw metadata for its keywords — the expensive
    /// part — so throwing the result away meant the profile, the `held` generator and the engine's
    /// facet attachment each parsed the same library again on every request. Keeping it turns three
    /// full passes per request into none.
    /// </remarks>
    public IReadOnlyDictionary<Guid, TitleFacets> ByItem { get; } = byItem;

    /// <summary>
    /// The damping factor for one facet: <c>ln(1 + N/df)</c>.
    /// </summary>
    /// <remarks>
    /// Smoothed rather than raw <c>ln(N/df)</c>, which would zero out a facet every title carries and
    /// silently delete a whole family from a small library.
    /// </remarks>
    public double Damping(FacetFamily family, string value)
    {
        if (DocumentCount == 0)
        {
            return 1;
        }

        var frequency = frequencies.GetValueOrDefault((family, value), 0);
        return Math.Log(1 + ((double)DocumentCount / Math.Max(frequency, 1)));
    }
}

/// <summary>
/// Builds and caches the library facet index.
/// </summary>
/// <remarks>
/// Cached against a <em>generation</em> rather than invalidated by events. The index depends on every
/// title in the library and on every metadata refresh, so an event-based scheme would need a hook on
/// each of those write paths, and the failure mode of a forgotten hook is invisible: the feed keeps
/// working while ranking against a library that no longer exists. A generation is three aggregates
/// over columns the writers already maintain, so nothing can be forgotten — if the library moved, the
/// stamp moved.
/// <para>
/// Rebuilding parses every title's raw metadata for its keywords, which is the reason this is cached
/// at all. It is a singleton so the cost is paid once per generation for the whole instance rather
/// than once per user or per request.
/// </para>
/// </remarks>
public sealed class LibraryFacetIndexCache
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private LibraryGeneration _generation;
    private LibraryFacetIndex? _index;

    /// <summary>
    /// Facets for the given items, served from the cached index and read only for what it lacks.
    /// </summary>
    /// <remarks>
    /// The index covers every held work, which is nearly always the whole question. A tombstoned title
    /// the viewer rated, or an item added since the stamp was taken, falls through to a direct read —
    /// correctness first, and it is a handful of rows rather than the library.
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, TitleFacets>> FacetsForAsync(
        IReadOnlyCollection<Guid> itemIds,
        MediaServerDbContext database,
        TitleFacetReader reader,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<Guid, TitleFacets>();
        }

        var index = await GetAsync(database, reader, cancellationToken);
        var result = new Dictionary<Guid, TitleFacets>(itemIds.Count);
        var missing = new List<Guid>();

        foreach (var id in itemIds)
        {
            if (index.ByItem.TryGetValue(id, out var facets))
            {
                result[id] = facets;
            }
            else
            {
                missing.Add(id);
            }
        }

        if (missing.Count > 0)
        {
            foreach (var (id, facets) in await reader.ReadAsync(missing, cancellationToken))
            {
                result[id] = facets;
            }
        }

        return result;
    }

    /// <summary>The index for the library as it stands, rebuilt only when the library has moved.</summary>
    public async Task<LibraryFacetIndex> GetAsync(
        MediaServerDbContext database, TitleFacetReader facets, CancellationToken cancellationToken)
    {
        var generation = await GenerationOfAsync(database, cancellationToken);
        if (_index is { } current && _generation == generation)
        {
            return current;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the lock: a fan-out of feed requests would otherwise each rebuild.
            if (_index is { } existing && _generation == generation)
            {
                return existing;
            }

            var built = await BuildAsync(database, facets, cancellationToken);
            _index = built;
            _generation = generation;
            return built;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<LibraryFacetIndex> BuildAsync(
        MediaServerDbContext database, TitleFacetReader facets, CancellationToken cancellationToken)
    {
        var itemIds = await WorkIdsAsync(database, cancellationToken);
        var byItem = await facets.ReadAsync(itemIds, cancellationToken);

        var frequencies = new Dictionary<(FacetFamily, string), int>();
        foreach (var title in byItem.Values)
        {
            // Document frequency, not term frequency: a title that lists a keyword twice is still one
            // document, and counting it twice would make that facet look rarer than it is.
            foreach (var facet in title.Facets.Select(facet => (facet.Family, facet.Value)).Distinct())
            {
                frequencies[facet] = frequencies.GetValueOrDefault(facet, 0) + 1;
            }
        }

        return new LibraryFacetIndex(byItem.Count, frequencies, byItem);
    }

    /// <summary>Movies and series the instance still holds — the documents the frequencies are over.</summary>
    private static async Task<List<Guid>> WorkIdsAsync(MediaServerDbContext database, CancellationToken cancellationToken) =>
        await database.MediaItems.AsNoTracking()
            .Where(item => item.RemovedAt == null && (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// A cheap stamp of the library's shape: how many works there are, when one last changed, and when
    /// metadata was last refreshed. Any add, removal or re-enrich moves at least one of the three.
    /// </summary>
    internal static async Task<LibraryGeneration> GenerationOfAsync(
        MediaServerDbContext database, CancellationToken cancellationToken)
    {
        var works = database.MediaItems.AsNoTracking()
            .Where(item => item.RemovedAt == null && (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series));

        var count = await works.CountAsync(cancellationToken);
        var updated = await works.MaxAsync(item => (DateTimeOffset?)item.UpdatedAt, cancellationToken);
        var enriched = await database.MetadataRecords.AsNoTracking()
            .MaxAsync(record => (DateTimeOffset?)record.FetchedAt, cancellationToken);

        return new LibraryGeneration(count, updated?.UtcTicks ?? 0, enriched?.UtcTicks ?? 0);
    }
}

/// <summary>What the library looked like when an index was built.</summary>
public readonly record struct LibraryGeneration(int Works, long LastUpdatedTicks, long LastEnrichedTicks);
