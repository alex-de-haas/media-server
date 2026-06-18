using MediaServer.Api.Configuration;
using MediaServer.Api.Hosty;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Stable server-level facts the compatibility surface reports. The version is a tested constant —
/// Infuse 8.3 is known to pair against Jellyfin 10.11, and some clients gate features on it, so bump
/// it deliberately after verifying. See <c>docs/planning/jellyfin-compatibility.md</c>.
/// </summary>
public sealed class JellyfinServerContext(HostyOptions hosty, MediaServerSettings settings)
{
    public const string ServerVersion = "10.11.0";

    public string ServerId { get; } = JellyfinIds.Server(hosty.AppId);

    public string ServerName => settings.JellyfinServerName;
}
