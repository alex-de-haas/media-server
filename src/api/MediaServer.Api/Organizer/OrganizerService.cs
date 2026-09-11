using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Organizer;

public sealed class OrganizerService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    ILogger<OrganizerService> logger)
    : IOrganizer
{
    // Path equality follows the filesystem: case-insensitive on Windows and default macOS, ordinal elsewhere.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public async Task<IReadOnlyList<OrganizedFile>> OrganizeAsync(
        IReadOnlyList<SourceFile> sourceFiles, Catalog catalog, CancellationToken cancellationToken)
    {
        var paths = CatalogPaths.For(catalog);
        paths.EnsureCreated();

        var organized = new List<OrganizedFile>();
        var stagingToClean = new HashSet<string>(StringComparer.Ordinal);
        // Cleanup is recursive: spare roots holding an unorganized file even when a sibling moved out.
        var stagingKept = new HashSet<string>(StringComparer.Ordinal);

        // Match database claims using the filesystem's case rules, not SQLite's default collation.
        // Load once for the whole batch so a season pack does not rescan the catalog for every episode.
        var publishedClaims = await database.MediaSources
            .Where(source => source.MediaItem!.CatalogId == catalog.Id)
            .Select(source => new { source.Path, source.SourceFileId }).ToListAsync(cancellationToken);
        var ingestClaims = await database.SourceFiles
            .Where(file => file.IngestItem!.CatalogId == catalog.Id)
            .Select(file => new { Path = file.RelativePath, SourceFileId = (Guid?)file.Id }).ToListAsync(cancellationToken);
        var claims = publishedClaims.Concat(ingestClaims).ToLookup(claim => claim.Path,
            claim => claim.SourceFileId,
            PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void KeepStaging(SourceFile file)
        {
            if (StagingRootOf(file.RelativePath) is { } root && sandbox.TryResolve(catalog, root, out var absolute))
            {
                stagingKept.Add(absolute);
            }
        }

        // Companion audio tracks and subtitles are not organized here: their names derive from the video's
        // canonical one, so they are placed afterwards (see SidecarPlacementService). Their staging root
        // must survive this sweep — recursive deletion would take the only copy of a dub with it.
        foreach (var companion in sourceFiles.Where(file =>
            file.AssignmentStatus == SourceFileAssignmentStatus.Confirmed && MediaFormats.IsCompanion(file.RelativePath)))
        {
            KeepStaging(companion);
        }

        // Group by assigned media item so that when several files map to one movie/episode (e.g. a
        // black-and-white and a regular cut of the same episode) each gets a distinct canonical path —
        // alternate versions of one item — instead of colliding on the (item, path) unique index.
        var groups = sourceFiles
            .Where(file => file.MediaItemId is not null && MediaFormats.IsPlayableMedia(file.RelativePath, file.SizeBytes))
            .GroupBy(file => file.MediaItemId!.Value);

        foreach (var group in groups)
        {
            var item = await database.MediaItems.FirstOrDefaultAsync(media => media.Id == group.Key, cancellationToken);
            if (item is null)
            {
                continue;
            }

            // Stable order so the primary version chosen for the item's LibraryPath and any ordinal
            // fallback labels are deterministic across re-runs.
            var filesInGroup = group
                .OrderBy(file => file.TorrentFileIndex ?? int.MaxValue)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();

            // Only a multi-file item needs version labels; a lone file keeps its plain canonical name.
            var editions = filesInGroup.Count > 1
                ? EditionLabeler.Label(filesInGroup.Select(file => file.RelativePath).ToList())
                : null;

            var libraryPathSet = false;
            for (var index = 0; index < filesInGroup.Count; index++)
            {
                var sourceFile = filesInGroup[index];
                var edition = sourceFile.Edition ?? editions?[index];

                if (!sandbox.TryResolve(catalog, sourceFile.RelativePath, out var sourceAbsolute))
                {
                    logger.LogWarning("Refusing to organize unresolved source path {Path}", sourceFile.RelativePath);
                    continue;
                }

                if (!File.Exists(sourceAbsolute))
                {
                    logger.LogWarning("Source file missing for organize: {Path}", sourceAbsolute);
                    continue;
                }

                var extension = Path.GetExtension(sourceFile.RelativePath);
                var canonicalRelative = await BuildLibraryPathAsync(catalog, item, extension, edition, cancellationToken);

                // A file scanned from an already-organized library can already sit at its canonical path for a
                // non-null edition — "<canonical stem> - <label>.<ext>", exactly what LibraryNaming writes for a
                // version and what transcode-engine emits. Alone in its ingest (which is how a scan queues every
                // file) there is no sibling for EditionLabeler to diff against, so the name on disk is the only
                // evidence of the label. Recover it instead of renaming the file onto the plain canonical name,
                // which belongs to a different version.
                if (edition is null && RecoverEdition(sourceFile.RelativePath, canonicalRelative) is { } recovered)
                {
                    edition = recovered;
                    canonicalRelative = await BuildLibraryPathAsync(catalog, item, extension, edition, cancellationToken);
                }

                if (!sandbox.TryResolve(catalog, canonicalRelative, out var canonicalAbsolute))
                {
                    logger.LogWarning("Refusing to organize outside catalog root: {Path}", canonicalRelative);
                    continue;
                }

                // Separate downloads do not meet in EditionLabeler. Allocate a free version name against
                // both disk and database claims, including claims whose files are temporarily missing.
                var baseEdition = edition;
                var versionNumber = 2;
                while (!string.Equals(sourceAbsolute, canonicalAbsolute, PathComparison) &&
                       (File.Exists(canonicalAbsolute) || Directory.Exists(canonicalAbsolute) ||
                        claims[canonicalRelative].Any(owner => owner != sourceFile.Id)))
                {
                    edition = baseEdition is null ? $"Version {versionNumber++}" : $"{baseEdition} {versionNumber++}";
                    canonicalRelative = await BuildLibraryPathAsync(catalog, item, extension, edition, cancellationToken);
                    if (!sandbox.TryResolve(catalog, canonicalRelative, out canonicalAbsolute))
                    {
                        throw new IOException($"Cannot resolve library version path: {canonicalRelative}");
                    }
                }

                // A case-only path change on a case-insensitive filesystem maps to the same file — skip the move.
                if (!string.Equals(sourceAbsolute, canonicalAbsolute, PathComparison))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(canonicalAbsolute)!);
                    // Never overwrite: a concurrent claimant makes this ingest fail safely and retry.
                    File.Move(sourceAbsolute, canonicalAbsolute);

                    // The staging folder may go now that its file actually moved out. Recorded here rather
                    // than before the move so a failed file never schedules its own staging root for cleanup.
                    if (StagingRootOf(sourceFile.RelativePath) is { } stagingRoot &&
                        sandbox.TryResolve(catalog, stagingRoot, out var stagingAbsolute))
                    {
                        stagingToClean.Add(stagingAbsolute);
                    }
                }

                sourceFile.RelativePath = canonicalRelative;
                sourceFile.Edition = edition;
                sourceFile.UpdatedAt = DateTimeOffset.UtcNow;

                // The item's LibraryPath tracks the primary (first successfully organized) version; the
                // per-file MediaSource rows probed next are the real source of truth for every version.
                if (!libraryPathSet)
                {
                    item.LibraryPath = canonicalRelative;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                    libraryPathSet = true;
                }

                organized.Add(new OrganizedFile(sourceFile.Id, item.Id, canonicalRelative, canonicalAbsolute));
            }
        }

        await database.SaveChangesAsync(cancellationToken);

        var organizedIds = organized.Select(file => file.SourceFileId).ToHashSet();
        foreach (var file in sourceFiles.Where(file =>
            file.MediaItemId is not null && MediaFormats.IsPlayableMedia(file.RelativePath, file.SizeBytes) &&
            !organizedIds.Contains(file.Id)))
        {
            KeepStaging(file);
        }

        // Skipped files (unmatchable extras the operator excluded) are never grouped/organized above, so a
        // skip-only torrent ingest would otherwise leave its whole .incoming/<downloadId>/ staging — and the
        // skipped files inside it — on disk forever. Note their staging roots here so the recursive cleanup
        // below sweeps them. Scan-imported files sit outside .incoming/ (StagingRootOf → null) and are left
        // in place; the operator's own on-disk file is never deleted by a skip.
        foreach (var skipped in sourceFiles.Where(file => file.AssignmentStatus == SourceFileAssignmentStatus.Skipped))
        {
            if (StagingRootOf(skipped.RelativePath) is { } stagingRoot &&
                sandbox.TryResolve(catalog, stagingRoot, out var stagingAbsolute))
            {
                stagingToClean.Add(stagingAbsolute);
            }
        }

        // Remove emptied .incoming/<downloadId>/ staging folders (torrent leftovers: samples, .nfo, extras),
        // except any still holding a file the organizer deliberately left alone.
        foreach (var staging in stagingToClean.Except(stagingKept))
        {
            TryDeleteDirectory(staging);
        }

        return organized;
    }

    /// <summary>The <c>.incoming/&lt;downloadId&gt;</c> staging root of a path, or null if it is not staged.</summary>
    private static string? StagingRootOf(string relativePath)
    {
        if (!CatalogPaths.IsIncoming(relativePath))
        {
            return null;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : segments[0];
    }

    private void TryDeleteDirectory(string absolute)
    {
        try
        {
            if (Directory.Exists(absolute))
            {
                Directory.Delete(absolute, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to remove staging folder {Path}", absolute);
        }
    }

    /// <summary>
    /// Reads back the <c> - {edition}</c> suffix <see cref="LibraryNaming"/> writes: returns the label when
    /// <paramref name="actualRelative"/> is <paramref name="canonicalRelative"/> with a suffix appended to the
    /// filename stem, otherwise null. Requires the same folder and differs only by the suffix, so a title that
    /// itself contains " - " (e.g. "Mission Impossible - Fallout") is not mistaken for a version — its
    /// canonical stem already carries the hyphen and matches exactly.
    /// </summary>
    private static string? RecoverEdition(string actualRelative, string canonicalRelative)
    {
        if (!string.Equals(FolderOf(actualRelative), FolderOf(canonicalRelative), PathComparison))
        {
            return null;
        }

        var actualStem = Path.GetFileNameWithoutExtension(actualRelative);
        var prefix = Path.GetFileNameWithoutExtension(canonicalRelative) + " - ";
        if (!actualStem.StartsWith(prefix, PathComparison))
        {
            return null;
        }

        var label = actualStem[prefix.Length..].Trim();
        return label.Length == 0 ? null : label;
    }

    // Catalog-relative paths are posix-style (see ToRelative in LibraryImportService).
    private static string FolderOf(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private async Task<string> BuildLibraryPathAsync(
        Catalog catalog, MediaItem item, string extension, string? edition, CancellationToken cancellationToken)
    {
        if (item.Kind is MediaKind.Episode or MediaKind.Video)
        {
            var series = item.SeriesId is { } seriesId
                ? await database.MediaItems.FirstOrDefaultAsync(media => media.Id == seriesId, cancellationToken)
                : null;

            // A series extra lives in the show's extras/ folder; a Video without a series (not produced by
            // the ingest flow, but tolerated) falls through to the movie template below.
            if (item.Kind == MediaKind.Video)
            {
                if (series is not null)
                {
                    return LibraryNaming.ForExtra(series, item, extension, edition);
                }
            }
            else
            {
                // Fall back to the episode's own title if the series row is missing.
                return LibraryNaming.ForEpisode(series ?? item, item, extension, edition);
            }
        }

        return LibraryNaming.ForMovie(catalog, item, extension, edition);
    }
}
