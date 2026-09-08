using System.Security.Claims;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using MediaServer.Api.Native;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Native;

public sealed class NativeHomeTests
{
    [Fact]
    public async Task Routes_are_native_public_surface_but_require_authorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<MediaServerDbContext>();
        builder.Services.AddScoped<LibraryReadService>();
        await using var app = builder.Build();
        app.MapGroup(NativeEndpoints.RoutePrefix).AllowPublic().MapNativeHomeEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Assert.Equal(2, routes.Length);
        Assert.All(routes, route =>
        {
            Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.NotNull(route.Metadata.GetMetadata<PublicSurfaceAttribute>());
        });
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData(-10, 1)]
    [InlineData(1000, 60)]
    public async Task Resume_is_bounded_and_scoped_to_the_caller(int? limit, int expected)
    {
        using var db = new JellyfinDatabase();
        var database = db.Context;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Root = "/movies" };
        var user = new AppUser { HostUserId = "viewer", Email = "viewer@example.com" };
        var other = new AppUser { HostUserId = "other", Email = "other@example.com" };
        database.Catalogs.Add(catalog);
        database.AppUsers.AddRange(user, other);
        await database.SaveChangesAsync();
        for (var i = 0; i < 65; i++)
        {
            var item = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie,
                PublicId = $"movie-{i}", Title = $"Movie {i}" };
            database.MediaItems.Add(item);
            database.UserItemData.Add(new UserItemData { AppUserId = user.Id, MediaItemId = item.Id,
                PlaybackPositionTicks = 100, LastPlayedDate = DateTimeOffset.UtcNow.AddMinutes(-i) });
        }
        await database.SaveChangesAsync();
        var library = new LibraryReadService(database, new UserDataService(database, TimeProvider.System), new MediaServerSettings());
        static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)]));

        var result = await NativeHomeEndpoints.ReadAsync(false, limit, Principal("viewer"), database, library, CancellationToken.None);
        Assert.Equal(expected, Assert.IsType<Ok<IReadOnlyList<LibraryRailItemDto>>>(result).Value!.Count);
        var otherResult = await NativeHomeEndpoints.ReadAsync(false, limit, Principal("other"), database, library, CancellationToken.None);
        Assert.Empty(Assert.IsType<Ok<IReadOnlyList<LibraryRailItemDto>>>(otherResult).Value!);
        foreach (var nextUp in new[] { false, true })
            Assert.IsType<UnauthorizedHttpResult>(await NativeHomeEndpoints.ReadAsync(nextUp, limit,
                Principal("unknown"), database, library, CancellationToken.None));
    }
}
