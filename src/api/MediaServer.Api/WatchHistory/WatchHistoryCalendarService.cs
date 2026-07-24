using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
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

/// <summary>The calendar payload for one visible range.</summary>
public sealed record WatchHistoryCalendarResponse(
    IReadOnlyList<WatchHistoryCalendarEvent> Events,
    WatchHistoryUndatedCounts Undated,
    /// <summary>The user's most recent dated play, so an empty month can offer a jump without
    /// loading history.</summary>
    DateTimeOffset? LatestWatchedAt);

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
    /// The widest range one request may ask for. A month grid spans at most 6 weeks, so 62 days
    /// leaves room for the adjacent-month cells while keeping the scan bounded.
    /// </summary>
    internal static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

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
            .ToListAsync(cancellationToken);

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
            latest);
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

    private async Task<Dictionary<Guid, string>> PostersAsync(
        IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var posters = new Dictionary<Guid, string>();
        foreach (var chunk in itemIds.Chunk(500))
        {
            var rows = await database.ImageAssets.AsNoTracking()
                .Where(image => chunk.Contains(image.MediaItemId) && image.ImageType == ImageType.Primary)
                .GroupBy(image => image.MediaItemId)
                .Select(group => new
                {
                    MediaItemId = group.Key,
                    Url = group.OrderBy(image => image.SortOrder).Select(image => image.RemotePath).First(),
                })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                posters[row.MediaItemId] = row.Url;
            }
        }

        return posters;
    }

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

    /// <summary>The same preference order <c>LibraryReadService</c> applies: exact locale, then the
    /// language prefix, then whatever exists.</summary>
    private MetadataRecord PickLanguage(List<MetadataRecord> records)
    {
        var preferred = settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";
        var prefix = preferred.Length >= 2 ? preferred[..2] : preferred;
        return records.FirstOrDefault(record => string.Equals(record.Language, preferred, StringComparison.OrdinalIgnoreCase))
            ?? records.FirstOrDefault(record => record.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?? records[0];
    }
}
