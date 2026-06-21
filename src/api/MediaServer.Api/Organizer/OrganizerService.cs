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

        foreach (var sourceFile in sourceFiles)
        {
            if (sourceFile.MediaItemId is not { } mediaItemId)
            {
                continue; // Unassigned files cannot be organized until matched.
            }

            if (!MediaFormats.IsPlayableMedia(sourceFile.RelativePath, sourceFile.SizeBytes))
            {
                continue;
            }

            var item = await database.MediaItems.FirstOrDefaultAsync(media => media.Id == mediaItemId, cancellationToken);
            if (item is null)
            {
                continue;
            }

            if (!sandbox.TryResolve(catalog, sourceFile.RelativePath, out var sourceAbsolute))
            {
                logger.LogWarning("Refusing to organize unresolved source path {Path}", sourceFile.RelativePath);
                continue;
            }

            var extension = Path.GetExtension(sourceFile.RelativePath);
            var canonicalRelative = await BuildLibraryPathAsync(catalog, item, extension, cancellationToken);

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
            sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
            item.LibraryPath = canonicalRelative;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            organized.Add(new OrganizedFile(sourceFile.Id, item.Id, canonicalRelative, canonicalAbsolute));
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

    private async Task<string> BuildLibraryPathAsync(Catalog catalog, MediaItem item, string extension, CancellationToken cancellationToken)
    {
        if (item.Kind == MediaKind.Episode)
        {
            var series = item.SeriesId is { } seriesId
                ? await database.MediaItems.FirstOrDefaultAsync(media => media.Id == seriesId, cancellationToken)
                : null;

            // Fall back to the episode's own title if the series row is missing.
            series ??= item;
            return LibraryNaming.ForEpisode(series, item, extension);
        }

        return LibraryNaming.ForMovie(catalog, item, extension);
    }
}
