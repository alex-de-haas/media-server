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

            // Stable order so the "primary" version (no edition suffix is preferred for the item's
            // LibraryPath) and any ordinal fallback labels are deterministic across re-runs.
            var filesInGroup = group
                .OrderBy(file => file.TorrentFileIndex ?? int.MaxValue)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();

            // Only a multi-file item needs version labels; a lone file keeps its plain canonical name.
            var editions = filesInGroup.Count > 1
                ? EditionLabeler.Label(filesInGroup.Select(file => file.RelativePath).ToList())
                : null;

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

                if (!sandbox.TryResolve(catalog, canonicalRelative, out var canonicalAbsolute))
                {
                    logger.LogWarning("Refusing to organize outside catalog root: {Path}", canonicalRelative);
                    continue;
                }

                // Remember the staging folder so it can be removed once its file(s) move out.
                if (StagingRootOf(sourceFile.RelativePath) is { } stagingRoot &&
                    sandbox.TryResolve(catalog, stagingRoot, out var stagingAbsolute))
                {
                    stagingToClean.Add(stagingAbsolute);
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
                        File.Delete(canonicalAbsolute); // Idempotent re-run: replace any stale file at the destination.
                    }

                    File.Move(sourceAbsolute, canonicalAbsolute);
                }

                sourceFile.RelativePath = canonicalRelative;
                sourceFile.Edition = edition;
                sourceFile.UpdatedAt = DateTimeOffset.UtcNow;

                // The item's LibraryPath tracks the primary (first) version; the per-file MediaSource rows
                // probed next are the real source of truth for every version.
                if (index == 0)
                {
                    item.LibraryPath = canonicalRelative;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                }

                organized.Add(new OrganizedFile(sourceFile.Id, item.Id, canonicalRelative, canonicalAbsolute));
            }
        }

        await database.SaveChangesAsync(cancellationToken);

        // Remove emptied .incoming/<downloadId>/ staging folders (torrent leftovers: samples, .nfo, extras).
        foreach (var staging in stagingToClean)
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

    private async Task<string> BuildLibraryPathAsync(
        Catalog catalog, MediaItem item, string extension, string? edition, CancellationToken cancellationToken)
    {
        if (item.Kind == MediaKind.Episode)
        {
            var series = item.SeriesId is { } seriesId
                ? await database.MediaItems.FirstOrDefaultAsync(media => media.Id == seriesId, cancellationToken)
                : null;

            // Fall back to the episode's own title if the series row is missing.
            series ??= item;
            return LibraryNaming.ForEpisode(series, item, extension, edition);
        }

        return LibraryNaming.ForMovie(catalog, item, extension, edition);
    }
}
