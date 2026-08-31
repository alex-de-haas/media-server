using System.Globalization;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>
/// TMDb's change lists (<c>/movie/changes</c>, <c>/tv/changes</c>): every title the provider edited in a
/// date range, which is how a nightly refresh can visit ten titles instead of a thousand.
/// </summary>
/// <remarks>
/// The lists are global — every film TMDb touched, not just the ones held here — so a day is a few
/// thousand ids over tens of pages. That is still far cheaper than asking after each library title in
/// turn, and it is the only shape the provider offers.
/// </remarks>
public sealed class TmdbChangeFeed(
    IHttpClientFactory httpClientFactory,
    MediaServerSettings settings,
    ILogger<TmdbChangeFeed> logger)
    : IMetadataChangeFeed
{
    /// <summary>
    /// A ceiling on paging, per day. The lists run to tens of pages for a day, so this is far above
    /// anything real — it is there because an unbounded loop against a remote list is the kind of thing
    /// that turns a provider hiccup into a night of requests. Hitting it means the day could not be read
    /// in full, which is reported as no answer rather than as a short one.
    /// </summary>
    private const int MaxPagesPerDay = 200;

    public string Key => "tmdb";

    /// <summary>TMDb answers at most 14 days at a time, and keeps no more than that.</summary>
    public TimeSpan MaxWindow => TimeSpan.FromDays(14);

    public async Task<IReadOnlyCollection<string>?> GetChangedAsync(
        MediaKind kind, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var path = kind switch
        {
            MediaKind.Movie => "movie/changes",
            MediaKind.Series => "tv/changes",
            _ => null,
        };
        if (path is null)
        {
            return [];
        }

        // TMDb takes dates, not instants, and treats them as UTC days, so the window is walked a day at a
        // time. Asking for the whole span in one query would work, but its paging grows with the span:
        // a fortnight's catch-up would run to hundreds of pages, and any cap on those turns into titles
        // silently skipped. A day is the unit the provider actually answers in.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var day = since.UtcDateTime.Date; day <= until.UtcDateTime.Date; day = day.AddDays(1))
        {
            var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (await ReadDayAsync(path, date, ids, cancellationToken) is false)
            {
                return null;
            }
        }

        return ids;
    }

    /// <summary>
    /// Adds one day's changed ids to <paramref name="ids"/>. False when the day could not be read in
    /// full — a failed request, an unexpected shape, or more pages than the cap allows. Reporting a
    /// short answer as a complete one is the one thing this must not do: the caller advances its sync
    /// marker on the strength of it, and would step over whatever went unread for good.
    /// </summary>
    private async Task<bool> ReadDayAsync(
        string path, string date, HashSet<string> ids, CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxPagesPerDay; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var document = await TmdbRequest.GetAsync(
                httpClientFactory, settings, logger,
                $"{path}?start_date={date}&end_date={date}&page={page}", cancellationToken);
            if (document is null)
            {
                logger.LogWarning("TMDb change list {Path} failed at page {Page} of {Date}; skipping this refresh.", path, page, date);
                return false;
            }

            if (!document.RootElement.TryGetProperty("results", out var results))
            {
                logger.LogWarning("TMDb change list {Path} answered without results for {Date}.", path, date);
                return false;
            }

            foreach (var entry in results.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out var id) && id.TryGetInt64(out var value))
                {
                    ids.Add(value.ToString(CultureInfo.InvariantCulture));
                }
            }

            var totalPages = document.RootElement.TryGetProperty("total_pages", out var total) && total.TryGetInt32(out var count)
                ? count
                : page;
            if (page >= totalPages)
            {
                return true;
            }
        }

        logger.LogWarning(
            "TMDb change list {Path} has more than {MaxPages} pages for {Date}; skipping this refresh rather than reading part of it.",
            path, MaxPagesPerDay, date);
        return false;
    }
}
