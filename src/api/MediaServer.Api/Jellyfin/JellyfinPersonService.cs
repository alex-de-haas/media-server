using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Backing data for the Jellyfin people surface: an item's credits, the people credited somewhere in the
/// library, and public-id resolution + profile-image tags shared by <see cref="JellyfinLibraryService"/>
/// (browsing), <see cref="JellyfinItemMapper"/> (projection) and <see cref="JellyfinImageService"/>
/// (artwork). Pure read model over the EF domain, mirroring <see cref="JellyfinCollectionService"/>.
/// </summary>
public sealed class JellyfinPersonService(MediaServerDbContext database)
{
    /// <summary>The credits of the given items, person included, ordered per item by billing order.</summary>
    public async Task<Dictionary<Guid, IReadOnlyList<ItemCredit>>> LoadAsync(
        IReadOnlyList<Guid> mediaItemIds, CancellationToken cancellationToken)
    {
        if (mediaItemIds.Count == 0)
        {
            return [];
        }

        var credits = await database.MediaItemPersons.AsNoTracking()
            .Where(credit => mediaItemIds.Contains(credit.MediaItemId))
            .Join(
                database.Persons.AsNoTracking(),
                credit => credit.PersonId,
                person => person.Id,
                (credit, person) => new ItemCredit(person, credit))
            .ToListAsync(cancellationToken);

        return credits
            .GroupBy(entry => entry.Credit.MediaItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ItemCredit>)group.OrderBy(entry => entry.Credit.Order).ToList());
    }

    /// <summary>
    /// The people holding at least one credit on a published item, by name. Optionally narrowed by a search
    /// term; <paramref name="limit"/> caps the page taken from <paramref name="startIndex"/>.
    /// </summary>
    public async Task<(IReadOnlyList<Person> People, int Total)> SearchAsync(
        string? searchTerm, int startIndex, int? limit, CancellationToken cancellationToken)
    {
        var query = CreditedPeople();
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(person => EF.Functions.Like(person.Name, term));
        }

        var total = await query.CountAsync(cancellationToken);
        var page = query.OrderBy(person => person.Name).Skip(startIndex);
        if (limit is { } take)
        {
            page = page.Take(take);
        }

        return (await page.ToListAsync(cancellationToken), total);
    }

    /// <summary>Resolves a person public id back to its person, or null when the id is not one.</summary>
    public async Task<Person?> ResolveAsync(string publicId, CancellationToken cancellationToken)
    {
        var matched = await ResolveManyAsync([publicId], cancellationToken);
        return matched.Count > 0 ? matched[0] : null;
    }

    /// <summary>
    /// Resolves person public ids back to their people. The id is a one-way hash of the provider identity,
    /// so this projects the identity columns and re-derives the hash rather than querying by id. That is a
    /// scan, but of three narrow columns over a table that holds one row per credited person — the same
    /// trade <see cref="JellyfinCollectionService.ResolveAsync"/> makes, and it keeps the id derivable from
    /// the provider identity alone instead of persisting a second key.
    /// </summary>
    public async Task<IReadOnlyList<Person>> ResolveManyAsync(
        IReadOnlyList<string> publicIds, CancellationToken cancellationToken)
    {
        if (publicIds.Count == 0)
        {
            return [];
        }

        var wanted = publicIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identities = await database.Persons.AsNoTracking()
            .Select(person => new { person.Id, person.Provider, person.ProviderId })
            .ToListAsync(cancellationToken);

        var matchedIds = identities
            .Where(identity => wanted.Contains(JellyfinIds.Person(identity.Provider, identity.ProviderId)))
            .Select(identity => identity.Id)
            .ToList();
        if (matchedIds.Count == 0)
        {
            return [];
        }

        return await database.Persons.AsNoTracking()
            .Where(person => matchedIds.Contains(person.Id))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The published items the given people are credited on.</summary>
    public async Task<IReadOnlyList<Guid>> CreditedItemIdsAsync(
        IReadOnlyList<Guid> personIds, CancellationToken cancellationToken) =>
        personIds.Count == 0
            ? []
            : await database.MediaItemPersons.AsNoTracking()
                .Where(credit => personIds.Contains(credit.PersonId))
                .Select(credit => credit.MediaItemId)
                .Distinct()
                .ToListAsync(cancellationToken);

    /// <summary>The client-facing id of a person.</summary>
    public static string PublicId(Person person) => JellyfinIds.Person(person.Provider, person.ProviderId);

    /// <summary>
    /// The Primary-image tag advertised for a person; null when the provider has no photo. Like the
    /// collection tags it changes with the underlying image, so a replaced photo busts client caches.
    /// </summary>
    public static string? PrimaryTag(Person person) => PrimaryTag(person.Id, person.ProfileUrl);

    /// <inheritdoc cref="PrimaryTag(Person)"/>
    /// <remarks>The column form, for callers that project the two fields instead of loading the entity.</remarks>
    public static string? PrimaryTag(Guid personId, string? profileUrl) =>
        string.IsNullOrEmpty(profileUrl)
            ? null
            : Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"{personId:N}|profile|{profileUrl}")))[..16];

    private IQueryable<Person> CreditedPeople() =>
        database.Persons.AsNoTracking().Where(person =>
            database.MediaItemPersons.Any(credit =>
                credit.PersonId == person.Id &&
                database.MediaItems.Any(item => item.Id == credit.MediaItemId && item.PublicId != null)));
}
