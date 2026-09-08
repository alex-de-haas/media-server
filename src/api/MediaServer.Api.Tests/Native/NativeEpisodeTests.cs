using System.Security.Claims;
using MediaServer.Api.Jellyfin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Native;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediaServer.Api.Tests.Native;

public sealed class NativeEpisodeTests
{
    [Fact]
    public async Task Route_requires_authorization_on_the_public_native_surface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<MediaServerDbContext>();
        builder.Services.AddScoped<LibraryReadService>();
        builder.Services.AddSingleton(new MediaServerSettings());
        await using var app = builder.Build();
        app.MapGroup(NativeEndpoints.RoutePrefix).AllowPublic().MapNativeEpisodeEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.NotNull(endpoint.Metadata.GetMetadata<PublicSurfaceAttribute>());
    }

    [Fact]
    public async Task Lists_only_available_episodes_with_instance_stills_and_viewer_progress()
    {
        using var fixture = new JellyfinDatabase();
        await using var db = fixture.Create();
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "TV", Root = "/tv", Type = CatalogType.Series };
        db.Catalogs.Add(catalog);
        var series = Item(MediaKind.Series);
        var season = Item(MediaKind.Season); season.SeriesId = series.Id; season.IndexNumber = 1;
        var first = Episode(1); var second = Episode(2); var missing = Episode(3); var removed = Episode(4);
        removed.RemovedAt = DateTimeOffset.UtcNow;
        var unpublished = Episode(5); unpublished.PublicId = null;
        var foreignSeason = Item(MediaKind.Season);
        var mismatched = Episode(6); mismatched.SeasonId = foreignSeason.Id;
        db.MediaItems.AddRange(series, season, first, second, missing, removed, unpublished, foreignSeason, mismatched);
        foreach (var episode in new[] { first, second, removed, unpublished, mismatched })
            db.MediaSources.Add(new MediaSource { Id = Guid.NewGuid(), MediaItemId = episode.Id,
                Path = $"/tv/{episode.Id}.mkv", Container = "mkv", DurationTicks = TimeSpan.FromMinutes(42).Ticks });
        var user = new AppUser { HostUserId = "viewer", DisplayName = "Viewer" };
        var other = new AppUser { HostUserId = "other", DisplayName = "Other" };
        db.AppUsers.AddRange(user, other);
        await db.SaveChangesAsync();
        db.UserItemData.Add(new UserItemData { AppUserId = user.Id, MediaItemId = first.Id, PlaybackPositionTicks = 100 });
        db.ImageAssets.Add(new ImageAsset { Id = Guid.NewGuid(), MediaItemId = first.Id, ImageType = ImageType.Backdrop,
            Provider = "tmdb", RemotePath = "https://image.tmdb.org/still.jpg", Tag = "frame" });
        await db.SaveChangesAsync();
        var settings = new MediaServerSettings();
        var library = new LibraryReadService(db, new UserDataService(db, TimeProvider.System), settings);
        async Task<Microsoft.AspNetCore.Http.IResult> Read(Guid id, Guid? sid, string viewer = "viewer") =>
            await NativeEpisodeEndpoints.ReadAsync(id, sid,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, viewer)])),
                db, library, settings, CancellationToken.None);
        var rows = Assert.IsType<Ok<IReadOnlyList<NativeEpisodeDto>>>(await Read(series.Id, season.Id)).Value!;
        Assert.Equal(new[] { first.Id, second.Id }, rows.Select(row => row.Episode.Id));
        Assert.EndsWith("/images/backdrop?tag=frame", rows[0].Still);
        Assert.StartsWith("/native/v1/", rows[0].Still);
        Assert.Equal(100, rows[0].Episode.UserData!.PlaybackPositionTicks);
        Assert.Equal(TimeSpan.FromMinutes(42).Ticks, rows[0].DurationTicks);
        Assert.Null(rows[1].Still);
        var otherRows = Assert.IsType<Ok<IReadOnlyList<NativeEpisodeDto>>>(await Read(series.Id, null, "other")).Value!;
        Assert.Equal(2, otherRows.Count);
        Assert.Equal(0, otherRows[0].Episode.UserData?.PlaybackPositionTicks ?? 0);
        Assert.IsType<UnauthorizedHttpResult>(await Read(series.Id, null, "unknown"));
        Assert.IsType<NotFound>(await Read(series.Id, foreignSeason.Id));
        Assert.IsType<NotFound>(await Read(first.Id, null));
        var summaries = await NativeEpisodeEndpoints.AvailableSeasonsAsync(series.Id,
            [new(season.Id, season.PublicId, 1, "Season 1", 5, null)], db, CancellationToken.None);
        Assert.Equal(2, Assert.Single(summaries).EpisodeCount);
        season.RemovedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Empty(Assert.IsType<Ok<IReadOnlyList<NativeEpisodeDto>>>(await Read(series.Id, null)).Value!);
        series.RemovedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.IsType<NotFound>(await Read(series.Id, null));

        MediaItem Item(MediaKind kind) => new() { Id = Guid.NewGuid(), CatalogId = catalog.Id,
            Kind = kind, Title = kind.ToString(), PublicId = Guid.NewGuid().ToString("N") };
        MediaItem Episode(int number)
        {
            var item = Item(MediaKind.Episode); item.SeriesId = series.Id; item.SeasonId = season.Id;
            item.ParentIndexNumber = 1; item.IndexNumber = number; return item;
        }
    }
}
