using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.People;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Fetches and caches provider metadata for every supported language and the item's images, keyed by
/// <c>provider + language</c>. Idempotent: re-enriching refreshes existing records in place. See
/// <c>docs/features/metadata/feature.md</c>.
/// </summary>
public sealed class EnrichService(
    MediaServerDbContext database,
    IMetadataProvider provider,
    MediaServerSettings settings,
    PersonSyncService personSync,
    CollectionSyncService collectionSync)
{
    public async Task EnrichAsync(Catalog catalog, MediaItem item, CancellationToken cancellationToken)
    {
        if (item.IdentityProvider is null || item.IdentityProviderId is null)
        {
            return;
        }

        var reference = new ProviderRef(item.IdentityProvider, item.IdentityProviderId);
        var languages = ResolveLanguages(catalog);

        var records = await provider.FetchAsync(reference, item.Kind, languages, cancellationToken);
        var existing = await database.MetadataRecords
            .Where(record => record.MediaItemId == item.Id && record.Provider == reference.Provider)
            .ToListAsync(cancellationToken);
        var byLanguage = existing.ToDictionary(record => record.Language, StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (!byLanguage.TryGetValue(record.Language, out var target))
            {
                target = new MetadataRecord
                {
                    Id = Guid.NewGuid(),
                    MediaItemId = item.Id,
                    Provider = record.Reference.Provider,
                    Language = record.Language,
                };
                database.MetadataRecords.Add(target);
            }

            target.Title = record.Title;
            target.Overview = record.Overview;
            target.Tagline = record.Tagline;
            target.Genres = record.Genres.ToList();
            target.OfficialRating = record.OfficialRating;
            target.CommunityRating = record.CommunityRating;
            target.ReleaseDate = record.ReleaseDate;
            target.RuntimeTicks = record.RuntimeTicks;
            target.Raw = record.Raw;
            target.FetchedAt = DateTimeOffset.UtcNow;
        }

        var primary = records.FirstOrDefault();
        if (primary is not null)
        {
            item.OriginalTitle ??= primary.OriginalTitle;
            item.OriginalLanguage ??= primary.OriginalLanguage;
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await UpsertImagesAsync(item, reference, languages, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        // Persist cast/crew from the freshly fetched credits (in the primary record's payload). Done after
        // the metadata save so a credits failure can't strand the rest of the enrich, and re-run on every
        // (re)fetch so the join converges to the latest credits.
        if (primary is not null)
        {
            await personSync.SyncAsync(item.Id, reference.Provider, primary.Raw, cancellationToken);
            // Link a movie to its franchise/collection from the same payload (no-op for non-movies). Kept
            // separate from the metadata save so a sync failure can't strand the rest of the enrich.
            await collectionSync.SyncAsync(item.Id, reference.Provider, primary.Raw, cancellationToken);
        }
    }

    private async Task UpsertImagesAsync(MediaItem item, ProviderRef reference, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var images = await provider.GetImagesAsync(reference, item.Kind, languages, cancellationToken);
        if (images.Count == 0)
        {
            return;
        }

        var existing = await database.ImageAssets.Where(image => image.MediaItemId == item.Id).ToListAsync(cancellationToken);
        // A set rather than a dictionary keyed by remote path: ToDictionary throws on a duplicate, and two
        // enriches of one item can race (a manual refresh alongside a catalog-wide one — the coordinator
        // serializes catalogs, not items), which would leave that item permanently un-enrichable.
        var byRemote = existing.Select(image => image.RemotePath).ToHashSet(StringComparer.Ordinal);

        var added = new List<ImageAsset>();
        foreach (var image in images)
        {
            if (!byRemote.Add(image.RemotePath))
            {
                continue;
            }

            var asset = new ImageAsset
            {
                Id = Guid.NewGuid(),
                MediaItemId = item.Id,
                ImageType = image.Type,
                Language = image.Language,
                Provider = reference.Provider,
                RemotePath = image.RemotePath,
                Tag = ImageTag(image.RemotePath),
                SortOrder = image.SortOrder,
            };
            added.Add(asset);
            database.ImageAssets.Add(asset);
        }

        if (added.Count == 0)
        {
            return;
        }

        // The check above only sees what this context read, so two enriches discovering the same new image
        // can both get past it and the second insert then violates the unique (MediaItemId, RemotePath)
        // index. That is a race the loser should shrug off rather than fail on: the rows are identical —
        // Tag is derived from RemotePath — so whoever wrote them first wrote the same thing. Saved here, on
        // its own, so a conflict costs the artwork nothing and leaves the metadata in this unit of work for
        // the caller's save.
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            foreach (var asset in added)
            {
                database.Entry(asset).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Whether a failed save was a unique-index collision rather than a real fault. SQLite reports both a
    /// constraint failure and a unique failure as extended result codes over <c>SQLITE_CONSTRAINT</c> (19),
    /// so the primary code is what identifies the class.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    private IReadOnlyList<string> ResolveLanguages(Catalog catalog)
    {
        var languages = new List<string>();
        if (!string.IsNullOrWhiteSpace(catalog.MetadataLanguage))
        {
            languages.Add(catalog.MetadataLanguage);
        }

        languages.AddRange(settings.SupportedLanguages);
        return languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ImageTag(string remotePath)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(remotePath));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
