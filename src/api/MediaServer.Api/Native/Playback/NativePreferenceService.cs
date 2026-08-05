using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native.Playback;

/// <summary>
/// Reads and writes track preferences. Kept out of the endpoints so the scope rule — a title's own
/// override, else the user's default — is stated once.
/// </summary>
public sealed class NativePreferenceService(MediaServerDbContext database)
{
    public async Task<IReadOnlyList<NativePreferenceDto>> ListAsync(int appUserId, CancellationToken cancellationToken) =>
        await database.PlaybackPreferences.AsNoTracking()
            .Where(row => row.AppUserId == appUserId)
            .OrderBy(row => row.MediaItemId)
            .Select(row => new NativePreferenceDto(
                row.MediaItemId,
                row.AudioLanguage,
                row.SubtitleLanguage,
                row.SubtitlesForcedOnly,
                row.PreferOriginalAudio))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The preference that applies to an item: its own override when one exists, otherwise the user's
    /// default. For an episode the caller passes the series id, so a choice made on one episode carries
    /// to the next.
    /// </summary>
    public async Task<PlaybackPreference?> ResolveAsync(
        int appUserId, Guid? scopeId, CancellationToken cancellationToken)
    {
        var rows = await database.PlaybackPreferences.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && (row.MediaItemId == null || row.MediaItemId == scopeId))
            .ToListAsync(cancellationToken);

        return rows.FirstOrDefault(row => row.MediaItemId == scopeId && scopeId is not null)
            ?? rows.FirstOrDefault(row => row.MediaItemId == null);
    }

    /// <summary>Upserts one scope. Writing through the change tracker is what puts it in the sync feed.</summary>
    public async Task<NativePreferenceDto> SetAsync(
        int appUserId, NativePreferenceDto requested, CancellationToken cancellationToken)
    {
        var row = await database.PlaybackPreferences
            .FirstOrDefaultAsync(
                candidate => candidate.AppUserId == appUserId && candidate.MediaItemId == requested.MediaItemId,
                cancellationToken);

        if (row is null)
        {
            row = new PlaybackPreference
            {
                Id = Guid.NewGuid(),
                AppUserId = appUserId,
                MediaItemId = requested.MediaItemId,
            };
            database.PlaybackPreferences.Add(row);
        }

        row.AudioLanguage = Normalize(requested.AudioLanguage);
        row.SubtitleLanguage = Normalize(requested.SubtitleLanguage);
        row.SubtitlesForcedOnly = requested.SubtitlesForcedOnly;
        row.PreferOriginalAudio = requested.PreferOriginalAudio;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await database.SaveChangesAsync(cancellationToken);

        return requested with
        {
            AudioLanguage = row.AudioLanguage,
            SubtitleLanguage = row.SubtitleLanguage,
        };
    }

    /// <summary>Clears one scope. Clearing a title's override falls back to the user's default.</summary>
    public async Task<bool> ClearAsync(int appUserId, Guid? scopeId, CancellationToken cancellationToken)
    {
        var row = await database.PlaybackPreferences
            .FirstOrDefaultAsync(
                candidate => candidate.AppUserId == appUserId && candidate.MediaItemId == scopeId,
                cancellationToken);

        if (row is null)
        {
            return false;
        }

        database.PlaybackPreferences.Remove(row);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Normalize(string? language) =>
        string.IsNullOrWhiteSpace(language) ? null : language.Trim();
}

/// <summary>
/// A preference over one scope. <c>MediaItemId</c> null is the user's default; otherwise the movie or
/// series it overrides it for.
/// </summary>
public sealed record NativePreferenceDto(
    Guid? MediaItemId,
    string? AudioLanguage,
    string? SubtitleLanguage,
    bool SubtitlesForcedOnly,
    bool PreferOriginalAudio);
