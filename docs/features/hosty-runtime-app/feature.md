# Hosty Runtime App

Created: 2026-06-15
Updated: 2026-10-05

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
  `/Videos`, ...) and the native client's `/native/v1/*` surface, consumed directly by
  Infuse, by the Apple clients, and by other Jellyfin clients.

`web` exposes one public `http` port, which is the Shell UI entrypoint.

### The media port is bound on every interface

Under the **`docker` runtime**, `jellyfin` carries `"expose": "host"` with a pinned `hostPort`
of 8096, so Core publishes it as `0.0.0.0:8096` rather than on loopback. Every other port
stays on loopback, which is Core's default and right for them: they are reached by Core's own
proxy. The `dev` profile does not carry the fields — under `localCommand` they are validated
but inert, and pinning 8096 there would collide with the container's.

This port is different because **its clients are not on this machine**. A television on the
same network has no route to a loopback port, so without this the only way to a media server
across the room was out to the internet and back — which measured as a hard ceiling near 100
Mbit/s and 67 ms of latency per request, on a film needing 53 Mbit/s and holding two seconds
of buffer.

#### What that makes reachable from the network

The Jellyfin and native surfaces become reachable from anything on the local network. They
already were from the internet, through Core's endpoint, so this is not a new class of
exposure — but it is a wider one, and the anonymous part of it is worth stating exactly
rather than waving at:

| Anonymous route | What it returns |
| --- | --- |
| `GET /native/v1/server/public` | server name, app id, surface version, Core's origin |
| `GET /System/Info/Public` | server id, name, version |
| `GET|POST /System/Ping` | the server name |
| `GET /Branding/Configuration` | branding defaults |
| `GET /Users/Public` | deliberately an empty list — users are never enumerated |
| `POST /Users/AuthenticateByName` | **issues a credential**; rate-limited to 10 attempts per 30 s per address, with per-account lockout |
| `/native/v1/media/…` | the media itself, gated by a signed token in the URL rather than by a session |

Everything else requires an identity. The discovery routes are deliberately thin — none of
them says anything about the library, its users, or which integrations are configured — but
"thin" is not "nothing", and the login route is the one an operator should weigh: on a
network shared with guests it is now reachable by them.

## Runtime Profiles

- `dev` (`localCommand`) — the primary local development loop; a host process can
  read operator-configured catalog paths directly. Omit `localPort` / `hostPort`;
  Core assigns loopback ports and injects `HOSTY_PORT_{KEY}` (and `PORT` for
  single-port services).
- `docker` — production images from GitHub Container Registry; the **v1 delivery
  target** (`defaultRuntime: docker`). Unblocked now that Hosty Core provides the
  external host-path mount model for catalog roots (`externalMounts`, injected as
  `HOSTY_MOUNT_{KEY}`) and Cloudflare-tunnel ingress (see
  [Storage and data](../storage-and-data/feature.md) and
  [Implementation plan](../implementation-plan.md)).

Keep the same service keys, endpoint keys, setting keys, data semantics, and UI
navigation across profiles so switching runtime is reviewable and reversible.

## Endpoints

- `ui` → `web:http`, `public: true`. The Shell UI entrypoint.
- `jellyfin` → `api:jellyfin`, `public: true`. Reachable by native Jellyfin
  clients with Media Server-owned tokens (see
  [Jellyfin compatibility](../jellyfin-compatibility/feature.md)).

The internal `api` port is not exposed as a public endpoint; only `web` reaches
it, through the injected dependency URL.

Media Server holds **no** raw torrent port. Downloading is delegated to the
external, VPN-isolated `torrent-engine` app — a **required** cross-app dependency
declared in the manifest's `dependencies`, discovered via the injected
`HOSTY_DEPENDENCY_TORRENT_ENGINE_URL`, and driven over its HTTP control API + SSE
by `RemoteTorrentEngine`. All peer connectivity, the raw listen port, and port
mapping live in that app. When the dependency is unconfigured, a
`DisabledTorrentEngine` keeps the rest of the app working (see
[Torrents and organizer](../torrents-and-organizer/feature.md) and
[Torrent engine app](../../ideas/torrent-engine-app.md)).

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

**UI (Core-owned).** Shell opens the `web` origin without a user credential.
The SDK `AppIdentityBridge` probes `/api/auth/session` and mounts protected content
only after session validation. JSON API requests and SSE connections use SDK
`appFetch` on the same app origin and refuse credential-bearing redirects.

The web dependency is `@hosty-sdk/app: ^0.21.0`, with SDK `0.21.0` resolved from
npm in the checked-in pnpm lockfile. The app uses the published SDK's proof-aware
exchange and asynchronous recovery integration.

1. Every sign-in attempt has an independent cryptographically random private
   verifier and public state. The SDK derives a public S256 challenge using the
   maintained portable SHA-256 implementation. Popup proof stays in the initiating
   document's memory; navigation proof uses a bounded, short-lived app-origin
   `sessionStorage` entry. The verifier never appears in a URL, a Core form, a
   parent-frame message, or a log. A callback requires a matching local attempt,
   state and exact app callback before exchange; state alone does not prove it.
2. For protocol 2, an app-origin browser form posts the public challenge, state,
   callback and mode to Core `/api/apps/{appId}/sign-in-intent`. Core validates the
   exact browser `Origin` and navigation context, binds the intent to a unique
   HttpOnly Core browser nonce, and redirects to `/open?requestId=...`. Arbitrary
   `/open` GET challenge parameters cannot issue a code. Core's cookie hostname is
   isolated from every app endpoint hostname, including endpoints on other ports.
   An initial embedded document attempts silent recovery once, then offers an
   app-owned popup opened synchronously by a user action. A known silent iframe
   intent with a blocked nonce returns only state-correlated `login_required`,
   without a code or consumed intent. Standalone recovery uses guarded navigation;
   native clients intercept the app-initiated public proof GET before network
   access and correlate in-place renewal results with the unchanged app document.
3. The browser posts `{ code, codeVerifier }` to `/api/auth/app-code`; the SDK
   server handler exchanges it at
   `POST {HOSTY_CORE_ORIGIN}/api/auth/apps/token` with
   `Authorization: Bearer <HOSTY_APP_SERVICE_TOKEN>`. A code-only or malformed-proof
   request returns 400 locally without contacting Core. A missing or blank service
   token returns 503 `app_service_token_missing` before exchange. A valid-shaped
   wrong proof returns Core's 401 `invalid_code` and does not consume the code.
   Core checks the service app audience as well as the proof before consumption.
   Both browser-to-app and server-to-Core proof POSTs use `redirect: "error"` and
   `cache: "no-store"`.
4. `web` stores the returned app identity token in its `hosty_identity` HttpOnly
   app-origin cookie. Cookie lifetime follows the token: HTTPS uses
   `SameSite=None; Secure`, and HTTP uses `SameSite=Lax` without `Secure`. Embedded
   launches also keep the grant in memory and app-origin `sessionStorage` for the
   current tab, restoring it before the first probe after reload when third-party
   cookies are blocked. Standalone launches do not store this grant in
   `sessionStorage`; neither mode uses `localStorage`. The app's Core audience
   remains `com.haas.media-server`, separate from Core login and other app sessions.
5. `web` revalidates with Core `/api/auth/apps/revalidate` using its app service
   bearer before extending trust. It forwards the validated Host user identity to
   `api` as a bearer token that `api` revalidates against Core. Core also accepts
   this identity as `X-Docker-Host-Identity`. The API never trusts unsigned or
   client-set user headers or cookies. Proof exchange does not replace this second
   validation boundary.

The force-dynamic session probe awaits server-side recovery discovery and returns
`appId`, `corePublicOrigin` and `appAuthProtocol` with its session status and activity
metadata. Core `/api/auth/apps/protocol` discovery is uncached. Protocol 1 requires
a definite metadata 404 followed by a valid running `hosty-core` status with a
version below `0.120.0`; errors or malformed metadata fail closed. Once protocol 2
is observed for a configured Core origin, transient failures or older metadata
cannot downgrade it. The browser uses this app-local response instead of fetching
Core metadata across origins.

The SDK deduplicates callback exchange through React Strict Mode effect replay.
When attempt storage is unavailable, it skips silent recovery and full navigation;
an explicit popup retains proof in memory and fails closed if blocked. It never
downgrades to an unbound code exchange. Logout and explicit `token_invalid`,
`token_revoked`, `token_expired` or `token_app_mismatch` rejection clear stored and
in-memory grants; `reauth_required` retains the grant for renewal. Storage errors
leave the mounted document's in-memory grant usable, and stale probes cannot erase
a newer grant. The bridge keeps authenticated content mounted during renewal,
preserving the current page. A same-tab embedded reload restores its stored grant;
closing the tab ends that storage. Shell and other tabs do not supply credentials
to the app frame.

After validation, Media Server upserts an internal app user in SQLite. Hosty
admins map to Media Server `admin`; other assigned Hosty users map to Media
Server `user`. The app stores the Hosty user id and email on the internal user
row so it can re-link by unique email if Hosty user ids change.

Every Host-identity endpoint maps the caller onto that row through one shared
resolver (`MediaServer.Api.Hosty.HostIdentity`), which reads the Hosty user id
from the principal's `NameIdentifier` claim. It resolves to nothing both when the
claim is absent and when no internal user has been provisioned yet — an
authenticated caller can reach an endpoint before the upsert above has run — so
routes that need a user answer `401` rather than falling back to another user.

**Jellyfin clients (app-owned).** Infuse cannot perform the app-code flow, so the
`jellyfin` endpoint uses Media Server-owned credentials and opaque access tokens.
See [Jellyfin compatibility](../jellyfin-compatibility/feature.md) and [Security](../security/feature.md).

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
| `FFPROBE_PATH` | string | Path to the `ffprobe` binary on the host (dev profile). |

Public endpoint origins are configured after install through Core-managed
`HOSTY_PUBLIC_ORIGIN_{ENDPOINT_KEY}` settings (`HOSTY_PUBLIC_ORIGIN_UI`,
`HOSTY_PUBLIC_ORIGIN_JELLYFIN`); empty means use the local `localhost` endpoint.

The `dev` profile explicitly declares `development: true`. Core runs it from the selected source
folder, including a custom override, and adopts manifest edits on restart. The web service uses
Next.js hot reload; API source edits need a restart with the current `dotnet run` command.

## Sample Manifest

The authoritative manifest is `manifest.json` at the repo root.
[Implementation plan §4](../implementation-plan.md) keeps the original planning copy,
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
    { "key": "dev",    "type": "localCommand", "development": true }
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
  "settings": [ /* TMDB_API_KEY (secret, required), SUPPORTED_LANGUAGES, JELLYFIN_*, FFPROBE_PATH */ ],
  "capabilities": ["backup", "logs"]
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
  [Storage and data](../storage-and-data/feature.md)).

Details in [Storage and data](../storage-and-data/feature.md).

## Shell Embedding

The `web` UI runs inside the Hosty Shell sandboxed iframe. It must:

- use relative URLs or `HOSTY_CORE_PUBLIC_ORIGIN`, never hard-coded origins;
- keep client routing compatible with `ui.entrypoint.path`;
- avoid reading Host cookies, Host local storage, or the parent DOM;
- allow the SDK silent initial-load navigation only inside the initiating app
  frame, before content mounts; mounted-page renewal uses an app-owned popup
  opened directly by a user action, with message source, origin, state and local
  proof validation, preserving the page and avoiding frame busting;
- validate SignalR transport (WebSocket, SSE, long-polling fallback) through the
  Core-managed runtime, because behavior can depend on the embed route.

### Launch Mode

The root layout runs the app SDK's `launchModeBootstrapScript` in `<head>` and mounts
`HostLaunchBridge`, which resolve the launch mode — a shell-declared `hosty_launch`
query parameter (`embedded`, `native`, or `standalone`) first, then the value
persisted for the tab, then the frame heuristic — and stamp it onto `<html>` as
`data-hosty-launch`. The top tab bar (wordmark plus the manifest `ui.navigation`
pages) carries the SDK's `hosty-shell-chrome` class and is hidden by an unlayered
CSS rule whenever the mode is not `standalone`, because a surrounding shell already
renders that navigation. Session recovery follows the resolved embedded, native
or standalone launch mode: embedded initial-load silent recovery and popup
renewal, native intercepted proof navigation and correlated in-place completion,
or standalone guarded Core navigation. Shell does not mint or exchange app grants
on behalf of the frame.

## Capabilities

`["backup", "logs"]` — the optional features this app offers a client. Lifecycle
verbs (open/update/restart/stop/remove) are inherent to Core managing the app and
are not declared here.

## Local Validation

```bash
hosty core start
hosty apps install . --runtime dev
hosty apps start com.haas.media-server
hosty apps open com.haas.media-server --mode shell
```

Identity, Shell embedding, assignments, scoped directory, redirects, WebSockets,
and SSE must be checked through this Core-managed lifecycle. Standalone dev
servers can validate UI and business logic only. `apps open` carries no user
credential and has no `--user` option; normal Core browser login selects the user.
`apps identity --user` remains a diagnostic helper for direct API probes, not the
browser sign-in flow.

## Testing Expectations

- Unit coverage for `MediaServerSettings.FromConfiguration`: every declared
  manifest setting is read from its documented environment key, and each one
  falls back to the manifest default when the variable is absent or unparsable.
- Unit coverage for the injected-environment readers (`HostyOptions`, the
  `HOSTY_MOUNT_CATALOGROOTS` parser, `HOSTY_DEPENDENCY_*` discovery): a missing
  dependency URL degrades the feature instead of failing startup.
- Unit coverage for the shared Host identity resolver: it picks the internal user
  belonging to the calling Hosty user id, and resolves to nothing when the claim
  is absent or empty and when no internal user exists yet.
- The manifest itself is validated against the Core contract through the local
  lifecycle above (`hosty apps install . --runtime dev`); no automated test
  substitutes for it.
- After changes to the tab bar or the launch bridges, verify a plain tab shows
  the tab bar while `?hosty_launch=embedded` hides it, and that the parameter is
  cleaned from the URL.

- Cover the app identity probe status, asynchronous recovery protocol metadata
  and activity metadata, plus bearer transport for JSON and SSE requests and
  rejection of other origins. Verify protocol discovery errors fail closed and an
  observed protocol 2 never downgrades. The SDK owns callback exchange
  deduplication, effect replay, popup source/origin/state and local proof coverage.
- Cover local code-only or malformed-verifier 400 without a Core call, missing
  service-token 503, the service bearer and `redirect: "error"`. A wrong verifier
  returns 401 without consuming a real Core-issued code; the correct verifier
  succeeds once. Verify Origin-plus-nonce binding and silent blocked-cookie
  fallback through the Core-managed runtime.
- Verify the embedded popup returns access to the original frame with app cookies
  blocked. Check Dashboard → Home, Dashboard → Movies, and returning to the first
  page without another sign-in; reload the same embedded tab and verify its grant
  is restored. Cover storage exceptions, standalone no-grant-storage, logout,
  explicit rejected-token cleanup, `reauth_required` retention, stale probes and
  renewal without page loss.
- Retain web-to-API Core revalidation tests and the separation of Media-owned
  Jellyfin credentials from the Hosty app identity flow.
- Registry installation and production builds use the published SDK dependency
  and lockfile; a candidate-tarball build alone is not publication or deployment
  evidence.
