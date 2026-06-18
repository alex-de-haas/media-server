using MediaServer.Api.Jellyfin.Auth;
using Microsoft.AspNetCore.Http;

namespace MediaServer.Api.Tests.Jellyfin;

public sealed class MediaBrowserAuthorizationTests
{
    [Fact]
    public void Parses_the_mediabrowser_authorization_header()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization =
            "MediaBrowser Client=\"Infuse\", Device=\"iPhone\", DeviceId=\"abc123\", Version=\"8.3\", Token=\"secret-token\"";

        var parsed = MediaBrowserAuthorization.Parse(request, allowQueryToken: false);

        Assert.Equal("Infuse", parsed.Client);
        Assert.Equal("iPhone", parsed.Device);
        Assert.Equal("abc123", parsed.DeviceId);
        Assert.Equal("8.3", parsed.Version);
        Assert.Equal("secret-token", parsed.Token);
    }

    [Fact]
    public void Parses_the_emby_scheme_and_device_context_without_a_token()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["X-Emby-Authorization"] =
            "Emby Client=\"Infuse\", Device=\"Apple TV\", DeviceId=\"xyz\", Version=\"8.0\"";

        var parsed = MediaBrowserAuthorization.Parse(request, allowQueryToken: false);

        Assert.Equal("Apple TV", parsed.Device);
        Assert.Null(parsed.Token);
        Assert.Equal(new JellyfinDeviceContext("Infuse", "Apple TV", "xyz", "8.0"), parsed.ToDeviceContext());
    }

    [Fact]
    public void Reads_the_token_from_the_x_emby_token_header()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["X-Emby-Token"] = "header-token";

        var parsed = MediaBrowserAuthorization.Parse(request, allowQueryToken: false);

        Assert.Equal("header-token", parsed.Token);
    }

    [Fact]
    public void Honors_the_api_key_query_token_only_when_allowed()
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString("?api_key=query-token");

        Assert.Null(MediaBrowserAuthorization.Parse(request, allowQueryToken: false).Token);
        Assert.Equal("query-token", MediaBrowserAuthorization.Parse(request, allowQueryToken: true).Token);
    }

    [Fact]
    public void Returns_empty_when_nothing_is_present()
    {
        var parsed = MediaBrowserAuthorization.Parse(new DefaultHttpContext().Request, allowQueryToken: true);

        Assert.Null(parsed.Token);
        Assert.Null(parsed.Client);
    }
}
