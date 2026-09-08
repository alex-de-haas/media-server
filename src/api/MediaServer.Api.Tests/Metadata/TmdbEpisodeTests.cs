using System.Net;
using Imposter.Abstractions;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Jobs;
using MediaServer.Api.Realtime;
using Microsoft.Extensions.DependencyInjection;
using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: GenerateImposter(typeof(IHttpClientFactory))]
[assembly: GenerateImposter(typeof(IRealtimeNotifier))]

namespace MediaServer.Api.Tests.Metadata;

public sealed class TmdbEpisodeTests
{
    [Fact]
    public async Task Enriches_an_existing_double_episode_with_its_own_metadata_and_still_idempotently()
    {
        using var fixture = new JellyfinDatabase();
        await using var db = fixture.Create();
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "TV", Root = "/tv", Type = CatalogType.Series };
        var episode = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Episode,
            Title = "S01E02-E03", PublicId = "episode", IdentityProvider = "tmdb", IdentityProviderId = "123",
            ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 };
        db.Catalogs.Add(catalog); db.MediaItems.Add(episode); await db.SaveChangesAsync();
        var handler = new EpisodeHandler();
        var settings = new MediaServerSettings { TmdbApiKey = "0123456789abcdef0123456789abcdef", SupportedLanguages = ["en-US"] };
        var factory = IHttpClientFactory.Imposter();
        factory.CreateClient(Arg<string>.Any()).Returns(new HttpClient(handler) { BaseAddress = new("https://api.themoviedb.org/") });
        var provider = new TmdbMetadataProvider(factory.Instance(), settings, NullLogger<TmdbMetadataProvider>.Instance);
        var enrich = new EnrichService(db, provider, settings, new PersonSyncService(db), new CollectionSyncService(db),
            new MetadataTagSync(db, NullLogger<MetadataTagSync>.Instance));
        await enrich.EnrichAsync(catalog, episode, CancellationToken.None);
        await enrich.EnrichAsync(catalog, episode, CancellationToken.None);
        var metadata = await db.MetadataRecords.SingleAsync();
        Assert.Equal("The second episode", metadata.Title);
        Assert.Equal("Its own synopsis", metadata.Overview);
        Assert.Equal(TimeSpan.FromMinutes(48).Ticks, metadata.RuntimeTicks);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), metadata.ReleaseDate);
        var image = await db.ImageAssets.SingleAsync();
        Assert.Equal(ImageType.Backdrop, image.ImageType);
        Assert.EndsWith("/frame.jpg", image.RemotePath);
        Assert.All(handler.Paths, path => Assert.StartsWith("/3/tv/123/season/1/episode/2", path));
        Assert.Equal(3, episode.IndexNumberEnd);
        // The operator's catalog refresh also enriches already-published episodes.
        metadata.Title = "Stale series title";
        await db.SaveChangesAsync();
        var notifications = IRealtimeNotifier.Imposter();
        notifications.JobChangedAsync(Arg<string>.Any(), Arg<JobEvent>.Any(), Arg<CancellationToken>.Any()).Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.Create());
        services.AddScoped(sp => new EnrichService(sp.GetRequiredService<MediaServerDbContext>(), provider, settings,
            new PersonSyncService(sp.GetRequiredService<MediaServerDbContext>()),
            new CollectionSyncService(sp.GetRequiredService<MediaServerDbContext>()),
            new MetadataTagSync(sp.GetRequiredService<MediaServerDbContext>(), NullLogger<MetadataTagSync>.Instance)));
        using var serviceProvider = services.BuildServiceProvider();
        var jobs = new JobService(db, notifications.Instance());
        var refresh = new CatalogMetadataRefreshService(db, serviceProvider.GetRequiredService<IServiceScopeFactory>(), jobs,
            NullLogger<CatalogMetadataRefreshService>.Instance);
        var job = await jobs.StartAsync(CatalogMetadataRefreshService.JobType, "catalog", catalog.Id, CancellationToken.None);
        var report = await refresh.RunAsync(catalog.Id, job, CancellationToken.None);
        Assert.Equal(1, report.Refreshed);
        Assert.Equal(0, report.Failed);
        Assert.Equal("The second episode", (await db.MetadataRecords.AsNoTracking().SingleAsync()).Title);
        handler.NoImages = true;
        Assert.Empty(await provider.GetEpisodeImagesAsync(new("tmdb", "123"), 0, 1, ["en-US"], CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_replaces_legacy_backdrops_even_when_the_still_is_already_cached(bool stillAlreadyCached)
    {
        using var fixture = new JellyfinDatabase();
        await using var db = fixture.Create();
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "TV", Root = "/tv", Type = CatalogType.Series };
        var episode = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Episode,
            Title = "Episode", PublicId = "episode", IdentityProvider = "tmdb", IdentityProviderId = "123",
            ParentIndexNumber = 1, IndexNumber = 2 };
        db.Catalogs.Add(catalog); db.MediaItems.Add(episode);
        ImageAsset Asset(string path, string provider = "tmdb", ImageType type = ImageType.Backdrop) => new()
        {
            Id = Guid.NewGuid(), MediaItemId = episode.Id, Provider = provider, ImageType = type,
            RemotePath = $"https://image.tmdb.org/t/p/original/{path}.jpg", Tag = path,
        };
        db.ImageAssets.AddRange(Asset("legacy-show"), Asset("other-provider", "other"), Asset("poster", type: ImageType.Primary));
        if (stillAlreadyCached) db.ImageAssets.Add(Asset("frame"));
        await db.SaveChangesAsync();
        var handler = new EpisodeHandler { NoImages = true };
        var settings = new MediaServerSettings { TmdbApiKey = "0123456789abcdef0123456789abcdef", SupportedLanguages = ["en-US"] };
        var factory = IHttpClientFactory.Imposter();
        factory.CreateClient(Arg<string>.Any()).Returns(new HttpClient(handler) { BaseAddress = new("https://api.themoviedb.org/") });
        var provider = new TmdbMetadataProvider(factory.Instance(), settings, NullLogger<TmdbMetadataProvider>.Instance);
        var enrich = new EnrichService(db, provider, settings, new PersonSyncService(db), new CollectionSyncService(db),
            new MetadataTagSync(db, NullLogger<MetadataTagSync>.Instance));
        await enrich.EnrichAsync(catalog, episode, CancellationToken.None);
        Assert.True(await db.ImageAssets.AnyAsync(image => image.Tag == "legacy-show"));
        handler.NoImages = false;
        await enrich.EnrichAsync(catalog, episode, CancellationToken.None);
        await enrich.EnrichAsync(catalog, episode, CancellationToken.None);
        var images = await db.ImageAssets.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(images, image => image.Tag == "legacy-show");
        Assert.Contains(images, image => image.Tag == "other-provider");
        Assert.Contains(images, image => image.Tag == "poster");
        var backdrop = Assert.Single(images, image => image.Provider == "tmdb" && image.ImageType == ImageType.Backdrop);
        Assert.EndsWith("/frame.jpg", backdrop.RemotePath);
    }

    private sealed class EpisodeHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool NoImages { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var payload = request.RequestUri.AbsolutePath.EndsWith("/images")
                ? NoImages ? "{}" : """{"stills":[{"file_path":"/frame.jpg","iso_639_1":null}]}"""
                : """{"name":"The second episode","overview":"Its own synopsis","runtime":48,"air_date":"2026-09-01"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }
}
