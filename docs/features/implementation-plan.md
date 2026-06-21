# Implementation Plan

Status: Active
Created: 2026-06-17
Updated: 2026-06-21

> **Storage-model change (2026-06-21).** The catalog storage model was reworked
> from two hardlinked subtrees (`files/` + `library/`) to a **single tree**: a
> transient `.incoming/` staging dir plus canonical media at the catalog root,
> with the playable file **moved** (not hardlinked) into place, the `Download`
> dropped at the download→identify hand-off, seeding only during download, and a
> per-catalog import **Scan**. Milestone narrative below that still mentions
> `files/`/`library/` hardlinks or seed-copy teardown predates this change and is
> kept as a historical record; the current model is in
> [Torrents and organizer](torrents-and-organizer.md).

## Purpose

This is the execution plan that turns the `docs/features/` specifications into a
buildable sequence of milestones and tasks. It locks the technology stack,
reconciles the specs against the **actual** Hosty Core contracts (verified
against the sibling `docker-host` repository on 2026-06-17), defines the
repository scaffold and a working manifest, and decomposes the roadmap (M0–M5)
into tasks with acceptance criteria and dependencies.

The specs (domain model, automation pipeline, Jellyfin compatibility, etc.) are
the source of truth for *what* to build; this document is the source of truth
for *in what order* and *against which platform reality*.

## 1. Locked Technology Stack

Confirmed for v1 (changes require updating this section and `docs/root.md`).

### Backend — `api` service
- **.NET 10 (LTS)** — matches Hosty Core (itself .NET 10, AOT). ASP.NET Core
  Minimal API.
- **EF Core over SQLite**, single embedded DB file under `HOSTY_APP_DATA_DIR`,
  JSON columns for flexible provider blobs.
- **MonoTorrent** as a hosted service (torrent engine).
- **ffprobe** for media probing (no transcoding in v1).
- **Server-Sent Events** for real-time job/download progress (server→client only; rides
  the same-origin BFF as a streaming HTTP response — no WebSocket upgrade, which the
  Next.js route-handler BFF can't proxy). Superseded the initial SignalR choice (2026-06-20).
- **xUnit + Imposter** for unit tests (per `AGENTS.md`).

### Frontend — `web` service
- **Next.js 15+ (App Router)**, TypeScript, **Tailwind**, **shadcn/ui**.
- **TanStack React Query** as the client data layer (chosen over SWR: the app is
  mutation- and event-driven — pipeline actions, review queue, manual match,
  delete — and React Query's `useMutation` + `invalidateQueries`/`setQueryData`
  integrate cleanly with SSE push updates).
- **Server-Sent Events** client (a `fetch` + `ReadableStream` reader, so the identity
  bearer can be attached — `EventSource` can't set headers).
- **pnpm** as the package manager.
- **Vitest** (unit) + **Playwright** (e2e) for frontend tests.

### Runtime & delivery
- Hosty runtime app, `schemaVersion: "app.0.1"`.
- Runtime profiles: `dev` (`localCommand`, primary dev loop) and `docker`
  (**v1 delivery target** — unblocked, see §2).
- GitHub Actions CI → images published to GHCR.

## 2. Hosty Core Contract Reality (verified against `docker-host`)

The specs were written defensively with workarounds because Core lacked several
capabilities. Most blockers are now implemented. This table is authoritative;
`hosty-platform-requests.md` should be updated to match (see §8).

| Capability | Status | Concrete contract | Consequence for us |
| --- | --- | --- | --- |
| External catalog-root mounts (#1) | **Implemented** | `externalMounts.catalogRoots` (`kind: host-path`, `multiple`, `mode: rw`, `service`, `required`). Injected `HOSTY_MOUNT_CATALOGROOTS`: docker → `/mnt/catalogRoots/{label}` (one bind per path), dev → host paths. | **Unblocks `docker`.** One root = one bind = one filesystem, so the move from `.incoming/` into the canonical tree is atomic. |
| Public ingress + TLS (#2) | **Implemented** | `HOSTY_INGRESS_PROVIDER=cloudflared` → `HOSTY_PUBLIC_ORIGIN_{KEY}=https://{sub}.{base}`; subdomain via `HOSTY_INGRESS_SUBDOMAIN`. | **Drop** the operator reverse-proxy workaround. Read `HOSTY_PUBLIC_ORIGIN_UI` / `_JELLYFIN`. |
| On-demand backup (#3) | **Implemented** | `POST /api/internal/apps/{appId}/backups` body `{ "note" }`, bearer `HOSTY_APP_SERVICE_TOKEN`; 201 completed / 200 empty. | Call before EF migrations. App must flush itself first (no quiesce hook). |
| Operator notifications (#5) | **Implemented** | `POST /api/internal/apps/{appId}/notifications` `{ target, audience, level, title, body, link, dedupeKey }`; levels info/success/warning/error; app may **not** use `host-admin` audience. | Use for migration failure, low disk, catalog offline. |
| App identity / session | **Implemented** | `POST /api/auth/apps/token {code}` → JWT (HS256, 24h); `POST /api/auth/apps/revalidate` (bearer service token). | JWT is **symmetric** — app cannot verify locally; must revalidate (cache with TTL). |
| Scoped user directory | **Implemented** | `GET /api/internal/apps/{appId}/directory/users` → assigned, enabled users only. | Map Host users → app users; poll for changes. |
| Intra-app service discovery (#14) | **Implemented** (Core source, merged 2026-06-17) | `dependsOn: ["api"]` makes Core inject `HOSTY_SERVICE_API_URL` into `web` → `api`'s first non-public port. docker: `http://api:8080` over a per-app user network (no host publish); dev: `http://localhost:{assigned}`. **Caveat:** the installed `0.4.0` release predates this — run Core from source (`hosty core start --project …/Haas.Hosty.Core.csproj`) until a release ships it. | `web` BFF reads `HOSTY_SERVICE_API_URL`; no port pinning, no public internal port. Distinct from cross-app `HOSTY_DEPENDENCY_*`. Verified end-to-end in M0. |
| Directory-change webhooks (#6) | **Absent** | — | Keep polling `directory/users`; revoke Jellyfin tokens on unassign/disable by diff. |
| Pre-backup quiesce hook (#4) | **Absent** | — | Keep WAL + periodic SQLite online-backup snapshot for scheduled/manual backups. |
| Raw TCP/UDP port (#8) | **Implemented** (Core source, verified 2026-06-17) | Per-port `expose: host` + `transport: [tcp, udp]`; `expose: host` requires a pinned `hostPort`. Publishes `0.0.0.0:host:container/proto`; injects `HOSTY_PORT_{KEY}` once. (Same release caveat as #14.) | media-server declares a pinned `torrent` port; operator forwards it on the router (no UPnP in Core). |
| Restore-time mount remap (#10), LAN discovery (#11) | **Absent** | — | App marks unreachable roots offline + rescan; manual server URL in Infuse. |

CLI (actual, verified against 0.4.0): `hosty apps install <dir> [--runtime <key>]`, `hosty apps start|stop|restart|health|logs|remove|backup|restore|update-plan|update <id>`, `hosty apps open <id> --user <email>`, `hosty apps identity <id> --user <email> [--format token|header|env|json]`, `hosty apps list`, `hosty users list [--app <id>]`. **No** `apps configure` / `apps mounts` subcommands exist in 0.4.0 — app **settings** and **external mounts** are configured post-install through the Shell UI (and are not enforced by Core at `start`).

## 3. Repository Scaffold (M0)

```text
manifest.json              # schemaVersion app.0.1 (repo root)
src/
  api/                     # .NET 10 solution (api service)
    MediaServer.Api/
    MediaServer.Api.Tests/ # xUnit + Imposter
  web/                     # Next.js app (web service), pnpm
.github/workflows/
  ci.yml
docs/                      # existing planning docs
```

`defaultRuntime` is `docker` (delivery intent); local development installs with
`--runtime dev`.

## 4. Working Manifest (grounded in the real `app.0.1` schema)

Modeled on `docker-host/apps/demo-app/manifest.json`. Two ports on `api` isolate
the internal management surface from the public Jellyfin surface.

```jsonc
{
  "schemaVersion": "app.0.1",
  "id": "com.haas.media-server",
  "name": "Media Server",
  "version": "0.1.0",
  "runtimeProfiles": [
    { "key": "docker", "type": "docker", "default": true },
    { "key": "dev",    "type": "localCommand" }
  ],
  "defaultRuntime": "docker",
  "services": [
    {
      "key": "api",
      "runtimes": {
        "docker": {
          "type": "docker",
          "image": { "repository": "ghcr.io/alex-de-haas/media-server-api", "tag": "latest", "pullPolicy": "always" },
          "ports": [
            { "key": "internal", "containerPort": 8080, "protocol": "http" },
            { "key": "jellyfin", "containerPort": 8096, "protocol": "http", "public": true },
            { "key": "torrent",  "containerPort": 6881, "hostPort": 6881, "expose": "host", "transport": ["tcp", "udp"] }
          ]
        },
        "dev": {
          "type": "localCommand",
          "workingDirectory": "src/api",
          "command": "dotnet run --project MediaServer.Api",
          "ports": [
            { "key": "internal", "containerPort": 8080, "protocol": "http" },
            { "key": "jellyfin", "containerPort": 8096, "protocol": "http", "public": true },
            { "key": "torrent",  "containerPort": 6881, "hostPort": 6881, "expose": "host", "transport": ["tcp", "udp"] }
          ]
        }
      }
    },
    {
      "key": "web",
      "dependsOn": ["api"],
      "runtimes": {
        "docker": {
          "type": "docker",
          "image": { "repository": "ghcr.io/alex-de-haas/media-server-web", "tag": "latest", "pullPolicy": "always" },
          "ports": [ { "key": "http", "containerPort": 3000, "protocol": "http", "public": true } ]
        },
        "dev": {
          "type": "localCommand",
          "workingDirectory": "src/web",
          "command": "pnpm dev",
          "ports": [ { "key": "http", "containerPort": 3000, "protocol": "http", "public": true } ]
        }
      }
    }
  ],
  "endpoints": [
    { "key": "ui",       "service": "web", "port": "http",     "protocol": "http", "public": true },
    { "key": "jellyfin", "service": "api", "port": "jellyfin", "protocol": "http", "public": true }
  ],
  "ui": { "entrypoint": { "endpoint": "ui", "path": "/" } },
  "data": {
    "enabled": true,
    "targets": [
      { "runtime": "docker", "service": "api", "containerPath": "/app/data", "environment": "HOSTY_APP_DATA_DIR" },
      { "runtime": "dev", "environment": "HOSTY_APP_DATA_DIR" }
    ]
  },
  "externalMounts": {
    "catalogRoots": { "kind": "host-path", "multiple": true, "mode": "rw", "service": "api", "required": true }
  },
  "settings": [
    { "key": "TMDB_API_KEY",                "type": "string",  "secret": true, "required": true },
    { "key": "SUPPORTED_LANGUAGES",         "type": "string",  "default": "en-US" },
    { "key": "JELLYFIN_SERVER_NAME",        "type": "string",  "default": "Media Server" },
    { "key": "JELLYFIN_DISCOVERY_ENABLED",  "type": "boolean", "default": "false" },
    { "key": "FFPROBE_PATH",                "type": "string" },
    { "key": "TORRENT_ENABLE_PORT_MAPPING", "type": "boolean", "default": "true" },
    { "key": "TORRENT_BIND_ADDRESS",        "type": "string" },
    { "key": "TORRENT_MAX_DOWNLOAD_SPEED",  "type": "number",  "default": "0" },
    { "key": "TORRENT_MAX_UPLOAD_SPEED",    "type": "number",  "default": "0" }
  ],
  "capabilities": ["open", "update", "restart", "stop", "remove", "backup", "restore", "logs"]
}
```

> ✅ Resolved in M0 (verified against `docker-host` 2026-06-17): `web` discovers
> the **internal** `api` URL via Core's intra-app service discovery
> ([platform request #14](hosty-platform-requests.md)). Because `web` declares
> `dependsOn: ["api"]`, Core injects `HOSTY_SERVICE_API_URL` into `web`, resolving
> to `api`'s first non-public port (`internal`). Under `docker` this is
> `http://api:8080` over a per-app user network (service-name DNS; the internal
> port is **not** host-published); under `dev` it is `http://localhost:{assigned}`
> over loopback. This is distinct from cross-app `HOSTY_DEPENDENCY_{KEY}_URL`
> (which resolves to another *app's* public endpoint). `web` reads
> `HOSTY_SERVICE_API_URL` for its BFF proxy — no port pinning, no public exposure
> of the management API.

## 5. Milestones

### M0 — Scaffold & platform integration
**Goal:** the two services boot under Core lifecycle, identity works, CI is green.
- Repo layout (§3); .NET solution; Next.js app (pnpm); `manifest.json` (§4).
- `api`: Minimal API skeleton, `/health`, EF Core + SQLite at `HOSTY_APP_DATA_DIR`,
  read `HOSTY_*` env (no hard-coded ports/paths), two ports.
- `web`: app-code exchange (`/api/auth/apps/token`), session = app-origin
  HttpOnly cookie **+ in-memory/`sessionStorage` bearer fallback** (cross-site
  iframe), revalidate via service token, BFF proxy to `api`, React Query +
  SignalR client wired.
- CI: restore/build/test .NET (xUnit), build web, validate manifest + dev commands.
- **Integration verification (de-risk early):** (a) `HOSTY_SERVICE_API_URL`
  injected into `web` from `dependsOn` (intra-app discovery, #14); (b)
  `HOSTY_MOUNT_CATALOGROOTS` injection; (c) `HOSTY_PUBLIC_ORIGIN_*` from cloudflared.

**Acceptance (met 2026-06-17):** `hosty apps install . --runtime dev` → `start` → `open --user …`
loads the UI inside the Shell; identity validates; `/health` green; CI passes.

### M1 — Ingest happy path (primary use case)
**Goal:** add a torrent + pick a catalog → fully published, playable item, no manual steps.
Depends on M0. Specs: [automation-pipeline](automation-pipeline.md),
[domain-model](domain-model.md), [torrents-and-organizer](torrents-and-organizer.md),
[catalogs](catalogs.md), [metadata](metadata.md), [background-tasks](background-tasks.md).
- Catalog model; read roots from `HOSTY_MOUNT_CATALOGROOTS`; validate each is a
  single filesystem (`st_dev`); `files/` + `library/` layout per root.
- Torrent engine (MonoTorrent) hosted service: add magnet/`.torrent`, download
  into `files/`, progress via SignalR.
- Organizer: hardlink `files/` → clean `library/` layout.
- Identify (TMDb, `TMDB_API_KEY`) → Probe (ffprobe, `FFPROBE_PATH`) → Publish to catalog/items.
- Orchestrator: staged pipeline with `StageResult` (Completed/NeedsReview/Deferred/Failed),
  backoff, resume. Jobs + SignalR hub; live activity UI.

**Acceptance:** a single operator action yields an identified, probed, published
library item; pipeline survives restart (resume).

**Status (implemented 2026-06-17):** backend complete and unit-tested (xUnit, 36
tests green): EF Core M1 schema + migration (WAL/busy_timeout, JSON columns,
`IngestItem` concurrency token); catalog CRUD with `st_dev`/hardlink-probe
same-filesystem validation, path sandbox, free space; MonoTorrent engine
(DHT/PEX/LSD/MSE, `HOSTY_PORT_TORRENT`) + coordinator (progress broadcast, state
transitions, restart resume); hardlink organizer; TMDb provider + scoring,
Emby-style/AnitomySharp name parsing, ffprobe; the seven `IPipelineStage`
processing stages + orchestrator (lease, `StagesCompleted` resume, backoff,
`NeedsReview`) + reconciler + jobs + realtime notifier; REST under
`/api/{catalogs,torrents,ingest,library}`. Web dashboard (React Query) covers
catalogs, add-torrent, live downloads, pipeline activity + review match, and the
published library. **Realtime transport (resolved 2026-06-20):** the initial SignalR
hub was replaced with **Server-Sent Events** — a single `GET /api/events` stream behind
Host identity, fanned out by `SseRealtimeNotifier` (behind the unchanged
`IRealtimeNotifier` seam), consumed by a `fetch`-stream client (`RealtimeBridge`) that
patches `downloadProgress` into the `downloads` cache and invalidates on coarser
transitions. This rides the same-origin BFF cleanly (the route-handler BFF can't proxy a
WebSocket upgrade, so SignalR would have degraded to SSE/long-poll there anyway); React
Query keeps slow `refetchInterval`s only as a reconnect fallback.

### M2 — Jellyfin Direct Play
**Goal:** Infuse connects, browses, plays. Depends on M1.
Specs: [jellyfin-compatibility](jellyfin-compatibility.md), [security](security.md).
- `jellyfin` surface: System / Users / UserViews / Items / Images, `PlaybackInfo`,
  range-based (`206`) direct streaming.
- App-owned native-client auth (PIN + opaque tokens, argon2id, rate limit/lockout).
- Use `HOSTY_PUBLIC_ORIGIN_JELLYFIN` (cloudflared).

**Acceptance:** Infuse pairs, browses the library, and Direct Plays over the
public origin with working seek (range requests).

**Status (implemented 2026-06-18):** backend complete and unit-tested (xUnit, 70
tests green) on branch `feat/m2-jellyfin`. Adds: M2 EF schema + migration
(`JellyfinCredentials`, `JellyfinAccessTokens`); app-owned native-client auth —
argon2id PIN hashing (Konscious), opaque SHA-256-at-rest tokens, consecutive-failure
lockout (temporary at 10 with a growing window, permanent at 100), per-IP rate
limit on `AuthenticateByName`; a second ASP.NET auth scheme (`Jellyfin`) validating
tokens locally from the `MediaBrowser`/`Emby` header, `X-Emby-Token`, or (media/image
only) `api_key` query; PascalCase DTO serialization isolated from the camelCase
`/api`; `JellyfinItemMapper` + `JellyfinLibraryService` (collection folders, movies,
series/season/episode hierarchy with resolved parent links/child counts, localized
metadata, image tags, provider ids, `MediaSourceInfo`/`MediaStream`, user-data keys);
image proxy/cache; range-based (`206`/`HEAD`/`If-Range`/`416`) direct streaming
confined to catalog roots via the sandbox; the System/Users/Sessions/Library/Views/
Items/Shows/Images/PlaybackInfo/Videos endpoint set; internal `/api/jellyfin/credential`
UI endpoints + a minimal dashboard "Infuse access" card. Reads
`HOSTY_PUBLIC_ORIGIN_JELLYFIN` as the server URL. Playback state (`Sessions/Playing*`,
Resume/NextUp) is deferred to M3 — those routes return empty results for now. The
internal `/api` and Jellyfin surfaces are separated by auth scheme (both reachable on
both ports); end-to-end validation against Infuse over the public origin remains a
manual step.

### M3 — Playback state
**Goal:** resume & watched state. Depends on M2.
- `Sessions/Playing*`, per-user data, resume position, watched threshold,
  season/series aggregates.

**Acceptance:** progress reported by Infuse persists; resume and watched flags
behave; series/season rollups correct.

**Status (implemented 2026-06-18):** backend complete and unit-tested (xUnit, 85
tests green, Release) on branch `feat/m3-playback-state`. Adds: M3 EF schema +
migration (`UserItemData`, unique on `(AppUserId, MediaItemId)`, keyed to the
**internal** `MediaItem.Id` so it survives rescans/public-id remaps); a
`UserDataService` that (a) projects `UserItemData` into `UserItemDataDto` for item
batches — movies/episodes carry their own row (resume ticks, play count, watched,
favorite, `PlayedPercentage`); season/series folders carry a rollup computed on
read from descendant episodes (`Played` when all children played, `UnplayedItemCount`,
watched `PlayedPercentage`, max child `LastPlayedDate`, own-row favorite) — and (b)
applies the resume/watched policy on `Sessions/Playing*`: crossing 90% of runtime
marks watched + clears the resume point and bumps `PlayCount` (once per watch),
stops below 5% discard the resume point, opening a watched item from `0` keeps it
watched, and a fresh in-progress position clears the watched flag. Endpoints:
`POST /Sessions/Playing[/Progress|/Stopped]`, `POST|DELETE
/Users/{userId}/{PlayedItems,FavoriteItems}/{itemId}` (returns updated user data;
folder mark-played recurses to episodes), and the previously-empty
`/Users/{userId}/Items/Resume` (in-progress, newest first) + `/Shows/NextUp` (first
unwatched episode of each started series) wired to real data. Every browsing DTO now
carries the caller's `UserData` (the library service threads the authenticated app
user id; `/Users/{userId}/…` routes resolve the acting user, admins may act on
others). End-to-end validation against Infuse over the public origin remains a manual
step.

### M3.5 — App shell & UI redesign
**Goal:** turn the single-page functional dashboard into a navigable, themed,
multi-page application with real browse/detail pages. Depends on M1–M3;
independent of and parallelizable with M4. Specs:
[frontend-application](frontend-application.md). Design decisions recorded
2026-06-18.

**Architecture principle (decided 2026-06-18):** the `web` UI consumes **only** the
internal `/api` surface (camelCase, Hosty identity) through the BFF. The Jellyfin
surface is a **content-provider adapter** for external native players (Infuse) that
sits *beside* the UI, not beneath it — it may be swapped for another protocol later,
so nothing in `web` may couple to Jellyfin DTOs or endpoints. Both surfaces project
from a shared, surface-neutral domain/read layer (the EF domain + the playback-state
service); they are siblings, not a dependency chain. Concretely: relocate the
playback-state service (`UserDataService`) and its DTO out of the `Jellyfin`
namespace into a neutral location, and add a UI-facing library/detail read service
that returns UI DTOs — `JellyfinLibraryService` stays the Jellyfin adapter and the
UI never touches it.

- **Shell skeleton (no new features):** light + dark shadcn token sets in
  `globals.css`; port the Hosty Shell theme bridge (initial `hosty_theme` URL params
  + `hosty:shell-theme` postMessage, contract verified against `docker-host`); top
  tab bar (Home/Movies/Series/Downloads/Activity + a right-aligned admin menu for
  Catalogs/Settings); App Router routes compatible with `ui.entrypoint.path: "/"`
  surviving refresh/direct nav; split the current `components/dashboard.tsx` into
  per-page components (straight move, behavior unchanged).
- **`/api` for the UI (backend):** expand the internal surface beyond the slim
  `LibraryItemResponse` — movie/series detail, season/episode listings, media streams
  (resolution/codec/audio), and per-user playback state (resume/watched/favorite) —
  projected from the domain via the shared read layer, **not** from Jellyfin DTOs.
- **Visual language & typography (before detail pages):** decide and implement the
  visual language — font family + type scale, radius/density, spacing rhythm,
  iconography conventions (lucide), and a component-styling baseline on the shadcn
  `base-nova` tokens. The Shell theme bridge inherits **colors only** (light/dark),
  **not fonts**, so typography is the app's own decision. (The scaffold's `--font-sans`
  self-reference is already fixed so Geist Sans applies; the deliberate font choice is
  made here.) Detail pages build on top of this, not before it.
- **Browse + detail pages:** Movies/Series grids (infinite scroll) → detail pages
  (backdrop hero, media info, watched/favorite toggles, source-file remap, delete);
  Home rails (Continue Watching / Next Up / Recently Added) + an admin-only ops strip
  (active downloads, items needing review, catalog warnings). **No in-browser
  player** — Play deep-links to an Infuse/Jellyfin client.
- **Polish & tests:** empty/loading/error states; role-gating for admin surfaces;
  Vitest + Playwright for routing (refresh/direct nav), role gating, and the
  Shell-iframe embed, per the spec's testing expectations.

**Acceptance:** the app navigates as a multi-page UI inside the Shell, follows the
host light/dark theme, renders movie/series detail and Home rails from the internal
`/api` only (zero coupling to the Jellyfin surface), and Play opens an external
client.

**Open risk (resolved 2026-06-20):** Infuse deep-link from the Shell iframe — the embed
sandbox is `allow-scripts allow-same-origin allow-forms allow-popups allow-downloads` (no
`allow-top-navigation`), so the deep link is launched via `window.open` (the granted
`allow-popups`), with a copy-link fallback. Implemented with Infuse's TMDb library deep
links; end-to-end behaviour on a device with Infuse installed remains a manual check.

**Status (in progress 2026-06-18, branch `feat/m3.5-app-shell`):** slice 1 (app
shell — light/dark tokens, Hosty theme bridge, top-tab routing, `dashboard.tsx` split
into per-page components; web build/lint/vitest green) and slice 2 (backend `/api`
read layer) are done. Slice 2 moved `UserDataService` + `UserItemDataDto` into the
surface-neutral `MediaServer.Api.Library`, added `LibraryReadService` + UI DTOs, and
expanded `/api/library` with detail (`GET /{id}`), episodes (`GET /{id}/episodes`),
media streams, and per-user playback state — projected from the shared domain with
**zero coupling to the Jellyfin DTOs** (the Jellyfin surface stays a sibling adapter).
api: 92 xUnit tests green (Release). Slice 3 (visual language) is done: Inter (UI) +
Fraunces serif (media titles) + Geist Mono, a single amber "projector" brand accent
(`--brand`, used for resume/progress/active-nav/favorites) over the inherited neutral
theme, lucide icons + a brand active-tab underline, and serif card titles with an amber
resume bar / watched badge — "content speaks serif, the app speaks sans". Slice 4
(detail pages + Home rails) is done: `/movies/[id]` & `/series/[id]` detail (backdrop
hero with the serif title + amber resume, watched/favorite toggles, Play→Infuse, media
streams, per-season episodes), Home rails (Continue Watching / Next Up / Recently
Added) + an admin ops strip, and `/api` rails (recent/resume/nextup) + id-keyed
played/favorite mutations (also fixed a SQLite `DateTimeOffset` ORDER BY crash latent
in the Jellyfin "Latest" path). api: 96 xUnit tests green; web build/lint/tsc/vitest
green. Slice 5 (polish) in progress: empty/loading/error states (a shared `QueryState`)
across the library grid, downloads, activity, and catalogs, plus real role-gating — an
`Admin` authorization policy (`AppRoles.AdminPolicy`) guards catalog writes server-side
and the Catalogs page/tab is admin-only (`AdminOnly`) — are done, as is admin-gated
**delete** from the detail page (`LibraryDeleteService` + `DELETE /api/library/{id}`;
two modes — remove-from-library vs delete + remove the `library/` hardlinks, with the
download/`files/` left untouched; an in-app confirm since the Shell sandbox blocks
`window.confirm`). api 105 xUnit tests green; web build/lint/tsc green. **Playwright
e2e is done** too: a mocked-BFF suite in `web/e2e/` (8 specs — shell routing/refresh,
admin-vs-user gating, empty/error states, detail navigation + the watched mutation)
wired into a CI `web-e2e` job. **Source-file remap is now done** (2026-06-20): an admin
corrects a misidentified published leaf from the detail page — "Fix match…" on a movie,
a per-row affordance on an episode — by searching a corrected identity
(`POST /api/metadata/search`) and picking it; the backend `RemapService`
(`POST /api/library/{id}/remap`) reassigns the `MediaSource`, rebuilds the clean
`library/` hardlink **from the surviving library file** (the `files/` seed copy + the
`SourceFile` rows are already reclaimed once published, so the link is rebuilt from the
existing inode, not from `files/`), re-enriches + mints the public id of the corrected
target, and prunes the orphaned old item plus any emptied season/series. **The precise
Infuse deep-link is also done** (2026-06-20): Play uses Infuse's TMDb library deep links
(`infuse://movie/{id}` auto-play, `infuse://series/{id}`, `infuse://series/{id}-{s}-{e}?play`
per episode), so Infuse resolves the item against the user's connected Jellyfin source —
no token or direct stream URL needed. The id is exposed via `LibraryDetailDto.TmdbId` /
`EpisodeDto.SeriesTmdbId`; the launch goes through `window.open` (the Shell sandbox grants
`allow-popups` but not `allow-top-navigation`) with a copy-link fallback when the popup is
blocked or Infuse is absent. api 173 xUnit tests green; web lint/tsc/vitest + 10 Playwright
e2e green. M3.5 is now fully closed — no deferred items remain.

### M4 — Automation polish & Docker delivery
**Goal:** robustness + production container delivery. Depends on M1–M3.
- Reconciler, retries, review queue, manual match override, scheduled scans,
  metadata refresh.
  - **Re-search by corrected title (gap noted 2026-06-18, deferred here from M3.5):**
    when Identify returns **zero candidates** or the auto-parsed name is wrong (e.g.
    `Project.Hail.Mary.rus.LostFilm.TV.avi`), the review panel can today only `Retry`
    the same filename — a dead end. Add an inline metadata re-search: an operator types
    a corrected title (+ optional year) in the `NeedsReview` panel, we expose the
    existing `IMetadataProvider.SearchAsync` (new `/api/ingest/{id}/search` or
    `/api/metadata/search`), render the returned `MetadataCandidate`s with the same
    pick-to-`/match` flow already in `ReviewPanel`. **Scope:** metadata search only —
    the `library/` filename keeps deriving from the chosen metadata; no manual
    file-name/path override.
- Backups: on-demand backup before EF migrations (`POST …/backups`); WAL +
  periodic online-backup snapshot for scheduled/manual (no quiesce hook).
- Notifications (`POST …/notifications`) for migration failure / low disk /
  catalog offline; resolve via `dedupeKey`.
- Directory reconcile by polling (no webhooks): revoke Jellyfin tokens on
  unassign/disable.
- **Docker delivery:** Dockerfiles for `api` (ffprobe in image) + `web`; GHCR
  publish in CI; install via `--runtime docker`; verify mounts, ingress, and the
  torrent port decision (§7).

**Acceptance:** install/run under `docker` profile end-to-end (catalog mounts,
public origins, backups, notifications) matches the `dev` behavior.

**Status (implemented 2026-06-18, branch `feat/m4-automation-docker`):** all seven
slices done; api 130 xUnit tests green (Release), web lint/tsc/vitest + 8 Playwright
e2e green, manifest validates, both docker images build and boot. Adds: (1) a typed
`IHostyCoreClient` over Core's internal app APIs (`/backups`, `/notifications`,
`/directory/users`) with the service-token bearer, no-op when not Core managed;
contracts verified against `docker-host`. (2) On-demand Core backup before EF
migrations (only when migrations are pending) + a periodic SQLite online-backup
snapshot (`SqliteSnapshotService`/`DatabaseSnapshotWorker`, atomic `.snapshot`
beside the DB) so quiesce-less host backups capture a consistent copy; migration
failure raises an operator notification and rethrows. (3) `CatalogHealthService`/
`CatalogHealthWorker` persists `Catalog.OfflineSince`/`LowDiskSince` (migration
`M4CatalogHealth`) and emits deduped operator notifications on the offline→online
and low-disk transitions (broadcast, audience `user`; apps can't target
host-admin). (4) `DirectoryReconcileService`/`DirectoryReconcileWorker` polls the
scoped directory (no webhooks), upserts `AppUser`s (host.admin→Admin) and revokes
the Jellyfin credential+tokens of any app user no longer assigned/enabled. (5)
Re-search by corrected title: `POST /api/ingest/{id}/search` exposes
`IMetadataProvider.SearchAsync` (kind defaults from the catalog), wired into the
review panel's existing pick-to-`/match` flow (metadata search only). (6)
`LibraryMaintenanceService` — scheduled scan for missing library files (skips
offline catalogs, deduped notification) via `POST /api/library/scan` +
`LibraryScanWorker`, and on-demand metadata refresh `POST /api/library/{id}/refresh`
(admin), surfaced as a "Refresh metadata" button on the detail page. (7) Docker
delivery: multi-stage Dockerfiles for `api` (.NET 10 runtime + ffprobe; binds both
ports via `ASPNETCORE_URLS`) and `web` (pnpm + gated Next standalone output, off by
default so `next start`/e2e stay clean), `.dockerignore`s, and a `publish.yml` GHCR
workflow (push to main → `:latest`, tags → `:vX.Y.Z`). End-to-end install under the
`docker` profile via Core (mounts/ingress/torrent port) remains a manual on-device
step.

### M5 — Watchlist & discovery (future)
Per [watchlist-and-discovery](watchlist-and-discovery.md); deferred. M6 (MCP/AI)
remains future.

## 6. Cross-cutting

- **Testing:** backend xUnit + Imposter (mock TMDb, filesystem, torrent engine);
  frontend Vitest (unit) + Playwright (e2e). Host-facing behavior (identity,
  Shell embed, SignalR, public endpoints) validated through Core lifecycle, not
  forged tokens.
- **CI:** build both services + backend tests on every PR; image publish gated to
  M4 / docker enablement.
- **Observability:** structured logs; secrets (`TMDB_API_KEY`) redacted.
- **Security:** `api` trusts only Core-revalidated identity, never client-set
  headers/cookies; app-owned Jellyfin tokens hashed at rest.

## 7. Open Risks & Decisions

1. **Torrent inbound port under docker — resolved (2026-06-17).** Core is gaining
   a minimal opt-in per-port extension (`expose: host` + `transport: [tcp, udp]`)
   so a docker app can publish a stable raw L4 port on all interfaces; tracked in
   the `docker-host` repo. media-server declares a pinned `torrent` port (§4) and
   reads `HOSTY_PORT_TORRENT`; the operator forwards it on the router (no UPnP in
   Core). Dependency: the Core change must land before M4 docker delivery.
2. **`web` → `api` internal URL — resolved (2026-06-17).** Core injects
   `HOSTY_SERVICE_API_URL` into `web` from its `dependsOn: ["api"]` (intra-app
   service discovery, [request #14](hosty-platform-requests.md)); verified against
   `docker-host`. No fallback needed.
3. **Backup consistency without a quiesce hook** — WAL + periodic online-backup snapshot.
4. **Cloudflare tunnel for `jellyfin` Range streaming** — validate throughput and
   `206` pass-through for large files with Infuse during M2.
5. **HS256 identity tokens** — no local verification; revalidate against Core with
   a short cache TTL.

## 8. Documentation Reconciliation (done in this PR)

- `hosty-platform-requests.md`: #1, #2, #3, #5 marked **Implemented**; #8 marked
  **Planned** (raw-port extension); #4, #6, #10, #11 remain outstanding.
- `hosty-runtime-app.md`, `build-and-deployment.md`, `root.md`: `docker` is the v1
  delivery target (no longer deferred); the sample manifest uses the array-based
  `services`/`endpoints` shape of the real `app.0.1` schema; React Query pinned.
- **Torrent listen port — single source of truth:** the Hosty-injected
  `HOSTY_PORT_TORRENT` (from the manifest's pinned `torrent` port). The old
  `TORRENT_LISTEN_PORT` app setting is removed across the docs.

Still open (handled when the code is written, not a doc bug): the retired
`X-Docker-Host-Identity` header reference becomes JWT-forward + revalidate during
M0/M2 auth work.
