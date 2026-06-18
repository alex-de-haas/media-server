using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Media;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Organizer;

public sealed class OrganizerService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IHardLinker hardLinker,
    ILogger<OrganizerService> logger)
    : IOrganizer
{
    public async Task<IReadOnlyList<OrganizedFile>> OrganizeAsync(
        Download download, IReadOnlyList<SourceFile> sourceFiles, Catalog catalog, CancellationToken cancellationToken)
    {
        var paths = CatalogPaths.For(catalog);
        paths.EnsureCreated();

        var organized = new List<OrganizedFile>();

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

            var extension = Path.GetExtension(sourceFile.RelativePath);
            var libraryRelative = await BuildLibraryPathAsync(catalog, item, extension, cancellationToken);

            if (!sandbox.TryResolve(catalog, libraryRelative, out var libraryAbsolute))
            {
                logger.LogWarning("Refusing to organize outside catalog root: {Path}", libraryRelative);
                continue;
            }

            var sourceAbsolute = Path.Combine(paths.FilesDir, sourceFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourceAbsolute))
            {
                logger.LogWarning("Source file missing for organize: {Path}", sourceAbsolute);
                continue;
            }

            CreateOrReplaceHardlink(sourceAbsolute, libraryAbsolute);

            item.LibraryPath = libraryRelative;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            organized.Add(new OrganizedFile(sourceFile.Id, item.Id, libraryRelative, libraryAbsolute));
        }

        await database.SaveChangesAsync(cancellationToken);
        return organized;
    }

    public async Task UnlinkSeedCopyAsync(Download download, CancellationToken cancellationToken)
    {
        var sourceFiles = await database.SourceFiles
            .Where(file => file.DownloadId == download.Id)
            .ToListAsync(cancellationToken);

        foreach (var sourceFile in sourceFiles)
        {
            var absolute = Path.Combine(download.SavePath, sourceFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(absolute))
                {
                    File.Delete(absolute);
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Failed to unlink seed copy {Path}", absolute);
            }
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

    private void CreateOrReplaceHardlink(string sourceAbsolute, string libraryAbsolute)
    {
        if (File.Exists(libraryAbsolute))
        {
            // Idempotent re-run: rebuild the link so it points at the current source inode.
            File.Delete(libraryAbsolute);
        }

        hardLinker.Create(sourceAbsolute, libraryAbsolute);
    }
}
