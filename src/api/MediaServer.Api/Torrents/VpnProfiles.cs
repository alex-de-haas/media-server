using System.Net;

namespace MediaServer.Api.Torrents;

/// <summary>One OpenVPN profile in the engine's profiles folder: its id (the file name without extension) and
/// the host[:port] of its first <c>remote</c> line — a label for the picker, never the file contents.</summary>
public sealed record VpnProfile(string Id, string? Remote);

/// <summary>The engine's profiles and the one it runs (mirrors <c>torrent-engine</c>'s
/// <c>VpnProfilesResponse</c>). <see cref="Active"/> is <c>null</c> before the engine started one.</summary>
public sealed record VpnProfiles(string? Active, IReadOnlyList<VpnProfile> Profiles);

/// <summary>Body of <c>PUT /api/vpn/profile</c>.</summary>
public sealed record SelectVpnProfileRequest(string? Id);

/// <summary>The engine — or its absence — refused a control request. Carries the status to relay to the caller
/// and the engine's own message, which is written for an operator (an unknown profile id lists the known ones).</summary>
public sealed class EngineRequestException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
