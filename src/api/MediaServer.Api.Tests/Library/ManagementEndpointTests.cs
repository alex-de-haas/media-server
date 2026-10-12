using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Library;

public sealed class ManagementEndpointTests
{
    [Fact]
    public async Task Management_requires_admin_viewer_projection_requires_identity_and_query_defaults_bind()
    {
        using var database = new JellyfinDatabase();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(database.Context);
        builder.Services.AddSingleton(new MediaServerSettings());
        builder.Services.AddScoped<ManagementService>();
        await using var app = builder.Build();
        app.MapManagementEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Equal(4, routes.Length);
        foreach (var route in routes)
        {
            var auth = route.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.NotEmpty(auth);
            Assert.Equal(["GET"], route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            if (route.RoutePattern.RawText!.StartsWith("/api/management"))
                Assert.Contains(auth, p => p.Policy == AppRoles.AdminPolicy);
            else Assert.DoesNotContain(auth, p => p.Policy == AppRoles.AdminPolicy);
        }
        var list = routes.Single(r => r.RoutePattern.RawText == "/api/management/items");
        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await list.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(25, json.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("total").GetInt32());

        context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?limit=101");
        context.Response.Body = new MemoryStream();
        await list.RequestDelegate!(context);
        Assert.Equal(400, context.Response.StatusCode);

        var target = new MediaItem { Id = Guid.NewGuid(), PublicId = "target", Title = "B", Kind = MediaKind.Movie };
        database.Context.MediaItems.AddRange(
            new MediaItem { Id = Guid.NewGuid(), PublicId = "first", Title = "A", Kind = MediaKind.Movie }, target);
        await database.Context.SaveChangesAsync();
        context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString($"?focusId={target.Id}&limit=1");
        context.Response.Body = new MemoryStream();
        await list.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var focused = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(2, focused.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, focused.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(target.Id, Assert.Single(focused.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }
}
