# Hosty Runtime App

## Description

Media Server is a Hosty runtime app described by `apps/media-server/manifest.json`
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

- `api` — the .NET backend: torrent engine, catalog, metadata, probing, the
  automation orchestrator, SignalR, and the Jellyfin-compatible endpoints.
- `web` — the Next.js frontend and backend-for-frontend. Depends on `api`.

`api` exposes two ports so internal and external surfaces are isolated:

- `internal` (not public) — `/api/*` management endpoints and the SignalR hub,
  consumed by `web` via `HOSTY_DEPENDENCY_API_URL`.
- `jellyfin` (public) — Jellyfin-shaped routes (`/System`, `/Users`, `/Items`,
  `/Videos`, ...) consumed directly by Infuse and other Jellyfin clients.

`web` exposes one public `http` port, which is the Shell UI entrypoint.

## Runtime Profiles

- `dev` (`localCommand`) — default for development and for v1, because a host
  process can read operator-configured catalog paths directly without volume
  mounts. Omit `localPort` / `hostPort`; Core assigns loopback ports and injects
  `HOSTY_PORT_{KEY}` (and `PORT` for single-port services).
- `docker` — production images from GitHub Container Registry. Introduced once
  external host-path mounts for catalog roots are available (see
  [Storage and data](storage-and-data.md)). `docker` is declared as the
  manifest default for parity with the platform default, but v1 installs and runs
  under `dev`.

Keep the same service keys, endpoint keys, setting keys, data semantics, and UI
navigation across profiles so switching runtime is reviewable and reversible.

## Endpoints

- `ui` → `web:http`, `public: true`. The Shell UI entrypoint.
- `jellyfin` → `api:jellyfin`, `public: true`. Reachable by native Jellyfin
  clients with Media Server-owned tokens (see
  [Jellyfin compatibility](jellyfin-compatibility.md)).

The internal `api` port is not exposed as a public endpoint; only `web` reaches
it, through the injected dependency URL.

## Runtime Environment

Core injects, per service:

- `HOSTY_APP_ID`
- `HOSTY_APP_SERVICE_KEY`
- `HOSTY_APP_SERVICE_TOKEN`
- `HOSTY_CORE_ORIGIN` (process-to-Core origin)
- `HOSTY_CORE_PUBLIC_ORIGIN` (browser-facing Core origin)
- `HOSTY_APP_DATA_DIR`
- `HOSTY_PORT_{KEY}` (and `PORT` for single-port services)
- `HOSTY_DEPENDENCY_{KEY}_URL` (e.g. `HOSTY_DEPENDENCY_API_URL` injected into `web`)

The app must read these instead of hard-coding ports, origins, or paths.

## Identity And Sessions

Two independent auth domains.

**UI (Core-owned).** The browser only ever talks to the `web` origin.

1. Shell opens `web` with a one-time `?code`.
2. `web` exchanges it at `POST {HOSTY_CORE_ORIGIN}/api/auth/apps/token`.
3. `web` stores the returned app identity token in an app-origin HttpOnly cookie.
   Derive cookie attributes from the effective protocol: `SameSite=None; Secure`
   over https, `SameSite=Lax` without `Secure` on plain http.
4. `web` revalidates via `POST {HOSTY_CORE_ORIGIN}/api/auth/apps/revalidate` with
   `Authorization: Bearer <HOSTY_APP_SERVICE_TOKEN>` before extending trust.
5. `web` forwards the Host user identity to `api` (Bearer / `X-Docker-Host-Identity`);
   `api` validates it against Core and never trusts unsigned headers or cookies.

Page-to-page navigation inside the open app uses the app-origin session cookie,
so the app does not re-exchange a code on every Shell click.

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

Public endpoint origins are configured after install through Core-managed
`HOSTY_PUBLIC_ORIGIN_{ENDPOINT_KEY}` settings (`HOSTY_PUBLIC_ORIGIN_UI`,
`HOSTY_PUBLIC_ORIGIN_JELLYFIN`); empty means use the local `localhost` endpoint.

## Storage And Backups

- `data.enabled: true` with the primary app data directory at `HOSTY_APP_DATA_DIR`.
- The SQLite database, metadata/image caches, torrent engine state, and job state
  all live under this directory so Hosty backup/restore covers them.
- Catalog roots (the actual large media folders) are configured separately and
  are not part of app data or backups.

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
