using Microsoft.Extensions.Logging;

namespace MediaServer.Api.Remux;

/// <summary>
/// Keeps built indexes on disk, one file per media source, under the app's cache directory.
///
/// They live beside the database rather than in it. An index is <em>derived</em> data: large next to a row,
/// rebuildable from the source at any time, and of no interest to a backup — three properties that argue
/// against a table and for a file that can simply be deleted. The cache directory is where Hosty gives
/// those properties a home: it survives restarts and updates but is never backed up, so a gigabyte of
/// indexes no longer rides along in every snapshot. Under a Core that predates the cache contract the
/// caller passes the data directory instead, which is simply the old layout.
///
/// Nothing here decides when to build one; see <see cref="RemuxIndexService"/>.
/// </summary>
public sealed class RemuxIndexStore(string appCacheDirectory, ILogger<RemuxIndexStore> logger)
{
    private readonly string _directory = Path.Combine(appCacheDirectory, "remux-index");

    internal string PathFor(Guid mediaSourceId) => Path.Combine(_directory, $"{mediaSourceId:N}.idx");

    /// <summary>
    /// One-time move of index files from the pre-cache location (<c>{data}/remux-index</c>) into this
    /// store's directory, so an upgrade does not throw away hours of background walking. Idempotent:
    /// a crash mid-way leaves files that are simply moved on the next start, a file already present at
    /// the destination wins (the legacy copy is deleted), and once the legacy directory is empty it is
    /// removed and every later start returns immediately. A no-op when the store still lives under the
    /// data directory — the old-Core fallback — because the two paths are then the same directory.
    /// </summary>
    public void MigrateFrom(string appDataDirectory)
    {
        var legacy = Path.Combine(appDataDirectory, "remux-index");
        if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(_directory), StringComparison.Ordinal)
            || !Directory.Exists(legacy))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var moved = 0;
        foreach (var source in Directory.EnumerateFiles(legacy))
        {
            try
            {
                if (!source.EndsWith(".idx", StringComparison.Ordinal))
                {
                    // Only the store writes here, so anything else is a `.partial` some interrupted
                    // build left behind — garbage at either location.
                    File.Delete(source);
                    continue;
                }

                var destination = Path.Combine(_directory, Path.GetFileName(source));
                if (File.Exists(destination))
                {
                    File.Delete(source);
                    continue;
                }

                // File.Move degrades to copy-and-delete when data and cache are separate docker binds,
                // and a kill mid-copy would leave a truncated destination for the next start's
                // destination-wins check to keep over the intact source. Staged through a sibling
                // temp name, a file only ever appears at its final name via a same-volume rename.
                var temporary = destination + ".partial";
                File.Move(source, temporary, overwrite: true);
                File.Move(temporary, destination, overwrite: true);
                moved++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Skip and carry on: whatever could not move stays in the legacy directory (which then
                // survives below) and gets another chance on the next start.
                logger.LogWarning(exception, "Could not migrate remux index {Path}", source);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
            }
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "Could not remove legacy remux index directory {Path}", legacy);
        }

        if (moved > 0)
        {
            logger.LogInformation("Migrated {Count} remux index(es) from {Legacy} to {Directory}", moved, legacy, _directory);
        }
    }

    /// <summary>
    /// Loads the index for a source, or null when there is none, it does not match the file it was built
    /// from, or it cannot be read. Every one of those is answered the same way — rebuild — so they are not
    /// distinguished here.
    /// </summary>
    internal MatroskaIndex? Load(Guid mediaSourceId, string sourcePath)
    {
        var path = PathFor(mediaSourceId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (RemuxIndexFormat.Read(stream) is not { } read)
            {
                return null;
            }

            return read.Stamp.Matches(new FileInfo(sourcePath)) ? read.Index : null;
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "Could not read remux index {Path}", path);
            return null;
        }
    }

    /// <summary>True when a usable index already exists, without paying to decode the sample tables.</summary>
    internal bool IsCurrent(Guid mediaSourceId, string sourcePath)
    {
        var path = PathFor(mediaSourceId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return RemuxIndexFormat.ReadStamp(stream) is { } stamp && stamp.Matches(new FileInfo(sourcePath));
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal void Save(Guid mediaSourceId, string sourcePath, MatroskaIndex index)
    {
        Directory.CreateDirectory(_directory);
        var file = new FileInfo(sourcePath);
        var stamp = new RemuxIndexFormat.Stamp(file.Length, file.LastWriteTimeUtc);

        // Written aside and moved into place, so a build that is interrupted leaves no half-file for the
        // next read to mistake for an index.
        var path = PathFor(mediaSourceId);
        var temporary = path + ".partial";
        using (var stream = File.Create(temporary))
        {
            RemuxIndexFormat.Write(stream, index, stamp);
        }

        File.Move(temporary, path, overwrite: true);
    }

    internal void Delete(Guid mediaSourceId)
    {
        foreach (var path in new[] { PathFor(mediaSourceId), PathFor(mediaSourceId) + ".partial" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Could not delete remux index {Path}", path);
            }
        }
    }

    /// <summary>Every media source id that currently has an index file, for reconciling against the library.</summary>
    internal IEnumerable<Guid> Stored()
    {
        if (!Directory.Exists(_directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.idx"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
            {
                yield return id;
            }
        }
    }
}
