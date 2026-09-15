using MediaServer.Api.Data;
using MediaServer.Api.Groups;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Native;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Groups;

public sealed class GroupEndpointTests
{
    [Fact]
    public async Task Writes_and_settings_require_admin_and_native_routes_are_read_only_authenticated()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<GroupService>();
        builder.Services.AddScoped<MediaServerDbContext>();
        await using var app = builder.Build();
        app.MapGroupEndpoints();
        app.MapGroup(NativeEndpoints.RoutePrefix).AllowPublic().MapNativeGroupEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Equal(11, endpoints.Length);
        foreach (var route in endpoints)
        {
            var policies = route.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.NotEmpty(policies);
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods;
            var path = route.RoutePattern.RawText!;
            if (path.StartsWith("/native/"))
            {
                Assert.Equal(["GET"], methods);
                Assert.NotNull(route.Metadata.GetMetadata<PublicSurfaceAttribute>());
            }
            else if (!methods.Contains("GET") || path.EndsWith("definition") || path.EndsWith("candidates") || path.EndsWith("options"))
                Assert.Contains(policies, p => p.Policy == AppRoles.AdminPolicy);
        }
    }
}
