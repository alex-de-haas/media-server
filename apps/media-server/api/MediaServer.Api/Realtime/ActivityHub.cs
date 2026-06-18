using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MediaServer.Api.Realtime;

/// <summary>
/// Real-time hub for downloads and pipeline activity. Clients subscribe once and receive updates for
/// all active work; the server only pushes, so no client-callable methods are defined yet. Mounted at
/// <c>/hubs/activity</c> and reached by the web BFF proxy.
/// </summary>
[Authorize]
public sealed class ActivityHub : Hub
{
    public const string Path = "/hubs/activity";
}
