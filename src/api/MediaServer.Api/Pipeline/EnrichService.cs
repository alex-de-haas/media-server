using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.People;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Fetches and caches provider metadata for every supported language and the item's images, keyed by
/// <c>provider + language</c>. Idempotent: re-enriching refreshes existing records in place. See
/// <c>docs/features/metadata.md</c>.
/// </summary>
public sealed class EnrichService(MediaServerDbContext database, IMetadataProvider provider, MediaServerSettings settings, PersonSyncService personSync)
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
        var byRemote = existing.ToDictionary(image => image.RemotePath, StringComparer.Ordinal);

        foreach (var image in images)
        {
            if (byRemote.ContainsKey(image.RemotePath))
            {
                continue;
            }

            database.ImageAssets.Add(new ImageAsset
            {
                Id = Guid.NewGuid(),
                MediaItemId = item.Id,
                ImageType = image.Type,
                Language = image.Language,
                Provider = reference.Provider,
                RemotePath = image.RemotePath,
                Tag = ImageTag(image.RemotePath),
                SortOrder = image.SortOrder,
            });
        }
    }

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
