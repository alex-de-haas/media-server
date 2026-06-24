# Hosty Runtime App

Status: Implemented
Created: 2026-06-15
Updated: 2026-06-21

## Description

Media Server is a Hosty runtime app described by `manifest.json` at the repo root
with `schemaVersion: "app.0.1"`. Hosty Core installs the app from a local app
directory or a manifest URL, manages its lifecycle, injects runtime environment,
issues app identity, exposes a scoped user directory, and backs up the app data
directory. This replaces the retired Docker Host module (`schemaVersion: "0.2"`)
contract.

## App Identity

- App id: `com.haas.media-server` (stable reverse-DNS, preserved across releases).
- Starting version: `0.1.0`.

## Services

Two services with stable keys across runtime profiles:

- `api` — the .NET backend: the torrent control client (drives the external
  `torrent-engine` app), catalog, metadata, probing, the automation orchestrator,
  the realtime (SSE) stream, and the Jellyfin-compatible endpoints.
- `web` — the Next.js frontend and backend-for-frontend. Depends on `api`.

`api` exposes two ports so internal and external surfaces are isolated:

- `internal` (not public) — `/api/*` management endpoints and the SignalR hub,
  consumed by `web` via `HOSTY_DEPENDENCY_API_URL`.
- `jellyfin` (public) — Jellyfin-shaped routes (`/System`, `/Users`, `/Items`,
  `/Videos`, ...) consumed directly by Infuse and other Jellyfin clients.

`web` exposes one public `http` port, which is the Shell UI entrypoint.

## Runtime Profiles

- `dev` (`localCommand`) — the primary local development loop; a host process can
  read operator-configured catalog paths directly. Omit `localPort` / `hostPort`;
  Core assigns loopback ports and injects `HOSTY_PORT_{KEY}` (and `PORT` for
  single-port services).
- `docker` — production images from GitHub Container Registry; the **v1 delivery
  target** (`defaultRuntime: docker`). Unblocked now that Hosty Core provides the
  external host-path mount model for catalog roots (`externalMounts`, injected as
  `HOSTY_MOUNT_{KEY}`) and Cloudflare-tunnel ingress (see
  [Storage and data](storage-and-data.md) and
  [Implementation plan](implementation-plan.md)).

Keep the same service keys, endpoint keys, setting keys, data semantics, and UI
navigation across profiles so switching runtime is reviewable and reversible.

## Endpoints

- `ui` → `web:http`, `public: true`. The Shell UI entrypoint.
- `jellyfin` → `api:jellyfin`, `public: true`. Reachable by native Jellyfin
  clients with Media Server-owned tokens (see
  [Jellyfin compatibility](jellyfin-compatibility.md)).

The internal `api` port is not exposed as a public endpoint; only `web` reaches
it, through the injected dependency URL.

Media Server holds **no** raw torrent port. Downloading is delegated to the
external, VPN-isolated `torrent-engine` app — a **required** cross-app dependency
declared in the manifest's `dependencies`, discovered via the injected
`HOSTY_DEPENDENCY_TORRENT_ENGINE_URL`, and driven over its HTTP control API + SSE
by `RemoteTorrentEngine`. All peer connectivity, the raw listen port, and port
mapping live in that app. When the dependency is unconfigured, a
`DisabledTorrentEngine` keeps the rest of the app working (see
[Torrents and organizer](torrents-and-organizer.md) and
[Torrent engine app](../ideas/torrent-engine-app.md)).

## Runtime Environment

Core injects, per service:

- `HOSTY_APP_ID`
- `HOSTY_APP_SERVICE_KEY`
- `HOSTY_APP_SERVICE_TOKEN`
- `HOSTY_CORE_ORIGIN` (process-to-Core origin)
- `HOSTY_CORE_PUBLIC_ORIGIN` (browser-facing Core origin)
- `HOSTY_APP_DATA_DIR`
- `HOSTY_PORT_{KEY}` (and `PORT` for single-port services)
- `HOSTY_DEPENDENCY_{KEY}_URL` for cross-app dependencies (e.g.
  `HOSTY_DEPENDENCY_TORRENT_ENGINE_URL` injected into `api`)

The app must read these instead of hard-coding ports, origins, or paths.

## Identity And Sessions

Two independent auth domains.

**UI (Core-owned).** The browser only ever talks to the `web` origin.

1. Shell opens `web` with a one-time `?code`.
2. `web` exchanges it at `POST {HOSTY_CORE_ORIGIN}/api/auth/apps/token`.
3. `web` stores the returned app identity token in an app-origin HttpOnly cookie.
   Derive cookie attributes from the effective protocol: `SameSite=None; Secure`
   over https, `SameSite=Lax` without `Secure` on plain http. Because the app is
   embedded in a cross-site Shell iframe, browser privacy controls (Safari ITP,
   third-party cookie deprecation) may block this cookie. The session must
   therefore also support a header-based fallback: the browser keeps the identity
   token in memory / `sessionStorage` and sends it as `Authorization: Bearer` on
   requests to `web`. Partitioned cookies (CHIPS) may be used where supported, but
   the header fallback is the robust cross-browser path.
4. `web` revalidates via `POST {HOSTY_CORE_ORIGIN}/api/auth/apps/revalidate` with
   `Authorization: Bearer <HOSTY_APP_SERVICE_TOKEN>` before extending trust.
5. `web` forwards the validated Host user identity to `api` as a bearer token that
   `api` re-validates against Core. (Hosty Core also accepts this identity as the
   `X-Docker-Host-Identity` header.) `api` never trusts unsigned or client-set
   headers or cookies.

Page-to-page navigation inside the open app uses the app-origin session cookie,
so the app does not re-exchange a code on every Shell click.

After validation, Media Server upserts an internal app user in SQLite. Hosty
admins map to Media Server `admin`; other assigned Hosty users map to Media
Server `user`. The app stores the Hosty user id and email on the internal user
row so it can re-link by unique email if Hosty user ids change.

**Jellyfin clients (app-owned).** Infuse cannot perform the app-code flow, so the
`jellyfin` endpoint uses Media Server-owned credentials and opaque access tokens.
See [Jellyfin compatibility](jellyfin-compatibility.md) and [Security](security.md).

## Scoped User Directory

To bind Media Server records and Jellyfin credentials to Host users, `api` calls:

```text
GET {HOSTY_CORE_ORIGIN}/api/internal/apps/{appId}/directory/users
Authorization: Bearer <HOSTY_APP_SERVICE_TOKEN>
```

This returns only enabled Host users explicitly assigned to the app, not the full
Host directory.

## Settings

App-owned configuration declared in the manifest (`key`, `type`, `default`,
`secret`, `required`). Do not define settings with the reserved
`HOSTY_PUBLIC_ORIGIN_` prefix. Recommended settings:

| Key | Type | Notes |
| --- | --- | --- |
| `TMDB_API_KEY` | string, secret, required | No default for secrets. |
| `SUPPORTED_LANGUAGES` | string | Ordered list, e.g. `ru-RU,en-US,ja`; first is fallback. |
| `JELLYFIN_SERVER_NAME` | string | Shown in Infuse. |
| `JELLYFIN_DISCOVERY_ENABLED` | boolean | Optional UDP discovery (default off). |
| `TORRENT_MAX_DOWNLOAD_SPEED` | number | 0 = unlimited. |
| `TORRENT_MAX_UPLOAD_SPEED` | number | 0 = unlimited. |
| `FFPROBE_PATH` | string | Path to the `ffprobe` binary on the host (dev profile). |

Public endpoint origins are configured after install through Core-managed
`HOSTY_PUBLIC_ORIGIN_{ENDPOINT_KEY}` settings (`HOSTY_PUBLIC_ORIGIN_UI`,
`HOSTY_PUBLIC_ORIGIN_JELLYFIN`); empty means use the local `localhost` endpoint.

## Sample Manifest

The authoritative manifest is `manifest.json` at the repo root.
[Implementation plan §4](implementation-plan.md) keeps the original planning copy,
now historical — it predates the torrent-engine extraction and still shows the
removed raw `torrent` port and the `TORRENT_ENABLE_PORT_MAPPING` /
`TORRENT_BIND_ADDRESS` settings. The real `app.0.1` schema uses **arrays** (not
objects) for `services` and `endpoints`, a top-level `runtimeProfiles` list, and
per-service `runtimes` keyed by profile key:

```jsonc
{
  "schemaVersion": "app.0.1",
  "id": "com.haas.media-server",
  "version": "0.1.0",
  "name": "Media Server",
  "runtimeProfiles": [
    { "key": "docker", "type": "docker", "default": true },
    { "key": "dev",    "type": "localCommand" }
  ],
  "defaultRuntime": "docker",
  "services": [
    {
      "key": "api",
      "runtimes": {
        "docker": { "type": "docker", "image": { "repository": "ghcr.io/<owner>/media-server-api", "tag": "latest" }, "ports": [ /* internal; jellyfin (public) */ ] },
        "dev":    { "type": "localCommand", "command": "dotnet run --project MediaServer.Api", "ports": [ /* same keys */ ] }
      }
    },
    {
      "key": "web",
      "dependsOn": ["api"],
      "runtimes": {
        "docker": { "type": "docker", "image": { "repository": "ghcr.io/<owner>/media-server-web", "tag": "latest" }, "ports": [ { "key": "http", "containerPort": 3000, "public": true } ] },
        "dev":    { "type": "localCommand", "command": "pnpm dev", "ports": [ { "key": "http", "containerPort": 3000, "public": true } ] }
      }
    }
  ],
  "endpoints": [
    { "key": "ui",       "service": "web", "port": "http",     "public": true },
    { "key": "jellyfin", "service": "api", "port": "jellyfin", "public": true }
  ],
  "dependencies": [
    { "id": "com.haas.torrent-engine", "required": true, "endpoints": [ { "key": "control", "as": "torrent-engine" } ] }
  ],
  "data": { "enabled": true },
  "externalMounts": {
    "catalogRoots": { "kind": "host-path", "multiple": true, "mode": "rw", "service": "api", "required": true }
  },
  "settings": [ /* TMDB_API_KEY (secret, required), SUPPORTED_LANGUAGES, JELLYFIN_*, FFPROBE_PATH, TORRENT_MAX_DOWNLOAD_SPEED, TORRENT_MAX_UPLOAD_SPEED */ ],
  "capabilities": ["open", "update", "restart", "stop", "remove", "backup", "restore", "logs"]
}
```

The default install runs the `docker` profile; use `--runtime dev` for local
development. The repo-root `manifest.json` is the full, current manifest (every
port, dependency, and setting).

## Storage And Backups

- `data.enabled: true` with the primary app data directory at `HOSTY_APP_DATA_DIR`.
- The SQLite database, metadata/image caches, and job state all live under this
  directory so Hosty backup/restore covers them. (Torrent fast-resume/engine state
  lives in the external `torrent-engine` app's own data, not here.)
- Catalog roots (the actual large media folders) are configured separately and
  are not part of app data or backups.
- On schema upgrade, the app prefers to request an on-demand backup from Core
  before applying EF Core migrations; if Core does not expose an app-callable
  backup endpoint, the app applies migrations without a pre-migration backup (see
  [Storage and data](storage-and-data.md)).

Details in [Storage and data](storage-and-data.md).

## Shell Embedding

The `web` UI runs inside the Hosty Shell sandboxed iframe. It must:

- use relative URLs or `HOSTY_CORE_PUBLIC_ORIGIN`, never hard-coded origins;
- keep client routing compatible with `ui.entrypoint.path`;
- avoid reading Host cookies, Host local storage, or the parent DOM;
- avoid top-level redirects, frame busting, and popup auth flows;
- validate SignalR transport (WebSocket, SSE, long-polling fallback) through the
  Core-managed runtime, because behavior can depend on the embed route.

## Capabilities

`["open", "update", "restart", "stop", "remove", "backup", "restore", "logs"]`.

## Local Validation

```bash
hosty core start
hosty apps install . --runtime dev
hosty apps start com.haas.media-server
hosty apps open com.haas.media-server --user user@hosty.local --mode shell
```

Identity, Shell embedding, assignments, scoped directory, redirects, WebSockets,
and SSE must be checked through this Core-managed lifecycle. Standalone dev
servers can validate UI and business logic only.
