using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// What one identify pass concluded. <paramref name="ConflictCatalogId"/> is set only when a file was
/// parked because its identity already lives in another catalog — it names the Retarget destination.
/// </summary>
public sealed record IdentifyOutcome(
    bool AllResolved,
    string? ReviewReason,
    IReadOnlyList<MetadataCandidate> Candidates,
    Guid? ConflictCatalogId = null);

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
        // Distinct because one batch can collide with two different catalogs (a franchise pack whose
        // films live apart); a single retarget destination is only honest when there is exactly one.
        var conflictCatalogIds = new HashSet<Guid>();
        var releaseGroups = await appSettings.GetCustomReleaseGroupsAsync(cancellationToken);

        // Videos resolve first; companion tracks then match against the videos' items. A companion carries
        // no searchable identity of its own — "[Group] Show 05.mka" is a dub of this batch's episode 5, and
        // "Форсированные.srt" is a subtitle for its film, not a film called "Форсированные". Subtitles are
        // grouped with the dubs here for exactly that reason: identified as content they would each invent
        // a title of their own and make the batch look like it holds several.
        var videoFiles = sourceFiles.Where(file => !MediaFormats.IsCompanion(file.RelativePath)).ToList();
        var companionFiles = sourceFiles.Where(file => MediaFormats.IsCompanion(file.RelativePath)).ToList();

        // The items this run resolves, collected as we go: movies added by ResolveMovieAsync aren't flushed
        // until the final save, so a store query couldn't see them for the companion pass below.
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

            // The identity this file resolves to, and whether it lands as an episode of a series or as a
            // movie — both paths converge here so the cross-catalog gate below sees every resolution.
            MetadataCandidate identity;
            bool asEpisode;
            if (target is not null)
            {
                // Pinned identity: resolve straight to the target, no provider search (and thus no scoring).
                identity = new MetadataCandidate(new ProviderRef(target.Provider, target.ProviderId), target.Title, target.Year, 1.0);
                asEpisode = target.Kind == MediaKind.Series;

                // The show is pinned, but each file still needs its own episode number from the name. A file
                // with no SxxEyy (parses to a series-level title) can't be placed under a specific episode,
                // so route just that file to review rather than silently inventing S01E00.
                if (asEpisode && parsed.Episode is null)
                {
                    sourceFile.AssignmentStatus = SourceFileAssignmentStatus.NeedsReview;
                    sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
                    reviewReasons.Add($"Pinned to '{target.Title}', but no episode number was found in '{parsed.Title}'.");
                    continue;
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

                identity = best;
                asEpisode = parsed.Kind == MediaKind.Episode;
            }

            // A work lives in exactly one catalog: publishing this identity here while another catalog
            // already holds it would split watched state and favorites across two rows. Park instead —
            // the review offers Retarget (re-home the ingest to that catalog, where it merges as another
            // version) or Skip.
            if (await FindCrossCatalogConflictAsync(catalog, asEpisode, identity.Reference, cancellationToken) is { } conflict)
            {
                sourceFile.AssignmentStatus = SourceFileAssignmentStatus.NeedsReview;
                sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
                conflictCatalogIds.Add(conflict.CatalogId);
                reviewReasons.Add(
                    $"{Describe(identity)} is already in catalog '{conflict.CatalogName}' — a title lives in one " +
                    "catalog only. Retarget this download to that catalog, or skip it.");
                continue;
            }

            var mediaItem = asEpisode
                ? await ResolveEpisodeAsync(catalog, identity, parsed, cancellationToken)
                : await ResolveMovieAsync(catalog, identity, cancellationToken);

            sourceFile.MediaItemId = mediaItem.Id;
            sourceFile.AssignmentStatus = SourceFileAssignmentStatus.Confirmed;
            sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
            assignedItems[mediaItem.Id] = mediaItem;

            logger.LogInformation("Matched {File} → {Kind} '{Title}' ({Provider}:{Id}).",
                sourceFile.RelativePath, mediaItem.Kind, mediaItem.Title, mediaItem.IdentityProvider, mediaItem.IdentityProviderId);
        }

        if (companionFiles.Count > 0)
        {
            // Videos confirmed before this run (an operator match, or a prior drive) skipped the loop above;
            // load their items so the companion pass can match against the whole batch.
            var priorIds = videoFiles
                .Where(file => file is { AssignmentStatus: SourceFileAssignmentStatus.Confirmed, MediaItemId: { } id } && !assignedItems.ContainsKey(id))
                .Select(file => file.MediaItemId!.Value)
                .Distinct()
                .ToList();
            foreach (var item in await database.MediaItems.Where(item => priorIds.Contains(item.Id)).ToListAsync(cancellationToken))
            {
                assignedItems[item.Id] = item;
            }

            MatchCompanionTracks(catalog, companionFiles, assignedItems.Values, releaseGroups, reviewReasons);
        }

        await database.SaveChangesAsync(cancellationToken);

        var allResolved = sourceFiles.All(file =>
            (file.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && file.MediaItemId is not null) ||
            file.AssignmentStatus is SourceFileAssignmentStatus.Skipped or SourceFileAssignmentStatus.Merged);
        return new IdentifyOutcome(
            allResolved,
            allResolved ? null : string.Join(" ", reviewReasons.Distinct()),
            unresolved,
            // No destination when the batch collides with several catalogs: moving it to one of them
            // would leave the others conflicting, so those reasons stand on their own and the review
            // offers no retarget.
            allResolved || conflictCatalogIds.Count != 1 ? null : conflictCatalogIds.Single());
    }

    /// <summary>
    /// The published item holding this identity in a <b>different</b> catalog, if any — the check every
    /// path that creates library items runs before it does so (identification, and the operator's own
    /// match/extras actions in <see cref="IngestService"/>). Nothing is reported when this catalog
    /// already publishes the identity: adding another version beside it is the ordinary path, and a
    /// pre-existing duplicate pair is the audit's business, not the gate's. Tombstones count on neither
    /// side: a ghost here would otherwise be revived into a second published copy, and a ghost elsewhere
    /// carries no files to conflict with.
    /// </summary>
    internal async Task<(Guid CatalogId, string CatalogName)?> FindCrossCatalogConflictAsync(
        Catalog catalog, bool asEpisode, ProviderRef reference, CancellationToken cancellationToken)
    {
        var kind = asEpisode ? MediaKind.Series : MediaKind.Movie;

        var here = await database.MediaItems.AsNoTracking().AnyAsync(item =>
            item.CatalogId == catalog.Id && item.Kind == kind && item.RemovedAt == null &&
            item.IdentityProvider == reference.Provider && item.IdentityProviderId == reference.Id,
            cancellationToken);
        if (here)
        {
            return null;
        }

        return await database.MediaItems.AsNoTracking()
            .Where(item => item.CatalogId != null && item.CatalogId != catalog.Id && item.PublicId != null &&
                item.Kind == kind &&
                item.IdentityProvider == reference.Provider && item.IdentityProviderId == reference.Id)
            .Join(database.Catalogs.AsNoTracking(), item => item.CatalogId, other => (Guid?)other.Id,
                (_, other) => new { other.Id, other.Name })
            .Select(row => new ValueTuple<Guid, string>(row.Id, row.Name))
            .Cast<(Guid CatalogId, string CatalogName)?>()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>"Dune (2021)" — the operator-facing name of an identity in a review reason.</summary>
    private static string Describe(MetadataCandidate identity) =>
        identity.Year is { } year ? $"'{identity.Title}' ({year})" : $"'{identity.Title}'";

    /// <summary>
    /// Matches external audio tracks and subtitles to this batch's resolved videos: the single movie for a
    /// movie batch, otherwise by the episode number parsed from the track's file name (the season
    /// disambiguates when two seasons share an episode number). Matching assigns the video's own media item
    /// — the sidecar stage later places the track beside that item's video file. A track that can't be
    /// placed routes to review, where the operator matches it to its episode or skips it.
    /// </summary>
    private void MatchCompanionTracks(
        Catalog catalog, IReadOnlyList<SourceFile> companionFiles, IReadOnlyCollection<MediaItem> videoItems,
        IReadOnlyCollection<string> releaseGroups, List<string> reviewReasons)
    {
        var movies = videoItems.Where(item => item.Kind == MediaKind.Movie).ToList();
        var episodes = videoItems.Where(item => item.Kind == MediaKind.Episode).ToList();

        foreach (var companion in companionFiles)
        {
            if ((companion.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && companion.MediaItemId is not null) ||
                companion.AssignmentStatus is SourceFileAssignmentStatus.Skipped or SourceFileAssignmentStatus.Merged)
            {
                continue;
            }

            var name = Path.GetFileName(companion.RelativePath);
            // Named for what the file is, so the review reason reads true for a .srt as well as an .mka.
            var kind = MediaFormats.IsCompanionAudio(companion.RelativePath) ? "an audio track" : "a subtitle";
            MediaItem? matched = null;
            string? failure = null;

            if (movies.Count == 1 && episodes.Count == 0)
            {
                matched = movies[0];
            }
            else if (catalog.Type == CatalogType.Movie)
            {
                failure = movies.Count == 0
                    ? $"'{name}' looks like {kind}, but no movie is matched in this batch yet"
                    : $"'{name}' looks like {kind}, but this batch has several movies";
            }
            else if (parser.Parse(name, catalog.Type, releaseGroups) is not { Episode: { } episode } parsed)
            {
                failure = $"'{name}' looks like {kind}, but no episode number was found in its name";
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
                    [] => (null, $"'{name}' looks like {kind}, but episode {episode} has no video in this batch"),
                    _ => (null, $"'{name}' looks like {kind}, but episode {episode} is ambiguous in this batch"),
                };
            }

            if (matched is not null)
            {
                companion.MediaItemId = matched.Id;
                companion.AssignmentStatus = SourceFileAssignmentStatus.Confirmed;
                companion.UpdatedAt = DateTimeOffset.UtcNow;
                logger.LogInformation("Matched companion track {File} → {Kind} '{Title}' for placement.",
                    companion.RelativePath, matched.Kind, matched.Title);
            }
            else
            {
                // The reason is re-reported every drive, but an already-parked row isn't re-written —
                // no redundant UPDATE/broadcast when identify re-runs over a parked batch.
                if (companion.AssignmentStatus != SourceFileAssignmentStatus.NeedsReview)
                {
                    companion.AssignmentStatus = SourceFileAssignmentStatus.NeedsReview;
                    companion.UpdatedAt = DateTimeOffset.UtcNow;
                }

                reviewReasons.Add($"{failure} — match it to its episode (the track is placed beside that video), or skip it.");
            }
        }
    }

    public async Task<MediaItem> ResolveMovieAsync(Catalog catalog, MetadataCandidate candidate, CancellationToken cancellationToken)
    {
        // Published first: if a live row and a ghost share the identity in this catalog (a move can
        // leave that shape behind), new sources belong on the live one.
        var existing = await database.MediaItems
            .Where(item =>
                item.CatalogId == catalog.Id &&
                item.Kind == MediaKind.Movie &&
                item.IdentityProvider == candidate.Reference.Provider &&
                item.IdentityProviderId == candidate.Reference.Id)
            .OrderBy(item => item.PublicId == null ? 1 : 0)
            .FirstOrDefaultAsync(cancellationToken)
            // No live or ghost row in this catalog — a tombstone elsewhere (or catalog-less after its
            // catalog was deleted) is adopted instead, so a re-downloaded title finds its history.
            ?? await FindTombstoneAsync(MediaKind.Movie, candidate.Reference.Provider, candidate.Reference.Id,
                seasonNumber: null, episodeNumber: null, cancellationToken);

        if (existing is not null)
        {
            await AdoptIfTombstoneAsync(existing, catalog, cancellationToken);
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
        // Published first (a move can leave a live row and a ghost sharing an identity in one catalog).
        // Foreign tombstones are only searched for the series itself: a season or episode ghost carries
        // parent links into its original hierarchy, so it may only come back through its series' adoption
        // (which re-homes the whole ghost subtree, making the same-catalog lookup above find it).
        var existing = await database.MediaItems
            .Where(item =>
                item.CatalogId == catalog.Id &&
                item.Kind == kind &&
                item.IdentityProvider == provider &&
                item.IdentityProviderId == seriesProviderId &&
                item.IdentitySeasonNumber == seasonNumber &&
                item.IdentityEpisodeNumber == episodeNumber)
            .OrderBy(item => item.PublicId == null ? 1 : 0)
            .FirstOrDefaultAsync(cancellationToken)
            ?? (kind == MediaKind.Series
                ? await FindTombstoneAsync(kind, provider, seriesProviderId, seasonNumber, episodeNumber, cancellationToken)
                : null);

        if (existing is not null)
        {
            await AdoptIfTombstoneAsync(existing, catalog, cancellationToken);
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

    /// <summary>
    /// The tombstone for an identity, wherever it lies: in another catalog, or catalog-less after its
    /// catalog was deleted. Published rows in other catalogs are deliberately not matched — only a
    /// ghost may cross a catalog boundary. The same-catalog lookup runs first at every call site, so a
    /// local match (live or ghost) always wins over a foreign tombstone.
    /// </summary>
    private Task<MediaItem?> FindTombstoneAsync(
        MediaKind kind, string provider, string providerId, int? seasonNumber, int? episodeNumber,
        CancellationToken cancellationToken) =>
        database.MediaItems.FirstOrDefaultAsync(item =>
            item.RemovedAt != null &&
            item.Kind == kind &&
            item.IdentityProvider == provider &&
            item.IdentityProviderId == providerId &&
            item.IdentitySeasonNumber == seasonNumber &&
            item.IdentityEpisodeNumber == episodeNumber, cancellationToken);

    /// <summary>
    /// Brings a tombstone back to life in <paramref name="catalog"/>: clears <see cref="MediaItem.RemovedAt"/>,
    /// re-homes the row when it came from another (or a deleted) catalog, and drags a series' or movie's
    /// ghost children along — they stay tombstones until their own files arrive, but they must live in the
    /// adopting catalog or the per-catalog lookups above would mint duplicates beside them. Flushed
    /// immediately: later lookups in the same identify run query the database, not the change tracker.
    /// The publish stage then mints the public id as for any unpublished row. No-op for a live item.
    /// </summary>
    private async Task AdoptIfTombstoneAsync(MediaItem item, Catalog catalog, CancellationToken cancellationToken)
    {
        if (item.RemovedAt is null)
        {
            return;
        }

        item.RemovedAt = null;
        item.CatalogId = catalog.Id;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        if (item.Kind is MediaKind.Series or MediaKind.Movie)
        {
            var children = await database.MediaItems
                .Where(child => child.RemovedAt != null &&
                    (child.SeriesId == item.Id || child.ParentId == item.Id))
                .ToListAsync(cancellationToken);
            foreach (var child in children)
            {
                child.CatalogId = catalog.Id;
            }
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    private static string DeriveName(string relativePath, string? fallbackName)
    {
        var fileName = Path.GetFileName(relativePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallbackName ?? relativePath : fileName;
    }
}
