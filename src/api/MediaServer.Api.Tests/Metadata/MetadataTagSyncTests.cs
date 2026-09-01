using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// The searchable projection of genres and keywords. Genres are a converted JSON list and keywords
/// only exist inside the raw provider payload, so neither can be filtered on in SQL — these rows are
/// the only reason a genre or "about" search can be answered, and a stale row is a wrong answer that
/// looks right.
/// </summary>
public sealed class MetadataTagSyncTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly MetadataTagSync _sync;

    public MetadataTagSyncTests()
    {
        _context = _db.Create();
        _sync = new MetadataTagSync(_context, NullLogger<MetadataTagSync>.Instance);
    }

    [Fact]
    public async Task Genres_and_keywords_both_become_rows()
    {
        var itemId = AddItem();
        AddRecord(itemId, ["Action", "Comedy"], keywords: ["aircraft hijacking"]);
        await _context.SaveChangesAsync();

        await _sync.SyncAsync(itemId, MediaKind.Movie, CancellationToken.None);

        Assert.Equal(
            ["Action", "Comedy"],
            await ValuesAsync(MetadataTagKind.Genre));
        Assert.Equal(["aircraft hijacking"], await ValuesAsync(MetadataTagKind.Keyword));
    }

    [Fact]
    public async Task A_genre_dropped_by_a_refetch_is_dropped_from_the_index()
    {
        // Rebuilt rather than merged, and this is why: a title re-identified as a drama must stop
        // answering a search for comedies. A merge would leave the old row behind, and the search would
        // keep returning a film whose metadata no longer says any such thing.
        var itemId = AddItem();
        var record = AddRecord(itemId, ["Comedy"], keywords: []);
        await _context.SaveChangesAsync();
        await _sync.SyncAsync(itemId, MediaKind.Movie, CancellationToken.None);

        record.Genres = ["Drama"];
        await _context.SaveChangesAsync();
        await _sync.SyncAsync(itemId, MediaKind.Movie, CancellationToken.None);

        Assert.Equal(["Drama"], await ValuesAsync(MetadataTagKind.Genre));
    }

    [Fact]
    public async Task An_unreadable_payload_costs_its_keywords_and_nothing_else()
    {
        // A provider payload this cannot parse must not take the genres with it, or one malformed
        // record would remove a title from genre search as well.
        var itemId = AddItem();
        var record = AddRecord(itemId, ["Action"], keywords: []);
        record.Raw = "{ this is not json";
        await _context.SaveChangesAsync();

        await _sync.SyncAsync(itemId, MediaKind.Movie, CancellationToken.None);

        Assert.Equal(["Action"], await ValuesAsync(MetadataTagKind.Genre));
        Assert.Empty(await ValuesAsync(MetadataTagKind.Keyword));
    }

    private async Task<IReadOnlyList<string>> ValuesAsync(MetadataTagKind kind) =>
        await _context.MetadataTags.AsNoTracking()
            .Where(tag => tag.Kind == kind)
            .Select(tag => tag.Value)
            .OrderBy(value => value)
            .ToListAsync();

    private Guid AddItem()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Movie,
            Title = "Subject",
            PublicId = Guid.NewGuid().ToString("N"),
            AddedAt = now,
            UpdatedAt = now,
        };
        _context.MediaItems.Add(item);
        return item.Id;
    }

    private MetadataRecord AddRecord(Guid itemId, IReadOnlyList<string> genres, IReadOnlyList<string> keywords)
    {
        // The movie shape: TMDb nests a movie's keywords under keywords.keywords (series use .results).
        var names = string.Join(",", keywords.Select(keyword => $"{{\"name\":\"{keyword}\"}}"));
        var record = new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = itemId,
            Provider = "tmdb",
            Language = "en-US",
            Title = "Subject",
            Genres = genres.ToList(),
            Raw = $"{{\"keywords\":{{\"keywords\":[{names}]}}}}",
            FetchedAt = DateTimeOffset.UtcNow,
        };
        _context.MetadataRecords.Add(record);
        return record;
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
