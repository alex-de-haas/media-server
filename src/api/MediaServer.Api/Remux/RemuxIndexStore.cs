using Microsoft.Extensions.Logging;

namespace MediaServer.Api.Remux;

/// <summary>
/// Keeps built indexes on disk, one file per media source, under the app's data directory.
///
/// They live beside the database rather than in it. An index is <em>derived</em> data: large next to a row,
/// rebuildable from the source at any time, and of no interest to a backup — three properties that argue
/// against a table and for a file that can simply be deleted.
///
/// Nothing here decides when to build one; see <see cref="RemuxIndexService"/>.
/// </summary>
public sealed class RemuxIndexStore(string appDataDirectory, ILogger<RemuxIndexStore> logger)
{
    private readonly string _directory = Path.Combine(appDataDirectory, "remux-index");

    internal string PathFor(Guid mediaSourceId) => Path.Combine(_directory, $"{mediaSourceId:N}.idx");

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
