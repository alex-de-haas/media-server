using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

public sealed record IdentifyOutcome(bool AllResolved, string? ReviewReason, IReadOnlyList<MetadataCandidate> Candidates);

/// <summary>
/// A pinned identity for an ingest item: resolve its files against this provider reference directly, skipping
/// the name-parse + provider-search + confidence-scoring path (and therefore never routing to review).
/// <see cref="Kind"/> is <see cref="MediaKind.Movie"/> or <see cref="MediaKind.Series"/> — for a series the pin
/// is the owning show, and each file's season/episode still come from its parsed name.
/// </summary>
public sealed record TargetIdentity(string Provider, string ProviderId, MediaKind Kind, string Title, int? Year);

/// <summary>
/// Maps playable source files to movies or episodes: parses the name, searches the provider, scores
/// candidates, and on a high-confidence hit creates/reuses the canonical <see cref="MediaItem"/>
/// hierarchy and assigns the file. When the item carries a pinned <see cref="TargetIdentity"/> the search
/// and scoring are skipped — the file is resolved straight to that identity. Idempotent — re-identifying
/// reuses existing items by identity.
/// </summary>
public sealed class IdentifyService(
    MediaServerDbContext database, INameParser parser, IMetadataProvider provider, AppSettingsService appSettings, ILogger<IdentifyService> logger)
{
    public async Task<IdentifyOutcome> IdentifyAsync(
        Catalog catalog, IReadOnlyList<SourceFile> sourceFiles, string? fallbackName, TargetIdentity? target, CancellationToken cancellationToken)
    {
        var unresolved = new List<MetadataCandidate>();
        var reviewReasons = new List<string>();
        var releaseGroups = await appSettings.GetCustomReleaseGroupsAsync(cancellationToken);

        // Videos resolve first; external audio tracks then match against the videos' items (they carry no
        // searchable identity of their own — "[Group] Show 05.mka" is a dub of this batch's episode 5).
        var videoFiles = sourceFiles.Where(file => !MediaFormats.IsCompanionAudio(file.RelativePath)).ToList();
        var audioFiles = sourceFiles.Where(file => MediaFormats.IsCompanionAudio(file.RelativePath)).ToList();

        // The items this run resolves, collected as we go: movies added by ResolveMovieAsync aren't flushed
        // until the final save, so a store query couldn't see them for the audio pass below.
        var assignedItems = new Dictionary<Guid, MediaItem>();

        foreach (var sourceFile in videoFiles)
        {
            if (sourceFile.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && sourceFile.MediaItemId is not null)
            {
                continue; // Already mapped (operator confirm or a prior run).
            }

            if (sourceFile.AssignmentStatus == SourceFileAssignmentStatus.Skipped)
            {
                continue; // Operator excluded it (an unmatchable extra) — leave it unmapped, don't re-search.
            }

            // A recognizable extra (creditless OP/ED, PV, menu, …) has no provider identity — searching for
            // it is wasted at best and a false match at worst. Park it for review with a concrete hint; the
            // review dialog pre-suggests attaching it to its series as an extra (or skipping it).
            if (ExtraClassifier.Classify(sourceFile.RelativePath, catalog.Type) is { } extra)
            {
                sourceFile.AssignmentStatus = SourceFileAssignmentStatus.NeedsReview;
                sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
                reviewReasons.Add(
                    $"'{DeriveName(sourceFile.RelativePath, fallbackName)}' looks like an extra ({extra.Title}) — " +
                    (extra.SuggestSkip ? "skip it, or attach it to its series." : "attach it to its series, or skip it."));
                continue;
            }

            var name = DeriveName(sourceFile.RelativePath, fallbackName);
            var parsed = parser.Parse(name, catalog.Type, releaseGroups);

            MediaItem mediaItem;
            if (target is not null)
            {
                // Pinned identity: resolve straight to the target, no provider search (and thus no scoring).
                var pinned = new MetadataCandidate(new ProviderRef(target.Provider, target.ProviderId), target.Title, target.Year, 1.0);
                if (target.Kind == MediaKind.Series)
                {
                    // The show is pinned, but each file still needs its own episode number from the name. A file
                    // with no SxxEyy (parses to a series-level title) can't be placed under a specific episode,
                    // so route just that file to review rather than silently inventing S01E00.
                    if (parsed.Episode is null)
                    {
                        sourceFile.AssignmentStatus = SourceFileAssignmentStatus.NeedsReview;
                        sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
                        reviewReasons.Add($"Pinned to '{target.Title}', but no episode number was found in '{parsed.Title}'.");
                        continue;
                    }

                    mediaItem = await ResolveEpisodeAsync(catalog, pinned, parsed, cancellationToken);
                }
                else
                {
                    mediaItem = await ResolveMovieAsync(catalog, pinned, cancellationToken);
                }
            }
            else
            {
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

                mediaItem = parsed.Kind == MediaKind.Episode
                    ? await ResolveEpisodeAsync(catalog, best, parsed, cancellationToken)
                    : await ResolveMovieAsync(catalog, best, cancellationToken);
            }

            sourceFile.MediaItemId = mediaItem.Id;
            sourceFile.AssignmentStatus = SourceFileAssignmentStatus.Confirmed;
            sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
            assignedItems[mediaItem.Id] = mediaItem;

            logger.LogInformation("Matched {File} → {Kind} '{Title}' ({Provider}:{Id}).",
                sourceFile.RelativePath, mediaItem.Kind, mediaItem.Title, mediaItem.IdentityProvider, mediaItem.IdentityProviderId);
        }

        if (audioFiles.Count > 0)
        {
            // Videos confirmed before this run (an operator match, or a prior drive) skipped the loop above;
            // load their items so the audio pass can match against the whole batch.
            var priorIds = videoFiles
                .Where(file => file is { AssignmentStatus: SourceFileAssignmentStatus.Confirmed, MediaItemId: { } id } && !assignedItems.ContainsKey(id))
                .Select(file => file.MediaItemId!.Value)
                .Distinct()
                .ToList();
            foreach (var item in await database.MediaItems.Where(item => priorIds.Contains(item.Id)).ToListAsync(cancellationToken))
            {
                assignedItems[item.Id] = item;
            }

            MatchAudioTracks(catalog, audioFiles, assignedItems.Values, releaseGroups, reviewReasons);
        }

        await database.SaveChangesAsync(cancellationToken);

        var allResolved = sourceFiles.All(file =>
            (file.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && file.MediaItemId is not null) ||
            file.AssignmentStatus is SourceFileAssignmentStatus.Skipped or SourceFileAssignmentStatus.Merged);
        return new IdentifyOutcome(allResolved, allResolved ? null : string.Join(" ", reviewReasons.Distinct()), unresolved);
    }

    /// <summary>
    /// Matches external audio tracks to this batch's resolved videos: the single movie for a movie batch,
    /// otherwise by the episode number parsed from the track's file name (the season disambiguates when
    /// two seasons share an episode number). Matching assigns the video's own media item — the mux stage
    /// later merges the track into that item's video file. A track that can't be placed routes to review,
    /// where the operator matches it to its episode or skips it.
    /// </summary>
    private void MatchAudioTracks(
        Catalog catalog, IReadOnlyList<SourceFile> audioFiles, IReadOnlyCollection<MediaItem> videoItems,
        IReadOnlyCollection<string> releaseGroups, List<string> reviewReasons)
    {
        var movies = videoItems.Where(item => item.Kind == MediaKind.Movie).ToList();
        var episodes = videoItems.Where(item => item.Kind == MediaKind.Episode).ToList();

        foreach (var audio in audioFiles)
        {
            if ((audio.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && audio.MediaItemId is not null) ||
                audio.AssignmentStatus is SourceFileAssignmentStatus.Skipped or SourceFileAssignmentStatus.Merged)
            {
                continue;
            }

            var name = Path.GetFileName(audio.RelativePath);
            MediaItem? matched = null;
            string? failure = null;

            if (movies.Count == 1 && episodes.Count == 0)
            {
                matched = movies[0];
            }
            else if (catalog.Type == CatalogType.Movie)
            {
                failure = movies.Count == 0
                    ? $"'{name}' looks like an audio track, but no movie is matched in this batch yet"
                    : $"'{name}' looks like an audio track, but this batch has several movies";
            }
            else if (parser.Parse(name, catalog.Type, releaseGroups) is not { Episode: { } episode } parsed)
            {
                failure = $"'{name}' looks like an audio track, but no episode number was found in its name";
            }
            else
            {
                var candidates = episodes.Where(item => item.IndexNumber == episode).ToList();
                if (candidates.Count > 1 && parsed.Season is not null)
                {
                    candidates = candidates.Where(item => item.ParentIndexNumber == parsed.Season).ToList();
                }

                (matched, failure) = candidates switch
                {
                    [var single] => (single, (string?)null),
                    [] => (null, $"'{name}' looks like an audio track, but episode {episode} has no video in this batch"),
                    _ => (null, $"'{name}' looks like an audio track, but episode {episode} is ambiguous in this batch"),
                };
            }

            if (matched is not null)
            {
                audio.MediaItemId = matched.Id;
                audio.AssignmentStatus = SourceFileAssignmentStatus.Confirmed;
                audio.UpdatedAt = DateTimeOffset.UtcNow;
                logger.LogInformation("Matched audio track {File} → {Kind} '{Title}' for muxing.",
                    audio.RelativePath, matched.Kind, matched.Title);
            }
            else
            {
                audio.AssignmentStatus = SourceFileAssignmentStatus.NeedsReview;
                audio.UpdatedAt = DateTimeOffset.UtcNow;
                reviewReasons.Add($"{failure} — match it to its episode (the track is merged into that video), or skip it.");
            }
        }
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

    /// <summary>Gets or creates the series container for a provider identity (no season/episode).</summary>
    public async Task<MediaItem> ResolveSeriesAsync(
        Catalog catalog, MetadataCandidate seriesCandidate, CancellationToken cancellationToken)
    {
        var provider = seriesCandidate.Reference.Provider;
        var seriesId = seriesCandidate.Reference.Id;

        return await GetOrCreateContainerAsync(catalog, MediaKind.Series, provider, seriesId,
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
    }

    /// <summary>
    /// Gets or creates the extra (a playable non-episode <see cref="MediaKind.Video"/>) with the given title
    /// under a series — optionally scoped to a season. Extras carry no provider identity of their own (the
    /// provider has no entry for a creditless OP/ED); their stable identity is the series + title, so a
    /// re-imported extra with the same title becomes another version of the existing item. A created extra
    /// is only added to the context — persistence rides the caller's <c>SaveChangesAsync</c> (callers keep
    /// batch titles unique, so an unflushed sibling can never be a lookup target).
    /// </summary>
    public async Task<MediaItem> ResolveExtraAsync(
        Catalog catalog, MediaItem series, string title, int? seasonNumber, CancellationToken cancellationToken)
    {
        MediaItem? seasonItem = null;
        if (seasonNumber is { } season && series is { IdentityProvider: { } provider, IdentityProviderId: { } providerId })
        {
            seasonItem = await GetOrCreateContainerAsync(catalog, MediaKind.Season, provider, providerId,
                () => new MediaItem
                {
                    Id = Guid.NewGuid(),
                    CatalogId = catalog.Id,
                    Kind = MediaKind.Season,
                    Title = $"Season {season}",
                    ParentId = series.Id,
                    SeriesId = series.Id,
                    IdentityProvider = provider,
                    IdentityProviderId = providerId,
                    IdentitySeasonNumber = season,
                    ParentIndexNumber = season,
                    IndexNumber = season,
                    Providers = new Dictionary<string, string> { [provider] = providerId },
                }, seasonNumber: season, episodeNumber: null, cancellationToken);
        }

        var existing = await database.MediaItems.FirstOrDefaultAsync(item =>
            item.CatalogId == catalog.Id &&
            item.Kind == MediaKind.Video &&
            item.SeriesId == series.Id &&
            item.Title == title, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var extra = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Video,
            Title = title,
            ParentId = seasonItem?.Id ?? series.Id,
            SeriesId = series.Id,
            SeasonId = seasonItem?.Id,
            AddedAt = now,
            UpdatedAt = now,
        };
        database.MediaItems.Add(extra);
        return extra;
    }

    public async Task<MediaItem> ResolveEpisodeAsync(
        Catalog catalog, MetadataCandidate seriesCandidate, ParsedName parsed, CancellationToken cancellationToken)
    {
        var season = parsed.Season ?? 1;
        var episode = parsed.Episode ?? 0;
        var provider = seriesCandidate.Reference.Provider;
        var seriesId = seriesCandidate.Reference.Id;

        var series = await ResolveSeriesAsync(catalog, seriesCandidate, cancellationToken);

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
