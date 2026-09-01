using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// The one-time projection of tags for metadata written before the tag table existed. Without it a
/// settled library becomes searchable by genre only as its titles happen to be re-enriched, which for
/// a library nobody is adding to is never.
/// </summary>
public sealed class MetadataTagBackfillWorkerTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly ServiceProvider _provider;

    public MetadataTagBackfillWorkerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.Create());
        services.AddScoped<MetadataTagSync>();
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Records_written_before_the_table_existed_become_searchable()
    {
        Seed(genres: ["Action"], keywords: ["heist"]);

        Assert.Equal(1, await RunAsync());

        await using var context = _db.Create();
        Assert.Equal(
            [(MetadataTagKind.Genre, "Action"), (MetadataTagKind.Keyword, "heist")],
            await context.MetadataTags.OrderBy(tag => tag.Kind)
                .Select(tag => ValueTuple.Create(tag.Kind, tag.Value)).ToListAsync());
    }

    [Fact]
    public async Task A_second_run_finds_nothing_left_to_do()
    {
        // Including for a record that projects to no tags at all. Without the marker row such a record
        // looks unprocessed forever, so every restart would walk the whole library again — and on a
        // large one that is a scan nobody asked for, on every boot.
        Seed(genres: [], keywords: []);
        Seed(genres: ["Comedy"], keywords: []);

        Assert.Equal(2, await RunAsync());
        Assert.Equal(0, await RunAsync());
    }

    [Fact]
    public async Task A_record_that_projects_to_nothing_stays_out_of_search()
    {
        // The marker exists to make "already done" cheap, not to be findable. A blank value must never
        // come back as a genre, or an empty filter would match it.
        Seed(genres: [], keywords: []);

        await RunAsync();

        await using var context = _db.Create();
        var tag = await context.MetadataTags.SingleAsync();
        Assert.Equal(MetadataTagBackfillWorker.EmptyMarker, tag.Value);
        Assert.Equal(string.Empty, tag.Value);
    }

    [Fact]
    public async Task A_library_larger_than_one_batch_is_covered_whole()
    {
        // The walk advances by id, and the batch is joined to its media items — a join is free to return
        // rows in any order, so a cursor taken from whatever landed last would step over the records
        // behind it. Several batches is the only arrangement where that shows.
        //
        // What this does *not* establish: that taking the cursor from the last joined row rather than
        // the batch maximum is wrong. SQLite returns these batches in id order anyway, so the two agree
        // here and the mutation is invisible. The maximum is used because the join is not required to
        // preserve order, and the walk stops when the cursor fails to advance — before that guard
        // existed, a cursor moving backwards re-read the same batch forever, which is a background
        // service burning a core rather than a test going red.
        for (var i = 0; i < 5; i++)
        {
            Seed(genres: ["Action"], keywords: []);
        }

        Assert.Equal(5, await RunAsync(batchSize: 2));

        await using var context = _db.Create();
        Assert.Equal(5, await context.MetadataTags.CountAsync());
        Assert.Equal(0, await RunAsync(batchSize: 2));
    }

    private Task<int> RunAsync(int batchSize = 200) =>
        new MetadataTagBackfillWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MetadataTagBackfillWorker>.Instance,
            batchSize)
            .RunAsync(CancellationToken.None);

    private void Seed(IReadOnlyList<string> genres, IReadOnlyList<string> keywords)
    {
        using var context = _db.Create();
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
        context.MediaItems.Add(item);
        var names = string.Join(",", keywords.Select(keyword => $"{{\"name\":\"{keyword}\"}}"));
        context.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = item.Id,
            Provider = "tmdb",
            Language = "en-US",
            Title = "Subject",
            Genres = genres.ToList(),
            Raw = $"{{\"keywords\":{{\"keywords\":[{names}]}}}}",
            FetchedAt = now,
        });
        context.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
    }
}
