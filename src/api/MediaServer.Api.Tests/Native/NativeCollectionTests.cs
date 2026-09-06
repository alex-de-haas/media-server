using MediaServer.Api.Collections;
using MediaServer.Api.Library;
using MediaServer.Api.Native;
using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Native;

public sealed class NativeCollectionTests
{
    [Fact]
    public async Task Collection_routes_are_publicly_routable_but_require_authentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<CollectionReadService>();
        builder.Services.AddScoped<MediaServerDbContext>();
        builder.Services.AddScoped<JellyfinImageService>();
        await using var app = builder.Build();
        app.MapGroup(NativeEndpoints.RoutePrefix).AllowPublic().MapNativeCollectionEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Assert.Equal(3, endpoints.Length);
        var imageEndpoint = Assert.Single(endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText!.EndsWith("/images/{imageType}"));
        var imageResponse = Assert.Single(
            imageEndpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            response => response.StatusCode == 200);
        Assert.Equal(typeof(Stream), imageResponse.Type);
        Assert.Equal(new[] { "image/gif", "image/jpeg", "image/png", "image/webp" },
            imageResponse.ContentTypes.OrderBy(contentType => contentType));
        foreach (var endpoint in endpoints)
        {
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.NotNull(endpoint.Metadata.GetMetadata<PublicSurfaceAttribute>());
        }
    }

    [Fact]
    public void Summary_advertises_local_artwork_and_changes_tag_when_art_changes()
    {
        var id = Guid.NewGuid();
        var first = NativeCollectionEndpoints.Project(new CollectionSummaryDto(id, "Saga", "https://cdn/one", 2));
        var second = NativeCollectionEndpoints.Project(new CollectionSummaryDto(id, "Saga", "https://cdn/two", 2));
        Assert.StartsWith($"/native/v1/collections/{id:D}/images/primary?tag=", first.PosterUrl);
        Assert.NotEqual(first.PosterUrl, second.PosterUrl);
        Assert.Equal(first.PosterUrl, NativeCollectionEndpoints.Project(new CollectionSummaryDto(id, "Saga", "https://cdn/one", 2)).PosterUrl);
        Assert.Equal(2, first.ItemCount);
    }

    [Fact]
    public void Detail_keeps_member_identity_and_order_without_provider_urls()
    {
        var first = new LibraryItemDto(Guid.NewGuid(), "public", Guid.NewGuid(), "Movie", "First", 2000, "https://cdn/poster", null);
        var second = first with { Id = Guid.NewGuid(), Title = "Second", PosterUrl = null };
        var result = NativeCollectionEndpoints.Project(new CollectionDetailDto(Guid.NewGuid(), "Saga", null, null, [first, second]));
        Assert.Null(result.PosterUrl);
        Assert.Null(result.BackdropUrl);
        Assert.Equal(new[] { first.Id, second.Id }, result.Items.Select(item => item.Id));
        Assert.Equal($"/native/v1/items/{first.Id:D}/images/primary", result.Items[0].PosterUrl);
        Assert.Null(result.Items[1].PosterUrl);
    }
}
