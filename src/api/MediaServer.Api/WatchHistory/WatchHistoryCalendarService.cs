using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>One completed play, as the calendar renders it.</summary>
/// <remarks>
/// Raw per-play rows, deliberately ungrouped: the browser groups them in its own time zone, so a play
/// at 00:30 lands on the right local day and daylight-saving boundaries stay correct. Grouping here
/// would bake this server's clock into the answer.
/// </remarks>
public sealed record WatchHistoryCalendarEvent(
    Guid EntryId,
    DateTimeOffset WatchedAt,
    Guid MediaItemId,
    string? PublicId,
    string Kind,
    string Title,
    /// <summary>For an episode this is the <em>series</em> poster: episodes rarely carry their own
    /// artwork, and the calendar groups them at series level anyway.</summary>
    string? PosterUrl,
    Guid? SeriesId,
    string? SeriesTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Origin);

/// <summary>One watched mark that carries no date — shown in a list, never on the grid.</summary>
public sealed record WatchHistoryUndatedEntry(
    Guid EntryId,
    Guid MediaItemId,
    string? PublicId,
    string Kind,
    string Title,
    string? PosterUrl,
    string? SeriesTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Origin);

/// <summary>
/// The undated marks themselves, plus how many exist in total. The list is capped, so the total is
/// what lets the UI admit it is showing only the most recent ones rather than quietly truncating.
/// </summary>
public sealed record WatchHistoryUndatedPage(IReadOnlyList<WatchHistoryUndatedEntry> Entries, int Total);

/// <summary>How many watched marks carry no date, split by kind.</summary>
/// <remarks>
/// Per kind rather than one total: the Watched toolbar filters by Movies/Episodes, and these rows are
/// absent from <see cref="WatchHistoryCalendarResponse.Events"/> by design, so a single total could
/// never be re-filtered in the browser.
/// </remarks>
public sealed record WatchHistoryUndatedCounts(int Movies, int Episodes);

/// <summary>One page of dated history, with what the window left out.</summary>
/// <param name="UndatedTotal">
/// Plays this user has that carry no date at all — imported from a provider that reported none. They
/// can never fall inside a period, so an answer about one silently omits them unless it says so.
/// </param>
public sealed record WatchHistoryPage(
    IReadOnlyList<WatchHistoryCalendarEvent> Events,
    int Total,
    int Limit,
    int Offset,
    int UndatedTotal);

/// <summary>The calendar payload for one visible range.</summary>
public sealed record WatchHistoryCalendarResponse(
    IReadOnlyList<WatchHistoryCalendarEvent> Events,
    WatchHistoryUndatedCounts Undated,
    /// <summary>The user's most recent dated play, so an empty month can offer a jump without
    /// loading history.</summary>
    DateTimeOffset? LatestWatchedAt,
    /// <summary>
    /// True when the range held more events than one load returns. A caller that asked for a decade
    /// gets the earliest slice of it and is told the rest exists, rather than a quietly short list.
    /// </summary>
    bool Truncated = false);

/// <summary>
/// Reads one user's dated play history for a bounded range.
/// </summary>
/// <remarks>
/// Read-only and user-scoped: every query filters on the caller's <c>AppUserId</c>, so no request can
/// surface another user's viewing.
/// </remarks>
public sealed class WatchHistoryCalendarService(
    MediaServerDbContext database, MediaServerSettings settings)
{
    /// <summary>
    /// The most events one calendar load will materialise.
    /// </summary>
    /// <remarks>
    /// This replaced a 62-day cap on the *range*. That number came from the shape of a month grid —
    /// six weeks plus the adjacent-month cells — and it was doing a second job it was never sized for:
    /// being the only thing that stopped a request scanning a decade. Bounding the rows bounds the
    /// scan directly, and leaves the range free for questions that are not a calendar, like "what did
    /// I watch five years ago".
    /// </remarks>
    internal const int MaxEvents = 5_000;

    /// <summary>The most this list returns at once; it is a reminder of what else was watched, not an
    /// archive browser. The page's <see cref="WatchHistoryUndatedPage.Total"/> reports the rest.</summary>
    internal const int UndatedLimit = 200;

    /// <summary>
    /// The user's undated marks, newest first, optionally narrowed to one kind so the list and the
    /// toolbar's count answer the same question.
    /// </summary>
    public async Task<WatchHistoryUndatedPage> LoadUndatedAsync(
        int appUserId, MediaKind? kind, CancellationToken cancellationToken)
    {
        var matching = database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId && entry.WatchedAt == null);

        if (kind is { } wanted)
        {
            matching = matching.Where(entry =>
                database.MediaItems.Any(item => item.Id == entry.MediaItemId && item.Kind == wanted));
        }

        var total = await matching.CountAsync(cancellationToken);
        if (total == 0)
        {
            return new WatchHistoryUndatedPage([], 0);
        }

        var entries = await matching
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(UndatedLimit)
            .ToListAsync(cancellationToken);

        var projected = await ProjectAsync(entries, cancellationToken);
        return new WatchHistoryUndatedPage(
            [.. projected.Select(entry => new WatchHistoryUndatedEntry(
                entry.EntryId,
                entry.MediaItemId,
                entry.PublicId,
                entry.Kind,
                entry.Title,
                entry.PosterUrl,
                entry.SeriesTitle,
                entry.SeasonNumber,
                entry.EpisodeNumber,
                entry.Origin))],
            total);
    }

    public async Task<WatchHistoryCalendarResponse> LoadAsync(
        int appUserId, DateTimeOffset from, DateTimeOffset toExclusive, CancellationToken cancellationToken)
    {
        var entries = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId
                && entry.WatchedAt != null
                && entry.WatchedAt >= from
                && entry.WatchedAt < toExclusive)
            .OrderBy(entry => entry.WatchedAt)
            // One more than the cap, so a full page can be told from a complete one without counting
            // the range twice.
            .Take(MaxEvents + 1)
            .ToListAsync(cancellationToken);
        var truncated = entries.Count > MaxEvents;
        if (truncated)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        var events = entries.Count == 0
            ? []
            : await ProjectAsync(entries, cancellationToken);

        // Timeless rows never get a fabricated date, so they are counted rather than placed.
        var undated = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId && entry.WatchedAt == null)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (_, item) => item.Kind)
            .GroupBy(kind => kind)
            .Select(group => new { Kind = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var latest = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId && entry.WatchedAt != null)
            .MaxAsync(entry => (DateTimeOffset?)entry.WatchedAt, cancellationToken);

        return new WatchHistoryCalendarResponse(
            events,
            new WatchHistoryUndatedCounts(
                undated.FirstOrDefault(row => row.Kind == MediaKind.Movie)?.Count ?? 0,
                undated.FirstOrDefault(row => row.Kind == MediaKind.Episode)?.Count ?? 0),
            latest,
            truncated);
    }

    /// <summary>
    /// One page of dated history over any period, newest first — the shape a question asks in.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LoadAsync"/>, which fills a calendar grid: that one is oldest-first,
    /// returns a whole range at once, and is bounded by the grid the caller is drawing. A question is
    /// not a grid. "What did I watch yesterday" and "what did I watch five years ago" differ only in
    /// where the window sits, so the period is free and the page is what is bounded — with the total
    /// alongside, or a full page and a complete answer would look identical.
    /// </remarks>
    public async Task<WatchHistoryPage> SearchAsync(
        int appUserId,
        DateTimeOffset from,
        DateTimeOffset toExclusive,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var matching = database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId
                && entry.WatchedAt != null
                && entry.WatchedAt >= from
                && entry.WatchedAt < toExclusive);

        var total = await matching.CountAsync(cancellationToken);
        var entries = await matching
            .OrderByDescending(entry => entry.WatchedAt)
            // Id breaks ties: two plays sharing a timestamp could otherwise swap between pages, one
            // shown twice and one never.
            .ThenByDescending(entry => entry.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var undated = await database.PlaybackHistoryEntries.AsNoTracking()
            .CountAsync(entry => entry.AppUserId == appUserId && entry.WatchedAt == null, cancellationToken);

        return new WatchHistoryPage(
            entries.Count == 0 ? [] : await ProjectAsync(entries, cancellationToken),
            total,
            limit,
            offset,
            undated);
    }

    private async Task<List<WatchHistoryCalendarEvent>> ProjectAsync(
        List<PlaybackHistoryEntry> entries, CancellationToken cancellationToken)
    {
        var itemIds = entries.Select(entry => entry.MediaItemId).Distinct().ToList();
        var items = await database.MediaItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        // Episodes borrow their series' title and poster, so the series rows are fetched too.
        var seriesIds = items.Values
            .Where(item => item.Kind == MediaKind.Episode && item.SeriesId != null)
            .Select(item => item.SeriesId!.Value)
            .Distinct()
            .ToList();
        var series = seriesIds.Count == 0
            ? []
            : await database.MediaItems.AsNoTracking()
                .Where(item => seriesIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);

        var posters = await PostersAsync([.. itemIds.Concat(seriesIds).Distinct()], cancellationToken);
        var titles = await TitlesAsync([.. itemIds.Concat(seriesIds).Distinct()], cancellationToken);

        var events = new List<WatchHistoryCalendarEvent>(entries.Count);
        foreach (var entry in entries)
        {
            // A history row whose item is gone cannot be rendered; the cascade normally prevents this.
            if (!items.TryGetValue(entry.MediaItemId, out var item))
            {
                continue;
            }

            var parent = item.Kind == MediaKind.Episode && item.SeriesId is { } id
                ? series.GetValueOrDefault(id)
                : null;

            events.Add(new WatchHistoryCalendarEvent(
                entry.Id,
                // Undated rows reuse this projection for their list; the caller drops the instant.
                entry.WatchedAt ?? default,
                item.Id,
                item.PublicId,
                item.Kind.ToString(),
                titles.GetValueOrDefault(item.Id) ?? item.Title,
                parent is null ? posters.GetValueOrDefault(item.Id) : posters.GetValueOrDefault(parent.Id),
                parent?.Id,
                parent is null ? null : titles.GetValueOrDefault(parent.Id) ?? parent.Title,
                // Canonical numbering when the release was re-mapped; display numbering otherwise.
                item.IdentitySeasonNumber ?? item.ParentIndexNumber,
                item.IdentityEpisodeNumber ?? item.IndexNumber,
                entry.Origin.ToString()));
        }

        return events;
    }

    private Task<Dictionary<Guid, string>> PostersAsync(
        IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken) =>
        database.BestPosterUrlsAsync(itemIds, settings.PreferredLanguage, cancellationToken);

    /// <summary>
    /// Metadata titles win over the scanned title, matching what the rest of the library renders —
    /// including <em>which</em> localized title. An item can hold a record per language, so the
    /// preferred locale is chosen explicitly; taking whichever row the database returned last would
    /// make the calendar disagree with the item's own page.
    /// </summary>
    private async Task<Dictionary<Guid, string>> TitlesAsync(
        IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var records = new List<MetadataRecord>();
        foreach (var chunk in itemIds.Chunk(500))
        {
            records.AddRange(await database.MetadataRecords.AsNoTracking()
                .Where(record => chunk.Contains(record.MediaItemId) && record.Title != null)
                .ToListAsync(cancellationToken));
        }

        return records
            .GroupBy(record => record.MediaItemId)
            .Select(group => new { group.Key, Title = PickLanguage([.. group]).Title })
            .Where(row => !string.IsNullOrWhiteSpace(row.Title))
            .ToDictionary(row => row.Key, row => row.Title!);
    }

    private MetadataRecord PickLanguage(List<MetadataRecord> records) =>
        MetadataLanguage.Pick(records, settings.PreferredLanguage, record => record.Language);
}
