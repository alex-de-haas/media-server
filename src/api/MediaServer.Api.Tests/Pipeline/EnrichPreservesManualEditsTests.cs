using Microsoft.Extensions.Logging.Abstractions;
using MediaServer.Api.Metadata;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Enrich now runs unattended: the nightly pass re-enriches whatever the provider says it edited, with
/// nobody watching. That turns "enrich overwrites a choice the operator made" from a bug someone would
/// notice into one that happens every night, so the choices are pinned down here.
///
/// Each of these holds by construction today — enrich adds images and metadata records and touches
/// nothing else. These tests exist to make that construction deliberate rather than incidental.
/// </summary>
public sealed class EnrichPreservesManualEditsTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly Catalog _catalog;
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();

    public EnrichPreservesManualEditsTests()
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
        context.MediaSources.AddRange(
            new MediaSource
            {
                Id = _sourceId, MediaItemId = _itemId, Container = "matroska",
                Path = "Inception (2010)/Inception.mkv", SizeBytes = 1, DurationTicks = 1, CreatedAt = now,
            },
            new MediaSource
            {
                Id = Guid.NewGuid(), MediaItemId = _itemId, Container = "matroska",
                Path = "Inception (2010)/Inception - 4K.mkv", SizeBytes = 2, DurationTicks = 1,
                CreatedAt = now.AddMinutes(-1), // older, so it would win the default ordering without a pin
            });
        context.SaveChanges();
    }

    [Fact]
    public async Task A_pinned_poster_stays_pinned_and_stays_present()
    {
        await using (var seed = _db.Create())
        {
            await EnrichAsync(seed); // the provider's poster arrives and is cached
            var chosen = await seed.ImageAssets.SingleAsync(image => image.MediaItemId == _itemId);
            await seed.MediaItems.Where(item => item.Id == _itemId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PreferredPosterTag, chosen.Tag));
        }

        await using (var night = _db.Create())
        {
            await EnrichAsync(night);
        }

        await using var verify = _db.Create();
        var item = await verify.MediaItems.SingleAsync(candidate => candidate.Id == _itemId);
        var poster = await verify.ImageAssets.SingleAsync(image => image.MediaItemId == _itemId);
        Assert.Equal(poster.Tag, item.PreferredPosterTag);
    }

    [Fact]
    public async Task The_default_version_pin_survives()
    {
        await using (var seed = _db.Create())
        {
            await seed.MediaItems.Where(item => item.Id == _itemId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DefaultSourceId, _sourceId));
        }

        await using (var night = _db.Create())
        {
            await EnrichAsync(night);
        }

        await using var verify = _db.Create();
        Assert.Equal(_sourceId, (await verify.MediaItems.SingleAsync(item => item.Id == _itemId)).DefaultSourceId);
    }

    [Fact]
    public async Task Hand_written_track_labels_and_sidecars_are_left_alone()
    {
        // Track titles and languages are edited by hand and sidecars are placed by hand. Neither comes
        // from the metadata provider, and neither is enrich's to rewrite.
        await using (var seed = _db.Create())
        {
            seed.MediaStreams.AddRange(
                new MediaStream
                {
                    Id = Guid.NewGuid(), MediaSourceId = _sourceId, StreamType = StreamType.Audio, Index = 1,
                    Codec = "ac3", Language = "rus", Title = "Гаврилов",
                },
                new MediaStream
                {
                    Id = Guid.NewGuid(), MediaSourceId = _sourceId, StreamType = StreamType.Audio, Index = 1000,
                    Codec = "ac3", Language = "rus", Title = "Дубляж",
                    IsExternal = true, ExternalPath = "Inception (2010)/Inception.ru.mka",
                });
            await seed.SaveChangesAsync();
        }

        await using (var night = _db.Create())
        {
            await EnrichAsync(night);
        }

        await using var verify = _db.Create();
        var streams = await verify.MediaStreams.Where(stream => stream.MediaSourceId == _sourceId).ToListAsync();
        Assert.Equal(2, streams.Count);
        Assert.Contains(streams, stream => stream.Title == "Гаврилов" && !stream.IsExternal);
        Assert.Contains(streams, stream => stream.Title == "Дубляж" && stream.IsExternal);
    }

    private async Task EnrichAsync(MediaServerDbContext context)
    {
        var item = await context.MediaItems.FirstAsync(candidate => candidate.Id == _itemId);
        var enrich = new EnrichService(
            context,
            new FakeMetadataProvider(),
            new MediaServerSettings { SupportedLanguages = ["en-US"] },
            new PersonSyncService(context),
            new CollectionSyncService(context), new MetadataTagSync(context, NullLogger<MetadataTagSync>.Instance));
        await enrich.EnrichAsync(_catalog, item, CancellationToken.None);
    }

    public void Dispose() => _db.Dispose();
}
