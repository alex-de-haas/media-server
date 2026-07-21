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
        // Staging roots still holding a file the organizer refused to move. The cleanup below is recursive,
        // so a root must be spared even when a sibling file did organize out of it successfully.
        var stagingKept = new HashSet<string>(StringComparer.Ordinal);

        void KeepStaging(SourceFile file)
        {
            if (StagingRootOf(file.RelativePath) is { } root && sandbox.TryResolve(catalog, root, out var absolute))
            {
                stagingKept.Add(absolute);
            }
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
                var edition = editions?[index];

                if (!sandbox.TryResolve(catalog, sourceFile.RelativePath, out var sourceAbsolute))
                {
                    logger.LogWarning("Refusing to organize unresolved source path {Path}", sourceFile.RelativePath);
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

                // A case-only path change on a case-insensitive filesystem maps to the same file — skip the move.
                if (!string.Equals(sourceAbsolute, canonicalAbsolute, PathComparison))
                {
                    if (!File.Exists(sourceAbsolute))
                    {
                        logger.LogWarning("Source file missing for organize: {Path}", sourceAbsolute);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(canonicalAbsolute)!);
                    if (File.Exists(canonicalAbsolute))
                    {
                        // Replacing a stale leftover is fine, but never destroy a file that already backs a
                        // different version: organizing one file onto another source's canonical path would
                        // silently overwrite it (e.g. a re-ingested transcode output colliding with the
                        // original). In that case leave both files alone and skip this one.
                        var backsAnotherSource = await database.MediaSources.AnyAsync(
                            existing => existing.MediaItem!.CatalogId == catalog.Id &&
                                existing.Path == canonicalRelative && existing.SourceFileId != sourceFile.Id,
                            cancellationToken);

                        // A published MediaSource is not the only claim on a path. Scanning a pre-existing
                        // library queues one ingest per file, so the original and its " - <edition>" transcode
                        // outputs identify as the same item while MediaSources is still empty — whichever
                        // organizes first would delete the others. A file another ingest still owns is a real
                        // library file, never a leftover.
                        var ownedByAnotherIngest = !backsAnotherSource && await database.SourceFiles.AnyAsync(
                            other => other.IngestItem!.CatalogId == catalog.Id &&
                                other.Id != sourceFile.Id && other.RelativePath == canonicalRelative,
                            cancellationToken);

                        if (backsAnotherSource || ownedByAnotherIngest)
                        {
                            logger.LogWarning(
                                "Refusing to organize {Source} onto {Target}: that path already backs another version.",
                                sourceFile.RelativePath, canonicalRelative);
                            KeepStaging(sourceFile);
                            continue;
                        }

                        File.Delete(canonicalAbsolute); // Idempotent re-run: replace a stale, unreferenced leftover.
                    }

                    File.Move(sourceAbsolute, canonicalAbsolute);

                    // The staging folder may go now that its file actually moved out. Recorded here rather
                    // than before the move so a refused file never has its staging root swept from under it —
                    // the sweep is recursive and would delete the very file the refusal above preserved.
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
