using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.People;

/// <summary>
/// Persists the cast/crew of a single media item from a cached provider payload: upserts <see cref="Person"/>
/// rows by <c>(Provider, ProviderId)</c> so a person is shared across the library, then replaces the item's
/// <see cref="MediaItemPerson"/> credits. Idempotent — re-running with the same payload is a no-op, and a
/// re-fetch with changed credits converges to the new set.
/// </summary>
public sealed class PersonSyncService(MediaServerDbContext database)
{
    /// <summary>
    /// Syncs the people for <paramref name="mediaItemId"/> from <paramref name="raw"/> (a TMDb detail payload
    /// with an embedded <c>credits</c> object). Returns the number of credits written.
    /// </summary>
    public async Task<int> SyncAsync(Guid mediaItemId, string provider, string? raw, CancellationToken cancellationToken)
    {
        var credits = PersonCredits.Parse(raw);

        var existingJoins = await database.MediaItemPersons
            .Where(link => link.MediaItemId == mediaItemId)
            .ToListAsync(cancellationToken);

        if (credits.Count == 0)
        {
            // No credits in the payload: drop any stale rows so the item converges to "no people".
            if (existingJoins.Count > 0)
            {
                database.MediaItemPersons.RemoveRange(existingJoins);
                await database.SaveChangesAsync(cancellationToken);
            }

            return 0;
        }

        var providerIds = credits.Select(credit => credit.ProviderId).Distinct().ToList();
        var personByProviderId = await UpsertPersonsAsync(provider, providerIds, credits, cancellationToken);

        // Replace the item's credits wholesale; the Person rows above outlive this and stay shared.
        database.MediaItemPersons.RemoveRange(existingJoins);
        foreach (var credit in credits)
        {
            database.MediaItemPersons.Add(new MediaItemPerson
            {
                Id = Guid.NewGuid(),
                MediaItemId = mediaItemId,
                PersonId = personByProviderId[credit.ProviderId].Id,
                Role = credit.Role,
                Character = credit.Character,
                Job = credit.Job,
                Department = credit.Department,
                Order = credit.Order,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        return credits.Count;
    }

    /// <summary>
    /// Upserts the <see cref="Person"/> rows for <paramref name="providerIds"/> and returns them keyed by
    /// provider id. Only writes a row when it is new or an attribute actually changed, so a re-enrich does not
    /// churn <c>UpdatedAt</c> (and emit redundant UPDATEs) for unchanged people. Retries once if a concurrent
    /// sync inserts a shared person first and trips the <c>(Provider, ProviderId)</c> unique index.
    /// </summary>
    private async Task<Dictionary<string, Person>> UpsertPersonsAsync(
        string provider, IReadOnlyList<string> providerIds, IReadOnlyList<PersonCredit> credits, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await database.Persons
                .Where(person => person.Provider == provider && providerIds.Contains(person.ProviderId))
                .ToListAsync(cancellationToken);
            var byProviderId = existing.ToDictionary(person => person.ProviderId, StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;

            foreach (var providerId in providerIds)
            {
                // Cast/crew entries for one person carry identical person attributes, so the first wins.
                var source = credits.First(credit => credit.ProviderId == providerId);
                var isNew = false;
                if (!byProviderId.TryGetValue(providerId, out var person))
                {
                    person = new Person { Id = Guid.NewGuid(), Provider = provider, ProviderId = providerId, Name = source.Name };
                    database.Persons.Add(person);
                    byProviderId[providerId] = person;
                    isNew = true;
                }

                if (isNew
                    || person.Name != source.Name
                    || person.ProfilePath != source.ProfilePath
                    || person.KnownForDepartment != source.KnownForDepartment)
                {
                    person.Name = source.Name;
                    person.ProfilePath = source.ProfilePath;
                    person.ProfileUrl = PersonCredits.ProfileUrl(source.ProfilePath);
                    person.KnownForDepartment = source.KnownForDepartment;
                    person.UpdatedAt = now;
                }
            }

            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return byProviderId;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A concurrent sync inserted one of these (Provider, ProviderId) persons first and the unique
                // index rejected ours. Drop our losing inserts and retry once, reloading to adopt the winners.
                foreach (var entry in database.ChangeTracker.Entries<Person>().Where(entry => entry.State == EntityState.Added).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }
}
