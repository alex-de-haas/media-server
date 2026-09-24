using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Organizer;

public sealed class OrganizerService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    ILogger<OrganizerService> logger,
    FilePlacementService placement)
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

        var downloadId = sourceFiles.Select(x => x.DownloadId).FirstOrDefault(x => x != null);
        var download = downloadId is { } id ? await database.Downloads.FindAsync([id], cancellationToken) : null;
        var copy = download is { KeepSeeding: true, StopRequested: false };
        var organized = new List<OrganizedFile>();
        // Compare paths in SQL using the same Unicode/case rules as the filesystem; SQLite's NOCASE
        // only folds ASCII. Registration also applies when EF opens this connection for a later query.
        const string pathCollation = "organizer_path";
        ((SqliteConnection)database.Database.GetDbConnection()).CreateCollation(pathCollation,
            (left, right) => string.Compare(left, right, PathComparison));
        var claims = new Dictionary<string, HashSet<Guid?>>(
            PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        async Task<bool> IsClaimedAsync(string candidate, Guid sourceFileId)
        {
            if (!claims.TryGetValue(candidate, out var owners))
            {
                // Only read owners of this candidate, and cache across files in this batch. In-place
                // files and destinations already occupied on disk never need a claim query.
                var published = database.MediaSources
                    .Where(source => source.MediaItem!.CatalogId == catalog.Id &&
                        EF.Functions.Collate(source.Path, pathCollation) == candidate)
                    .Select(source => source.SourceFileId);
                var pending = database.SourceFiles
                    .Where(file => file.IngestItem!.CatalogId == catalog.Id &&
                        (EF.Functions.Collate(file.RelativePath, pathCollation) == candidate ||
                         EF.Functions.Collate(file.PlacementPath!, pathCollation) == candidate))
                    .Select(file => (Guid?)file.Id);
                owners = (await published.Concat(pending).ToListAsync(cancellationToken)).ToHashSet();
                claims.Add(candidate, owners);
            }

            return owners.Any(owner => owner != sourceFileId);
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
                var canonicalRelative = sourceFile.PlacementPath ?? await BuildLibraryPathAsync(catalog, item, extension, edition, cancellationToken);

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
                while (sourceFile.PlacementPath is null && !string.Equals(sourceAbsolute, canonicalAbsolute, PathComparison) &&
                       (File.Exists(canonicalAbsolute) || Directory.Exists(canonicalAbsolute) ||
                        await IsClaimedAsync(canonicalRelative, sourceFile.Id)))
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
                    sourceFile.Edition = edition; // Persist the chosen version with the destination reservation.
                    await placement.PlaceAsync(sourceFile, sourceAbsolute, canonicalAbsolute, canonicalRelative, copy, cancellationToken);


                }

                sourceFile.RelativePath = canonicalRelative;
                sourceFile.Edition = edition;
                sourceFile.UpdatedAt = DateTimeOffset.UtcNow;

                // Reserve successful moves immediately, before the batch's database save.
                if (!claims.TryGetValue(canonicalRelative, out var owners))
                {
                    claims.Add(canonicalRelative, owners = []);
                }
                owners.Add(sourceFile.Id);

                // The item's LibraryPath tracks the primary (first successfully organized) version; the
                // per-file MediaSource rows probed next are the real source of truth for every version.
                if (!libraryPathSet)
                {
                    item.LibraryPath = canonicalRelative;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                    libraryPathSet = true;
                }

                await database.SaveChangesAsync(cancellationToken);
                organized.Add(new OrganizedFile(sourceFile.Id, item.Id, canonicalRelative, canonicalAbsolute));
            }
        }

        await database.SaveChangesAsync(cancellationToken);

        return organized;
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
