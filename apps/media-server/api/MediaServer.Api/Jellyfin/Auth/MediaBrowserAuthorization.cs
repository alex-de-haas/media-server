using System.Text.RegularExpressions;

namespace MediaServer.Api.Jellyfin.Auth;

/// <summary>
/// Parses the credentials a Jellyfin/Emby client attaches to a request: the
/// <c>Authorization: MediaBrowser …</c> (or <c>Emby …</c>) scheme, the legacy
/// <c>X-Emby-Authorization</c> header, the <c>X-Emby-Token</c>/<c>X-MediaBrowser-Token</c> header, and
/// — only for media/image URLs that clients open without custom headers — the <c>api_key</c> query
/// parameter. Query-string tokens are deliberately restricted to those endpoints.
/// </summary>
public sealed partial record MediaBrowserAuthorization(
    string? Client, string? Device, string? DeviceId, string? Version, string? Token)
{
    public JellyfinDeviceContext ToDeviceContext() => new(Client, Device, DeviceId, Version);

    public static MediaBrowserAuthorization Parse(HttpRequest request, bool allowQueryToken)
    {
        var parsed = ParseSchemeHeader(request.Headers.Authorization.ToString())
            ?? ParseSchemeHeader(request.Headers["X-Emby-Authorization"].ToString())
            ?? ParseSchemeHeader(request.Headers["X-MediaBrowser-Authorization"].ToString())
            ?? new MediaBrowserAuthorization(null, null, null, null, null);

        var token = NullIfEmpty(parsed.Token)
            ?? NullIfEmpty(request.Headers["X-Emby-Token"].ToString())
            ?? NullIfEmpty(request.Headers["X-MediaBrowser-Token"].ToString());

        if (token is null && allowQueryToken)
        {
            token = NullIfEmpty(request.Query["api_key"].ToString())
                ?? NullIfEmpty(request.Query["ApiKey"].ToString());
        }

        return parsed with { Token = token };
    }

    private static MediaBrowserAuthorization? ParseSchemeHeader(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var trimmed = headerValue.Trim();
        foreach (var scheme in new[] { "MediaBrowser ", "Emby " })
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[scheme.Length..];
                break;
            }
        }

        string? client = null, device = null, deviceId = null, version = null, token = null;
        foreach (Match match in PairPattern().Matches(trimmed))
        {
            var key = match.Groups["k"].Value;
            var value = match.Groups["v"].Value;
            switch (key.ToLowerInvariant())
            {
                case "client": client = value; break;
                case "device": device = value; break;
                case "deviceid": deviceId = value; break;
                case "version": version = value; break;
                case "token": token = value; break;
            }
        }

        return new MediaBrowserAuthorization(client, device, deviceId, version, NullIfEmpty(token));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("(?<k>\\w+)=\"(?<v>[^\"]*)\"")]
    private static partial Regex PairPattern();
}
