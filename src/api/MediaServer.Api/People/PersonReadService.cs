using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.People;

/// <summary>
/// Read model for the internal <c>/api/persons</c> surface: resolves a person by its provider identity,
/// lazily refreshes its long-form details (biography, birth/death) from the provider on a staleness window,
/// and assembles the filmography from the <see cref="MediaItemPerson"/> join — limited to titles published in
/// the library and split into cast and crew. Poster/title resolution is reused from <see cref="LibraryReadService"/>.
/// </summary>
public sealed class PersonReadService(
    MediaServerDbContext database,
    LibraryReadService library,
    IMetadataProvider metadata,
    MediaServerSettings settings,
    TimeProvider clock,
    ILogger<PersonReadService> logger)
{
    // How long a person-detail fetch stays fresh before the next request refreshes it. Bios and birth fields
    // change rarely, so a long window keeps the lazy fetch from hitting the provider on every page view.
    private static readonly TimeSpan DetailsTtl = TimeSpan.FromDays(30);

    public async Task<PersonDetailDto?> GetAsync(string provider, string providerId, CancellationToken cancellationToken)
    {
        // Tracked (no AsNoTracking) so the lazy detail refresh below persists on SaveChanges.
        var person = await database.Persons
            .FirstOrDefaultAsync(entity => entity.Provider == provider && entity.ProviderId == providerId, cancellationToken);
        if (person is null)
        {
            return null;
        }

        await EnsureDetailsAsync(person, cancellationToken);

        var (cast, crew) = await LoadFilmographyAsync(person.Id, cancellationToken);

        return new PersonDetailDto(
            person.Provider,
            person.ProviderId,
            person.Name,
            person.ProfileUrl,
            person.Biography,
            person.KnownForDepartment,
            person.Birthday,
            person.Deathday,
            person.PlaceOfBirth,
            cast,
            crew);
    }

    /// <summary>
    /// Fetches and caches the person's long-form details on first view and after the staleness window. A
    /// provider miss or error leaves the row untouched (and any cached fields intact) so the page still renders.
    /// </summary>
    private async Task EnsureDetailsAsync(Person person, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (person.DetailsFetchedAt is { } fetchedAt && now - fetchedAt < DetailsTtl)
        {
            return;
        }

        PersonDetails? details;
        try
        {
            var language = settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";
            details = await metadata.FetchPersonAsync(new ProviderRef(person.Provider, person.ProviderId), language, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The person page is still useful from the credits-derived fields; don't fail it on a fetch error.
            // An HttpClient timeout surfaces as a TaskCanceledException (an OperationCanceledException) with the
            // caller's token *not* cancelled, so the IsCancellationRequested guard keeps that transient case here
            // while still letting a genuine caller-requested cancellation propagate.
            logger.LogDebug(exception, "Person-detail fetch failed for {Provider}:{ProviderId}.", person.Provider, person.ProviderId);
            return;
        }

        if (details is null)
        {
            // Provider returned nothing (miss or transport failure); retry on the next request rather than
            // marking the row fetched, so a transient failure doesn't suppress the bio for the whole TTL.
            return;
        }

        if (!string.IsNullOrWhiteSpace(details.Name))
        {
            person.Name = details.Name;
        }

        person.Biography = details.Biography;
        person.KnownForDepartment = details.KnownForDepartment ?? person.KnownForDepartment;
        if (details.ProfilePath is not null)
        {
            person.ProfilePath = details.ProfilePath;
            person.ProfileUrl = PersonCredits.ProfileUrl(details.ProfilePath);
        }

        person.Birthday = details.Birthday;
        person.Deathday = details.Deathday;
        person.PlaceOfBirth = details.PlaceOfBirth;
        person.DetailsFetchedAt = now;
        person.UpdatedAt = now;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<(IReadOnlyList<PersonFilmographyEntryDto> Cast, IReadOnlyList<PersonCrewGroupDto> Crew)> LoadFilmographyAsync(
        Guid personId, CancellationToken cancellationToken)
    {
        // Join the person's credits to their items, limited to published top-level movies/series — the set the
        // library browses ("media in the user's catalogs"). Seasons/episodes never carry person credits.
        var credits = await database.MediaItemPersons.AsNoTracking()
            .Where(credit => credit.PersonId == personId)
            .Join(
                database.MediaItems.AsNoTracking().Where(item =>
                    item.PublicId != null && (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series)),
                credit => credit.MediaItemId,
                item => item.Id,
                (credit, item) => new { credit.Role, credit.Character, credit.Job, credit.Department, credit.Order, Item = item })
            .ToListAsync(cancellationToken);

        if (credits.Count == 0)
        {
            return ([], []);
        }

        var items = credits.Select(row => row.Item).DistinctBy(item => item.Id).ToList();
        var cards = (await library.ProjectCardsAsync(items, appUserId: null, cancellationToken))
            .ToDictionary(card => card.Id);

        PersonFilmographyEntryDto Entry(string? character, string? job, MediaItem item)
        {
            var card = cards[item.Id];
            return new PersonFilmographyEntryDto(card.Id, card.Kind, card.Title, card.Year, card.PosterUrl, character, job);
        }

        var cast = credits
            .Where(row => row.Role == PersonRole.Cast)
            .Select(row => Entry(row.Character, null, row.Item))
            .OrderByDescending(entry => entry.Year ?? int.MinValue)
            .ThenBy(entry => entry.Title)
            .ToList();

        var crew = credits
            .Where(row => row.Role == PersonRole.Crew)
            .GroupBy(row => string.IsNullOrWhiteSpace(row.Department) ? "Other" : row.Department!)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PersonCrewGroupDto(
                group.Key,
                group
                    .Select(row => Entry(null, row.Job, row.Item))
                    .OrderByDescending(entry => entry.Year ?? int.MinValue)
                    .ThenBy(entry => entry.Title)
                    .ToList()))
            .ToList();

        return (cast, crew);
    }
}
