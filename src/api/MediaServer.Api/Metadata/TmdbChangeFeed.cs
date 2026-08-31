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
    /// A ceiling on paging. The lists are bounded in practice, but an unbounded loop against a remote
    /// list is the kind of thing that turns a provider hiccup into a night of requests.
    /// </summary>
    private const int MaxPages = 200;

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

        // TMDb takes dates, not instants, and treats them as UTC days. A part-day window therefore has to
        // round outward: asking for the day either end is in, rather than dropping the edges.
        var start = since.UtcDateTime.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = until.UtcDateTime.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var document = await TmdbRequest.GetAsync(
                httpClientFactory, settings, logger,
                $"{path}?start_date={start}&end_date={end}&page={page}", cancellationToken);
            if (document is null)
            {
                // A failed page makes the whole answer unreliable: reporting the ids gathered so far as
                // "everything that changed" would silently skip the rest.
                logger.LogWarning("TMDb change list {Path} failed at page {Page}; skipping this refresh.", path, page);
                return null;
            }

            if (!document.RootElement.TryGetProperty("results", out var results))
            {
                return null;
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
                return ids;
            }

            if (page == MaxPages)
            {
                logger.LogWarning(
                    "TMDb change list {Path} has more than {MaxPages} pages for {Start}..{End}; refreshing what was read.",
                    path, MaxPages, start, end);
            }
        }

        return ids;
    }
}
