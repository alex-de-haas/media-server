# Implementation Plan

Status: Draft
Created: 2026-06-17
Updated: 2026-06-17

## Purpose

This is the execution plan that turns the `docs/planning/` specifications into a
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
- **SignalR** for real-time job/download progress.
- **xUnit + Imposter** for unit tests (per `AGENTS.md`).

### Frontend — `web` service
- **Next.js 15+ (App Router)**, TypeScript, **Tailwind**, **shadcn/ui**.
- **TanStack React Query** as the client data layer (chosen over SWR: the app is
  mutation- and event-driven — pipeline actions, review queue, manual match,
  delete — and React Query's `useMutation` + `invalidateQueries`/`setQueryData`
  integrate cleanly with SignalR push updates).
- **SignalR JavaScript client**.
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
| External catalog-root mounts (#1) | **Implemented** | `externalMounts.catalogRoots` (`kind: host-path`, `multiple`, `mode: rw`, `service`, `required`). Injected `HOSTY_MOUNT_CATALOGROOTS`: docker → `/mnt/catalogRoots/{label}` (one bind per path), dev → host paths. | **Unblocks `docker`.** Hardlinks work: one root = one bind = one filesystem; `files/`+`library/` are subdirs of it. |
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
apps/media-server/
  manifest.json            # schemaVersion app.0.1
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
          "workingDirectory": "apps/media-server/api",
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
          "workingDirectory": "apps/media-server/web",
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

**Acceptance (met 2026-06-17):** `hosty apps install apps/media-server --runtime dev` → `start` → `open --user …`
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

### M2 — Jellyfin Direct Play
**Goal:** Infuse connects, browses, plays. Depends on M1.
Specs: [jellyfin-compatibility](jellyfin-compatibility.md), [security](security.md).
- `jellyfin` surface: System / Users / UserViews / Items / Images, `PlaybackInfo`,
  range-based (`206`) direct streaming.
- App-owned native-client auth (PIN + opaque tokens, argon2id, rate limit/lockout).
- Use `HOSTY_PUBLIC_ORIGIN_JELLYFIN` (cloudflared).

**Acceptance:** Infuse pairs, browses the library, and Direct Plays over the
public origin with working seek (range requests).

### M3 — Playback state
**Goal:** resume & watched state. Depends on M2.
- `Sessions/Playing*`, per-user data, resume position, watched threshold,
  season/series aggregates.

**Acceptance:** progress reported by Infuse persists; resume and watched flags
behave; series/season rollups correct.

### M4 — Automation polish & Docker delivery
**Goal:** robustness + production container delivery. Depends on M1–M3.
- Reconciler, retries, review queue, manual match override, scheduled scans,
  metadata refresh.
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
