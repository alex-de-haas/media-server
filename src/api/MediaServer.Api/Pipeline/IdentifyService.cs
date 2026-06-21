using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

public sealed record IdentifyOutcome(bool AllResolved, string? ReviewReason, IReadOnlyList<MetadataCandidate> Candidates);

/// <summary>
/// Maps playable source files to movies or episodes: parses the name, searches the provider, scores
/// candidates, and on a high-confidence hit creates/reuses the canonical <see cref="MediaItem"/>
/// hierarchy and assigns the file. Idempotent — re-identifying reuses existing items by identity.
/// </summary>
public sealed class IdentifyService(
    MediaServerDbContext database, INameParser parser, IMetadataProvider provider, AppSettingsService appSettings, ILogger<IdentifyService> logger)
{
    public async Task<IdentifyOutcome> IdentifyAsync(
        Catalog catalog, IReadOnlyList<SourceFile> sourceFiles, string? fallbackName, CancellationToken cancellationToken)
    {
        var unresolved = new List<MetadataCandidate>();
        var reviewReasons = new List<string>();
        var releaseGroups = await appSettings.GetCustomReleaseGroupsAsync(cancellationToken);

        foreach (var sourceFile in sourceFiles)
        {
            if (sourceFile.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && sourceFile.MediaItemId is not null)
            {
                continue; // Already mapped (operator confirm or a prior run).
            }

            var name = DeriveName(sourceFile.RelativePath, fallbackName);
            var parsed = parser.Parse(name, catalog.Type, releaseGroups);
            var query = new MediaQuery(parsed.Kind, parsed.Title, parsed.Year, parsed.Season, parsed.Episode);

            var candidates = await provider.SearchAsync(query, cancellationToken);
            var best = candidates.FirstOrDefault();

            if (best is null || best.Score < TitleScoring.AutoMatchThreshold)
            {
                sourceFile.AssignmentStatus = SourceFileAssignmentStatus.NeedsReview;
                sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
                unresolved.AddRange(candidates.Take(5));
                reviewReasons.Add($"Low-confidence match for '{parsed.Title}'.");
                continue;
            }

            var mediaItem = parsed.Kind == MediaKind.Episode
                ? await ResolveEpisodeAsync(catalog, best, parsed, cancellationToken)
                : await ResolveMovieAsync(catalog, best, cancellationToken);

            sourceFile.MediaItemId = mediaItem.Id;
            sourceFile.AssignmentStatus = SourceFileAssignmentStatus.Confirmed;
            sourceFile.UpdatedAt = DateTimeOffset.UtcNow;

            logger.LogInformation("Matched {File} → {Kind} '{Title}' ({Provider}:{Id}).",
                sourceFile.RelativePath, mediaItem.Kind, mediaItem.Title, mediaItem.IdentityProvider, mediaItem.IdentityProviderId);
        }

        await database.SaveChangesAsync(cancellationToken);

        var allResolved = sourceFiles.All(file => file.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && file.MediaItemId is not null);
        return new IdentifyOutcome(allResolved, allResolved ? null : string.Join(" ", reviewReasons.Distinct()), unresolved);
    }

    public async Task<MediaItem> ResolveMovieAsync(Catalog catalog, MetadataCandidate candidate, CancellationToken cancellationToken)
    {
        var existing = await database.MediaItems.FirstOrDefaultAsync(item =>
            item.CatalogId == catalog.Id &&
            item.Kind == MediaKind.Movie &&
            item.IdentityProvider == candidate.Reference.Provider &&
            item.IdentityProviderId == candidate.Reference.Id, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = candidate.Title,
            Year = candidate.Year,
            IdentityProvider = candidate.Reference.Provider,
            IdentityProviderId = candidate.Reference.Id,
            Providers = new Dictionary<string, string> { [candidate.Reference.Provider] = candidate.Reference.Id },
            AddedAt = now,
            UpdatedAt = now,
        };
        database.MediaItems.Add(movie);
        return movie;
    }

    public async Task<MediaItem> ResolveEpisodeAsync(
        Catalog catalog, MetadataCandidate seriesCandidate, ParsedName parsed, CancellationToken cancellationToken)
    {
        var season = parsed.Season ?? 1;
        var episode = parsed.Episode ?? 0;
        var provider = seriesCandidate.Reference.Provider;
        var seriesId = seriesCandidate.Reference.Id;

        var series = await GetOrCreateContainerAsync(catalog, MediaKind.Series, provider, seriesId,
            () => new MediaItem
            {
                Id = Guid.NewGuid(),
                CatalogId = catalog.Id,
                Kind = MediaKind.Series,
                Title = seriesCandidate.Title,
                Year = seriesCandidate.Year,
                IdentityProvider = provider,
                IdentityProviderId = seriesId,
                Providers = new Dictionary<string, string> { [provider] = seriesId },
            }, seasonNumber: null, episodeNumber: null, cancellationToken);

        var seasonItem = await GetOrCreateContainerAsync(catalog, MediaKind.Season, provider, seriesId,
            () => new MediaItem
            {
                Id = Guid.NewGuid(),
                CatalogId = catalog.Id,
                Kind = MediaKind.Season,
                Title = $"Season {season}",
                ParentId = series.Id,
                SeriesId = series.Id,
                IdentityProvider = provider,
                IdentityProviderId = seriesId,
                IdentitySeasonNumber = season,
                ParentIndexNumber = season,
                IndexNumber = season,
                Providers = new Dictionary<string, string> { [provider] = seriesId },
            }, seasonNumber: season, episodeNumber: null, cancellationToken);

        seasonItem.ParentId ??= series.Id;
        seasonItem.SeriesId ??= series.Id;

        var episodeItem = await GetOrCreateContainerAsync(catalog, MediaKind.Episode, provider, seriesId,
            () => new MediaItem
            {
                Id = Guid.NewGuid(),
                CatalogId = catalog.Id,
                Kind = MediaKind.Episode,
                Title = $"Episode {episode}",
                ParentId = seasonItem.Id,
                SeriesId = series.Id,
                SeasonId = seasonItem.Id,
                IndexNumber = episode,
                IndexNumberEnd = parsed.EpisodeEnd,
                ParentIndexNumber = season,
                IdentityProvider = provider,
                IdentityProviderId = seriesId,
                IdentitySeasonNumber = season,
                IdentityEpisodeNumber = episode,
                Providers = new Dictionary<string, string> { [provider] = seriesId },
            }, seasonNumber: season, episodeNumber: episode, cancellationToken);

        episodeItem.SeriesId ??= series.Id;
        episodeItem.SeasonId ??= seasonItem.Id;
        return episodeItem;
    }

    private async Task<MediaItem> GetOrCreateContainerAsync(
        Catalog catalog, MediaKind kind, string provider, string seriesProviderId,
        Func<MediaItem> factory, int? seasonNumber, int? episodeNumber, CancellationToken cancellationToken)
    {
        var existing = await database.MediaItems.FirstOrDefaultAsync(item =>
            item.CatalogId == catalog.Id &&
            item.Kind == kind &&
            item.IdentityProvider == provider &&
            item.IdentityProviderId == seriesProviderId &&
            item.IdentitySeasonNumber == seasonNumber &&
            item.IdentityEpisodeNumber == episodeNumber, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = factory();
        var now = DateTimeOffset.UtcNow;
        created.AddedAt = now;
        created.UpdatedAt = now;
        database.MediaItems.Add(created);

        // Flush so subsequent container lookups in the same drive see this row.
        await database.SaveChangesAsync(cancellationToken);
        return created;
    }

    private static string DeriveName(string relativePath, string? fallbackName)
    {
        var fileName = Path.GetFileName(relativePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallbackName ?? relativePath : fileName;
    }
}
