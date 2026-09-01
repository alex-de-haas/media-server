using Microsoft.Extensions.Logging.Abstractions;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Two enriches of one item can run at once — a manual refresh alongside a catalog-wide one, which the
/// coordinator serializes per catalog rather than per item — and both can discover the same new image. The
/// unique <c>(MediaItemId, RemotePath)</c> index means the second insert collides; the loser has to shrug
/// that off, because the row it was writing is the one already there.
/// </summary>
public sealed class EnrichImageRaceTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly Catalog _catalog;
    private readonly Guid _itemId = Guid.NewGuid();

    public EnrichImageRaceTests()
    {
        var now = DateTimeOffset.UtcNow;
        _catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies",
            CreatedAt = now, UpdatedAt = now,
        };

        using var context = _db.Create();
        context.Catalogs.Add(_catalog);
        context.MediaItems.Add(new MediaItem
        {
            Id = _itemId,
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = _catalog.Id,
            Kind = MediaKind.Movie,
            Title = "Inception",
            IdentityProvider = "tmdb",
            IdentityProviderId = "27205",
            AddedAt = now,
            UpdatedAt = now,
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task A_second_enrich_that_loses_the_race_still_completes()
    {
        // Both contexts read before either writes, so both believe the poster is new.
        using var first = _db.Create();
        using var second = _db.Create();
        var firstItem = await first.MediaItems.FirstAsync(item => item.Id == _itemId);
        var secondItem = await second.MediaItems.FirstAsync(item => item.Id == _itemId);

        await Enrich(first).EnrichAsync(_catalog, firstItem, CancellationToken.None);
        await Enrich(second).EnrichAsync(_catalog, secondItem, CancellationToken.None);

        using var reader = _db.Create();
        // One row, not two, and no exception from the loser — whose metadata still landed.
        var poster = Assert.Single(await reader.ImageAssets.Where(image => image.MediaItemId == _itemId).ToListAsync());
        Assert.Equal("https://image/poster.jpg", poster.RemotePath);
        Assert.NotEmpty(await reader.MetadataRecords.Where(record => record.MediaItemId == _itemId).ToListAsync());
    }

    [Fact]
    public async Task Re_enriching_the_same_item_adds_nothing_and_throws_nothing()
    {
        using var context = _db.Create();
        var item = await context.MediaItems.FirstAsync(candidate => candidate.Id == _itemId);

        await Enrich(context).EnrichAsync(_catalog, item, CancellationToken.None);
        await Enrich(context).EnrichAsync(_catalog, item, CancellationToken.None);

        using var reader = _db.Create();
        Assert.Single(await reader.ImageAssets.Where(image => image.MediaItemId == _itemId).ToListAsync());
    }

    private EnrichService Enrich(MediaServerDbContext context) => new(
        context,
        new FakeMetadataProvider(),
        new MediaServerSettings { SupportedLanguages = ["en-US"] },
        new PersonSyncService(context),
        new CollectionSyncService(context), new MetadataTagSync(context, NullLogger<MetadataTagSync>.Instance));

    public void Dispose() => _db.Dispose();
}
