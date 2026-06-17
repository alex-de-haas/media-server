import { HubConnection, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { getBearerToken } from "@/lib/api";

/**
 * Builds a SignalR connection to a hub exposed through the same-origin BFF proxy (e.g.
 * `/api/proxy/hubs/jobs`). The identity bearer is supplied per (re)connect. The backend job hub
 * lands in M1; this factory wires the client now so real-time UI can attach to it.
 */
export function createHubConnection(hubPath: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(hubPath, { accessTokenFactory: () => getBearerToken() ?? "" })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
