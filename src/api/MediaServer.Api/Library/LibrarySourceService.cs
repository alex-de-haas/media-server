using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Admin edits to a published item's sources: which version plays by default (clients treat the first
/// <c>MediaSource</c> as the default, so this drives ordering — see <see cref="MediaSourceOrdering"/>), and
/// the per-source version label shown in client pickers. Neither touches files on disk.
/// </summary>
public sealed class LibrarySourceService(MediaServerDbContext database)
{
    /// <summary>Pins the source a player defaults to, or clears the preference when <paramref name="sourceId"/>
    /// is null. The source must belong to the item. Returns false if the item or source is unknown.</summary>
    public async Task<bool> SetDefaultSourceAsync(Guid itemId, Guid? sourceId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
        {
            return false;
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

    /// <summary>Renames the version label of a single source — the name a client shows in its version picker —
    /// or clears it when null/blank (the client then falls back to the item title). Does not rename the file
    /// on disk. Returns false if the source is unknown.</summary>
    public async Task<bool> SetVersionAsync(Guid sourceId, string? versionName, CancellationToken cancellationToken)
    {
        var source = await database.MediaSources.FirstOrDefaultAsync(candidate => candidate.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return false;
        }

        var trimmed = versionName?.Trim();
        source.VersionName = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>Pins (or clears, when null) the default source for a published item.</summary>
public sealed record SetDefaultSourceRequest(Guid? SourceId);

/// <summary>Renames (or clears, when null/blank) a source's version label.</summary>
public sealed record SetVersionRequest(string? VersionName);
