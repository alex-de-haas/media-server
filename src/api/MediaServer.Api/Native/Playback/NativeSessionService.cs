using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native.Playback;

/// <summary>
/// Playback reporting for native clients.
///
/// It writes through <see cref="UserDataService"/> — the same path the Jellyfin surface uses — and
/// nothing else. A second writer is how the watched threshold, the resume rules, the season and series
/// aggregates, <c>PlaybackHistoryEntries</c> and the Trakt outbox would start disagreeing depending on
/// which client played the file. See <c>docs/features/native-playback/plan.md</c>.
/// </summary>
public sealed class NativeSessionService(MediaServerDbContext database, UserDataService userData)
{
    /// <summary>
    /// Opens a session and returns its id. The id is what keeps one viewing from counting several
    /// times when a viewer rewinds past the watched threshold and watches forward again, so the server
    /// mints it rather than trusting a client to be unique.
    /// </summary>
    public async Task<string?> StartAsync(
        int appUserId, NativeSessionStart start, CancellationToken cancellationToken)
    {
        var publicId = await PublicIdAsync(start.ItemId, cancellationToken);
        if (publicId is null)
        {
            return null;
        }

        var playSessionId = Guid.NewGuid().ToString("N");
        await userData.ReportPlaybackAsync(
            appUserId, publicId, Math.Max(0, start.PositionTicks), isStopped: false, playSessionId,
            diagnostics: null, cancellationToken);

        return playSessionId;
    }

    /// <summary>A progress or stop report against an open session.</summary>
    public async Task<bool> ReportAsync(
        int appUserId, NativeSessionReport report, bool isStopped, CancellationToken cancellationToken)
    {
        var publicId = await PublicIdAsync(report.ItemId, cancellationToken);
        if (publicId is null)
        {
            return false;
        }

        await userData.ReportPlaybackAsync(
            appUserId, publicId, Math.Max(0, report.PositionTicks), isStopped, report.PlaySessionId,
            diagnostics: null, cancellationToken);

        return true;
    }

    /// <summary>
    /// Native clients address items by their internal id; the reporting path is keyed by the public
    /// one. Unpublished and tombstoned items resolve to nothing, as everywhere else on this surface.
    /// </summary>
    private async Task<string?> PublicIdAsync(Guid itemId, CancellationToken cancellationToken) =>
        await database.MediaItems.AsNoTracking()
            .Where(item => item.Id == itemId && item.PublicId != null && item.RemovedAt == null)
            .Select(item => item.PublicId)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// What a client is about to play. The media source and the chosen tracks are carried because a
/// viewing is of one edition with one dub — not merely of a title — even though what the server does
/// with a report today needs only the item and the position.
/// </summary>
public sealed record NativeSessionStart(
    Guid ItemId,
    Guid? MediaSourceId,
    Guid? AudioStreamId,
    Guid? SubtitleStreamId,
    string? DeviceId,
    long PositionTicks = 0);

public sealed record NativeSessionReport(Guid ItemId, string? PlaySessionId, long PositionTicks);

public sealed record NativeSessionStarted(string PlaySessionId);
