using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Organizer;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Admin edits to a published item's sources: which version plays by default (clients treat the first
/// <c>MediaSource</c> as the default, so this drives ordering — see <see cref="MediaSourceOrdering"/>), and
/// the per-source version — the <c> - {edition}</c> suffix on the filename, which doubles as the label shown
/// in client pickers. Renaming the version actually renames the file on disk (the <c>Title (Year)</c> stem is
/// locked to the item's metadata) and keeps the stored label in sync.
/// </summary>
public sealed class LibrarySourceService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    ILogger<LibrarySourceService> logger)
{
    // Characters Sanitize would strip from a filename. Rejected up front so the on-disk suffix always
    // matches what the operator typed instead of being silently mangled.
    private static readonly char[] InvalidNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    // Path equality follows the filesystem: case-insensitive on Windows and default macOS, ordinal elsewhere.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Pins the source a player defaults to, or clears the preference when <paramref name="sourceId"/>
    /// is null. The source must belong to the item. Returns false if the item or source is unknown.</summary>
    public async Task<bool> SetDefaultSourceAsync(Guid itemId, Guid? sourceId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        if (item.DefaultSourceId == sourceId)
        {
            return true; // No change — skip the write and the UpdatedAt bump.
        }

        if (sourceId is { } id &&
            !await database.MediaSources.AnyAsync(source => source.Id == id && source.MediaItemId == itemId, cancellationToken))
        {
            return false;
        }

        item.DefaultSourceId = sourceId;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Renames a movie source's version: rewrites the <c> - {edition}</c> suffix of its filename (clearing it
    /// when <paramref name="versionName"/> is null/blank → bare <c>Title (Year).ext</c>), renaming the file on
    /// disk and syncing the stored label + the originating <see cref="SourceFile"/>. The <c>Title (Year)</c>
    /// stem is rebuilt from the item's metadata, so it can't be edited here. Versions only exist on movies.
    /// </summary>
    public async Task<RenameVersionResult> RenameVersionAsync(Guid sourceId, string? versionName, CancellationToken cancellationToken)
    {
        var source = await database.MediaSources
            .Include(candidate => candidate.MediaItem)
            .FirstOrDefaultAsync(candidate => candidate.Id == sourceId, cancellationToken);
        if (source?.MediaItem is null)
        {
            return RenameVersionResult.NotFound;
        }

        var item = source.MediaItem;
        if (item.Kind != MediaKind.Movie)
        {
            return RenameVersionResult.Unsupported;
        }

        if (!TryNormalizeEdition(versionName, out var edition, out var validationError))
        {
            return RenameVersionResult.Invalid(validationError);
        }

        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        if (catalog is null)
        {
            return RenameVersionResult.NotFound;
        }

        var oldRelative = source.Path;
        var newRelative = LibraryNaming.ForMovie(catalog, item, Path.GetExtension(oldRelative), edition);

        // No path change: just reconcile a drifted stored label (legacy data) and return.
        if (string.Equals(newRelative, oldRelative, StringComparison.Ordinal))
        {
            if (source.VersionName != edition)
            {
                source.VersionName = edition;
                await database.SaveChangesAsync(cancellationToken);
            }

            return RenameVersionResult.Ok;
        }

        // Collision: another version already owns the target path, or a stray file sits there.
        var pathTaken = await database.MediaSources.AnyAsync(
            other => other.Id != source.Id && other.MediaItem!.CatalogId == catalog.Id && other.Path == newRelative,
            cancellationToken);
        if (pathTaken)
        {
            return RenameVersionResult.Conflict("Another version already uses this name.");
        }

        if (!sandbox.TryResolve(catalog, oldRelative, out var oldAbsolute) ||
            !sandbox.TryResolve(catalog, newRelative, out var newAbsolute))
        {
            return RenameVersionResult.Conflict("The file path couldn't be resolved inside the catalog.");
        }

        if (!File.Exists(oldAbsolute))
        {
            return RenameVersionResult.MissingFile;
        }

        // A case-only edit (e.g. "1080p" -> "1080P") maps to the same file on a case-insensitive filesystem:
        // the target "exists" because it *is* the source, and File.Move would fail. Skip the move and just
        // restamp the stored path/label; on a case-sensitive filesystem the rename happens for real.
        var sameFileOnDisk = string.Equals(oldAbsolute, newAbsolute, PathComparison);

        if (!sameFileOnDisk && File.Exists(newAbsolute))
        {
            return RenameVersionResult.Conflict("Another version already uses this name.");
        }

        if (!sameFileOnDisk)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newAbsolute)!);
                File.Move(oldAbsolute, newAbsolute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to rename source file {Old} -> {New}", oldAbsolute, newAbsolute);
                return RenameVersionResult.Conflict("Couldn't rename the file on disk.");
            }
        }

        try
        {
            // One SaveChanges = one transaction: the file path, label, originating source file, and the item's
            // primary-version pointer all move together.
            source.Path = newRelative;
            source.VersionName = edition;

            if (source.SourceFileId is { } sourceFileId)
            {
                var sourceFile = await database.SourceFiles.FirstOrDefaultAsync(file => file.Id == sourceFileId, cancellationToken);
                if (sourceFile is not null)
                {
                    sourceFile.RelativePath = newRelative;
                    sourceFile.Edition = edition;
                    sourceFile.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            if (string.Equals(item.LibraryPath, oldRelative, StringComparison.Ordinal))
            {
                item.LibraryPath = newRelative;
            }

            item.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Roll the file back so disk and DB don't diverge (only when we actually moved it).
            if (!sameFileOnDisk)
            {
                try
                {
                    File.Move(newAbsolute, oldAbsolute);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(rollback, "Failed to roll back rename {New} -> {Old} after a DB error", newAbsolute, oldAbsolute);
                }
            }

            throw;
        }

        return RenameVersionResult.Ok;
    }

    // Trims, treats blank as "clear the suffix", rejects filename-unsafe characters (explicit error rather
    // than silent sanitize), and collapses to the exact form LibraryNaming.Sanitize would keep so the stored
    // label equals the on-disk suffix.
    private static bool TryNormalizeEdition(string? input, out string? edition, out string error)
    {
        edition = null;
        error = string.Empty;

        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true; // Cleared → no suffix.
        }

        if (trimmed.Any(character => InvalidNameChars.Contains(character) || char.IsControl(character)))
        {
            error = "The version name can't contain any of: / \\ : * ? \" < > |";
            return false;
        }

        var collapsed = string.Join(' ', trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        if (!collapsed.Any(char.IsLetterOrDigit))
        {
            error = "The version name must contain a letter or number.";
            return false;
        }

        edition = collapsed;
        return true;
    }
}

/// <summary>Pins (or clears, when null) the default source for a published item.</summary>
public sealed record SetDefaultSourceRequest(Guid? SourceId);

/// <summary>Renames (or clears, when null/blank) a source's version — and the file on disk.</summary>
public sealed record SetVersionRequest(string? VersionName);

/// <summary>Outcome of <see cref="LibrarySourceService.RenameVersionAsync"/>, mapped to HTTP by the endpoint.</summary>
public readonly record struct RenameVersionResult(RenameVersionResult.Kind Status, string? Error = null)
{
    public enum Kind { Ok, NotFound, Unsupported, InvalidName, Conflict, MissingFile }

    public static readonly RenameVersionResult Ok = new(Kind.Ok);
    public static readonly RenameVersionResult NotFound = new(Kind.NotFound);
    public static readonly RenameVersionResult Unsupported = new(Kind.Unsupported, "Only a movie version can be renamed.");
    public static readonly RenameVersionResult MissingFile = new(Kind.MissingFile, "The media file is missing on disk.");

    public static RenameVersionResult Invalid(string error) => new(Kind.InvalidName, error);
    public static RenameVersionResult Conflict(string error) => new(Kind.Conflict, error);
}
