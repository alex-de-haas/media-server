using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>One related-movie row, containing only published library movies.</summary>
public sealed record RelatedMoviesDto(string? CollectionName, IReadOnlyList<LibraryItemDto> Items);

/// <summary>Movie-specific relations, independent of the user's personalized recommendation feed.</summary>
public sealed class RelatedMoviesService(
    MediaServerDbContext database, LibraryReadService library, ITmdbRecommendationSource recommendations)
{
    /// <summary>Reads local franchise siblings, including a single sibling of a removed seed.</summary>
    public async Task<RelatedMoviesDto?> CollectionAsync(Guid id, int appUserId, CancellationToken cancellationToken)
    {
        var seed = await SeedAsync(id, appUserId, cancellationToken);
        if (seed is null) return null;
        if (seed.CollectionId is not { } collectionId) return new(null, []);
        var name = await database.MovieCollections.Where(collection => collection.Id == collectionId)
            .Select(collection => collection.Name).FirstOrDefaultAsync(cancellationToken);
        var items = await Available().Where(item => item.Id != id && item.CollectionId == collectionId)
            .ToListAsync(cancellationToken);
        var seedIdentity = TmdbId(seed.IdentityProvider, seed.IdentityProviderId, seed.Providers);
        var identityById = items.ToDictionary(item => item.Id,
            item => TmdbId(item.IdentityProvider, item.IdentityProviderId, item.Providers));
        var cards = await library.ProjectCardsAsync(items, appUserId, cancellationToken);
        return new(name, cards.Where(card => seedIdentity is null || identityById[card.Id] != seedIdentity)
            .OrderBy(card => card.Year ?? int.MaxValue)
            .ThenBy(card => card.Title, StringComparer.OrdinalIgnoreCase).ThenBy(card => card.Id)
            .DistinctBy(card => identityById[card.Id] ?? card.Id.ToString()).ToList());
    }

    /// <summary>Intersects cached TMDb lists with the available library, retaining provider order.</summary>
    public async Task<RelatedMoviesDto?> SimilarAsync(Guid id, int appUserId, CancellationToken cancellationToken)
    {
        var seed = await SeedAsync(id, appUserId, cancellationToken);
        if (seed is null) return null;
        var tmdbId = TmdbId(seed.IdentityProvider, seed.IdentityProviderId, seed.Providers);
        if (tmdbId is null) return new(null, []);
        var identity = new RecommendationIdentity(RecommendationKind.Movie, tmdbId);
        // Sequential: the shared cached source uses this request's scoped EF context.
        var recommended = await recommendations.ForSeedAsync(identity, TmdbRecommendationGenerator.Seeds, cancellationToken);
        var similar = await recommendations.ForSeedAsync(identity, TmdbRecommendationGenerator.Similar, cancellationToken);
        var ids = recommended.Concat(similar).Select(title => title.TmdbId)
            .Where(candidate => candidate != tmdbId).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return new(null, []);

        var candidates = await Available().Where(item => item.Id != id)
            .Select(item => new { item.Id, item.CollectionId, item.IdentityProvider, item.IdentityProviderId, item.Providers })
            .ToListAsync(cancellationToken);
        var identities = candidates.Select(item => new { item.Id, item.CollectionId,
            TmdbId = TmdbId(item.IdentityProvider, item.IdentityProviderId, item.Providers) }).ToList();
        // Legacy copies across catalogs must not repeat a collection title in the similar row.
        var collectionIdentities = identities.Where(item => seed.CollectionId != null && item.CollectionId == seed.CollectionId)
            .Select(item => item.TmdbId).ToHashSet(StringComparer.Ordinal);
        var byProvider = identities.Where(item => item.TmdbId != null && !collectionIdentities.Contains(item.TmdbId))
            .OrderBy(item => item.Id)
            .GroupBy(item => item.TmdbId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Id);
        var ordered = ids.Where(byProvider.ContainsKey).Select(key => byProvider[key]).ToList();
        var items = await Available().Where(item => ordered.Contains(item.Id)).ToListAsync(cancellationToken);
        var cards = (await library.ProjectCardsAsync(items, appUserId, cancellationToken)).ToDictionary(card => card.Id);
        return new(null, ordered.Where(cards.ContainsKey).Select(key => cards[key]).ToList());
    }

    private IQueryable<MediaItem> Available() => database.MediaItems.AsNoTracking()
        .Where(item => item.Kind == MediaKind.Movie && item.PublicId != null && item.RemovedAt == null && item.ParentId == null);

    private Task<MediaItem?> SeedAsync(Guid id, int userId, CancellationToken cancellationToken) =>
        MovieDetailAccess.Query(database, userId).FirstOrDefaultAsync(item => item.Id == id && item.Kind == MediaKind.Movie, cancellationToken);

    private static string? TmdbId(string? provider, string? id, Dictionary<string, string> providers) =>
        string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase) ? id : providers.GetValueOrDefault("tmdb");
}
