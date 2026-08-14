using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Profile;

/// <summary>
/// Reads the facets of library titles in bulk.
/// </summary>
/// <remarks>
/// Bulk by design: both callers — one user's profile and the library-wide frequency index — want
/// hundreds of titles at once, and a per-title read would turn either into a query storm.
/// <para>
/// Genres, decade, language and people come from columns and a join. <b>Keywords do not:</b> nothing
/// persists them, so they are parsed out of <see cref="MetadataRecord.Raw"/>, and that parse is the
/// expensive part of building the index. It is affordable only because the index is cached against a
/// library generation rather than rebuilt per request — see <see cref="LibraryFacetIndex"/>. If a
/// large library ever makes it hurt, the fix is a persisted keywords column, not a smaller sample.
/// </para>
/// </remarks>
public sealed class TitleFacetReader(MediaServerDbContext database)
{
    /// <summary>Facets for the given library items, keyed by item id. Items with nothing to say are omitted.</summary>
    public async Task<IReadOnlyDictionary<Guid, TitleFacets>> ReadAsync(
        IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<Guid, TitleFacets>();
        }

        var items = await database.MediaItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Kind, item.Year, item.OriginalLanguage })
            .ToListAsync(cancellationToken);

        // One metadata row per (item, provider, language); the first is enough — genres and keywords
        // are the same work in any language, and a profile is not a display surface.
        var metadata = await database.MetadataRecords.AsNoTracking()
            .Where(record => itemIds.Contains(record.MediaItemId))
            .Select(record => new { record.MediaItemId, record.Genres, record.Raw })
            .ToListAsync(cancellationToken);
        var metadataByItem = metadata
            .GroupBy(record => record.MediaItemId)
            .ToDictionary(group => group.Key, group => group.First());

        var credits = await database.MediaItemPersons.AsNoTracking()
            .Where(person => itemIds.Contains(person.MediaItemId))
            .Select(person => new
            {
                person.MediaItemId, person.PersonId, person.Role, person.Job, person.Department, person.Order,
            })
            .ToListAsync(cancellationToken);
        var creditsByItem = credits.GroupBy(credit => credit.MediaItemId).ToDictionary(group => group.Key, group => group.ToList());

        var result = new Dictionary<Guid, TitleFacets>(items.Count);
        foreach (var item in items)
        {
            var facets = new List<WeightedFacet>();

            if (Decade(item.Year) is { } decade)
            {
                facets.Add(new WeightedFacet(FacetFamily.Decade, decade, 1));
            }

            if (!string.IsNullOrWhiteSpace(item.OriginalLanguage))
            {
                facets.Add(new WeightedFacet(FacetFamily.Language, item.OriginalLanguage.ToLowerInvariant(), 1));
            }

            if (metadataByItem.GetValueOrDefault(item.Id) is { } record)
            {
                foreach (var genre in record.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)))
                {
                    facets.Add(new WeightedFacet(FacetFamily.Genre, genre.Trim().ToLowerInvariant(), 1));
                }

                foreach (var keyword in Keywords(record.Raw, item.Kind))
                {
                    facets.Add(new WeightedFacet(FacetFamily.Keyword, keyword, 1));
                }
            }

            foreach (var credit in creditsByItem.GetValueOrDefault(item.Id) ?? [])
            {
                if (PersonFacetWeight.Of(credit.Role, credit.Job, credit.Department, credit.Order) is { } weight)
                {
                    facets.Add(new WeightedFacet(FacetFamily.Person, credit.PersonId.ToString("N"), weight));
                }
            }

            if (facets.Count > 0)
            {
                result[item.Id] = new TitleFacets(facets);
            }
        }

        return result;
    }

    /// <summary>The decade a title belongs to, as a string, or null when the year is unknown.</summary>
    /// <remarks>Decade rather than year: a viewer has a taste for eighties films, not for 1987 ones.</remarks>
    internal static string? Decade(int? year) =>
        year is { } value && value > 1800 ? (value / 10 * 10).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    private static IReadOnlyList<string> Keywords(string? raw, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return [.. TmdbPayload.Parse(raw, kind).Keywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(keyword => keyword.Trim().ToLowerInvariant())];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A payload this cannot read costs one title's keywords, never the whole profile.
            return [];
        }
    }
}
