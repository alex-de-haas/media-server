using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Phase 0 observation instrument for the watched-history work: records one structured line per
/// playback-state request so we can see exactly what a client like Infuse sends, and what it did to
/// local state, without changing any playback behavior.
/// </summary>
/// <remarks>
/// Gated on the operator setting <c>PLAYBACK_DIAGNOSTICS</c> rather than the ASP.NET environment.
/// The original design assumed a Development-only log, but the app ships and runs under the
/// <c>docker</c> runtime, where that log is never written — so the observation matrix could not be
/// captured on a real install. An operator toggle works in every runtime profile.
///
/// Deliberately excluded from every record: tokens and headers, request bodies verbatim, media
/// titles, and filesystem paths. What remains is opaque ids, numbers and flags — enough to answer
/// the Phase 0 questions, and nothing that turns the log into a second copy of the library.
/// </remarks>
public sealed class PlaybackDiagnostics(PlaybackDiagnosticsWriter? writer)
{
    private PlaybackDiagnosticRecord? pending;

    /// <summary>True when the operator enabled diagnostics; callers may skip the work entirely.</summary>
    public bool Enabled => writer is not null;

    /// <summary>
    /// Opens a record from the request side. Route kind is supplied by the endpoint rather than
    /// derived from the path, so the two Jellyfin route generations collapse to one kind.
    /// </summary>
    public void BeginRequest(
        string routeKind,
        int? appUserId,
        string? itemId,
        long? positionTicks,
        string? playSessionId,
        string? mediaSourceId,
        bool? isPaused,
        bool isStopped,
        DateTimeOffset? datePlayed,
        bool datePlayedSupplied)
    {
        if (writer is null)
        {
            return;
        }

        pending = new PlaybackDiagnosticRecord
        {
            At = DateTimeOffset.UtcNow,
            Route = routeKind,
            AppUserId = appUserId,
            ItemId = itemId,
            PositionTicks = positionTicks,
            PlaySessionId = playSessionId,
            MediaSourceId = mediaSourceId,
            IsPaused = isPaused,
            IsStopped = isStopped,
            DatePlayed = datePlayed,
            DatePlayedSupplied = datePlayedSupplied,
        };
    }

    /// <summary>
    /// Records that the operation fanned out to descendant episodes instead of touching one row —
    /// a season or series mark. Reported instead of, not alongside, <see cref="ObserveState"/>:
    /// the folder has no row of its own to compare, and reporting its absent row as
    /// <c>played=false, playCount=0</c> would put a plausible-looking lie in the trace.
    /// </summary>
    public void ObserveFanOut(int affectedItems)
    {
        if (pending is null)
        {
            return;
        }

        pending.AffectedItems = affectedItems;
    }

    /// <summary>
    /// Fills in the state half from <see cref="Library.UserDataService"/>: what the row looked like
    /// before the operation and after it. Only meaningful for a leaf item (movie, episode, video);
    /// folder marks report <see cref="ObserveFanOut"/> instead. Ignored when no record is open, so
    /// the service stays usable from surfaces that never begin one (the web UI).
    /// </summary>
    public void ObserveState(
        long runtimeTicks,
        bool playedBefore,
        bool playedAfter,
        int playCountBefore,
        int playCountAfter,
        long positionBefore,
        long positionAfter)
    {
        if (pending is null)
        {
            return;
        }

        pending.RuntimeTicks = runtimeTicks;
        pending.PlayedBefore = playedBefore;
        pending.PlayedAfter = playedAfter;
        pending.PlayCountBefore = playCountBefore;
        pending.PlayCountAfter = playCountAfter;
        pending.PositionBefore = positionBefore;
        pending.PositionAfter = positionAfter;
        pending.PositionFraction = runtimeTicks > 0 && pending.PositionTicks is { } position
            ? Math.Round((double)position / runtimeTicks, 4)
            : null;
    }

    /// <summary>
    /// Writes the open record, if any. Never throws: a diagnostic must not be able to fail a playback
    /// request, which is the whole point of observing without changing behavior.
    /// </summary>
    public async Task CompleteAsync(int statusCode, CancellationToken cancellationToken)
    {
        if (writer is null || pending is null)
        {
            return;
        }

        var record = pending;
        pending = null;
        record.Status = statusCode;
        await writer.WriteAsync(record, cancellationToken);
    }
}

/// <summary>
/// Serializes records as one JSON object per line and appends them under the app's log directory,
/// beside <c>requests.log</c>. Writes are serialized through a semaphore because several playback
/// reports can land concurrently.
/// </summary>
public sealed class PlaybackDiagnosticsWriter(string path, ILogger<PlaybackDiagnosticsWriter> logger) : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // Nulls are dropped so a line shows only what the client actually sent: an absent
        // `playSessionId` field means "not echoed", which is one of the questions being asked.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim mutex = new(1, 1);
    private bool failed;

    /// <summary>The file records are appended to; surfaced so startup can log where to look.</summary>
    public string Path => path;

    public async Task WriteAsync(PlaybackDiagnosticRecord record, CancellationToken cancellationToken)
    {
        // One failure is enough: a broken log path would otherwise warn on every playback report.
        if (failed)
        {
            return;
        }

        try
        {
            var line = JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine;
            await mutex.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(path, line, Encoding.UTF8, cancellationToken);
            }
            finally
            {
                mutex.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The request went away mid-write; nothing to report.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failed = true;
            logger.LogWarning(ex, "Playback diagnostics disabled: {Path} could not be written.", path);
        }
    }

    public void Dispose() => mutex.Dispose();
}

/// <summary>One observed playback-state request. Property names are the JSON field names.</summary>
public sealed class PlaybackDiagnosticRecord
{
    public DateTimeOffset At { get; set; }

    /// <summary>One of the <see cref="PlaybackRouteKinds"/> values.</summary>
    public string Route { get; set; } = string.Empty;

    public int? Status { get; set; }

    /// <summary>Internal app user id — never an email, name, or Jellyfin token.</summary>
    public int? AppUserId { get; set; }

    /// <summary>The opaque Jellyfin item id as sent by the client; no title, no path.</summary>
    public string? ItemId { get; set; }

    public long? PositionTicks { get; set; }

    public long? RuntimeTicks { get; set; }

    /// <summary>Position as a fraction of runtime, rounded — the value the 90% policy compares.</summary>
    public double? PositionFraction { get; set; }

    /// <summary>Echoed client session id; null tells us Infuse does not return ours.</summary>
    public string? PlaySessionId { get; set; }

    public string? MediaSourceId { get; set; }

    public bool? IsPaused { get; set; }

    public bool IsStopped { get; set; }

    public DateTimeOffset? DatePlayed { get; set; }

    /// <summary>Distinguishes "client sent no DatePlayed" from "sent an unparseable one".</summary>
    public bool DatePlayedSupplied { get; set; }

    public bool? PlayedBefore { get; set; }

    public bool? PlayedAfter { get; set; }

    public int? PlayCountBefore { get; set; }

    public int? PlayCountAfter { get; set; }

    public long? PositionBefore { get; set; }

    public long? PositionAfter { get; set; }

    /// <summary>
    /// Number of descendant episodes a season/series mark applied to. Present only for folder
    /// operations, which carry no leaf before/after state.
    /// </summary>
    public int? AffectedItems { get; set; }
}

/// <summary>
/// Route classification for the observation matrix. Both Jellyfin route generations map to the same
/// kind: which URL shape the client picked is a separate question from what it meant.
/// </summary>
public static class PlaybackRouteKinds
{
    public const string Playing = "Playing";
    public const string Progress = "Progress";
    public const string Stopped = "Stopped";
    public const string PlayedItemsPost = "PlayedItemsPost";
    public const string PlayedItemsDelete = "PlayedItemsDelete";
}
