# Hosty Platform Requests

Status: Implemented
Created: 2026-06-15
Updated: 2026-06-21

## Description

Capabilities Media Server needs that Hosty Core does not (yet) provide. Each entry
is a small spec: the **problem** it solves for Media Server, a **proposed
contract** (illustrative — exact shapes follow the `app.0.1` schema and existing
Core conventions), **how the app uses it**, the **current workaround** and its
limits, and **acceptance criteria**. These are *platform* feature requests, kept
separate from the app's own backlog so the dependency on Hosty is explicit.

Contract sketches reuse existing Core conventions: app-authenticated calls go to
`{HOSTY_CORE_ORIGIN}/api/internal/apps/{appId}/...` with
`Authorization: Bearer <HOSTY_APP_SERVICE_TOKEN>`; runtime values are injected as
`HOSTY_*` env vars; per-app config is declared in the manifest.

Priority:

- **Blocking** — a milestone cannot ship as intended without it.
- **High** — strong correctness/operability win; the workaround is fragile.
- **Medium** — removes per-app boilerplate or a manual operator step.
- **Low** — convenience.

---

## 1. External host-path mount model for catalog roots — Implemented (2026-06-17)

**Status.** Implemented in Hosty Core as `externalMounts` (`kind: host-path`,
`multiple`, `mode`, `service`, `required`); configured paths are injected as
`HOSTY_MOUNT_{KEY}` as comma-joined `label=path` entries (docker → `/mnt/{key}/{label}`
binds, dev → host paths). This unblocks the `docker` runtime profile, now the v1
delivery target.

**Problem (historical).** Catalog roots are large media folders that must live
outside app data, be configured by the operator after install, survive app
update/restart/remove/runtime-switch, and never be backed up or deleted by Hosty.
Each root must be a single filesystem (a completed file is moved into the
canonical tree within the one root, an atomic zero-copy move).

**Proposed contract.** A manifest-declared external-mount capability plus a
Core-managed, operator-configured set of host-path → container-path binds:

```jsonc
// manifest
"externalMounts": {
  "catalogRoots": { "kind": "host-path", "multiple": true, "mode": "rw", "service": "api" }
}
```

Core injects the active binds at runtime as comma-joined `label=path` entries, e.g.
`HOSTY_MOUNT_CATALOGROOTS=movies-4k=/srv/movies-4k,anime=/srv/anime` (under `docker`
these are bind mounts; under `dev` they are the configured host paths read directly).
The label is the per-bind key Media Server forwards to the torrent-engine (`mountLabel`)
so it picks the downloads mount on the same host path — see
[Torrent engine app](../ideas/torrent-engine-app.md).

**How Media Server uses it.** Reads the injected roots and uses a transient
`.incoming/` staging dir plus the canonical media tree under each for download and
move/organize (one filesystem, so the move is atomic).

**Workaround.** `dev`/`localCommand` only, reading operator host paths directly —
blocks `docker` delivery.

**Acceptance criteria.**
- Operator can add/edit/remove catalog-root binds after install.
- Binds persist across update, restart, and runtime-switch.
- App removal leaves external media intact.
- Read-write; each bind is one mount point (so an atomic move works within it).
- Active binds injected under a stable env/contract.

## 2. External ingress with managed TLS for public endpoints — Implemented (2026-06-17)

**Status.** Implemented via `HOSTY_INGRESS_PROVIDER=cloudflared`: Core derives a
public origin per public endpoint and injects `HOSTY_PUBLIC_ORIGIN_{KEY}`
(`https://{subdomain}.{baseDomain}`, TLS terminated by Cloudflare; subdomain
overridable via `HOSTY_INGRESS_SUBDOMAIN`). The operator reverse-proxy workaround
is no longer needed.

**Problem (historical).** Native clients (Infuse) hit the public `jellyfin` endpoint directly,
and Hosty provides no external ingress, so the operator must run their own reverse
proxy and TLS. TLS also matters for the UI app-origin cookie (`SameSite=None;
Secure` needs HTTPS) inside the cross-site Shell iframe.

**Proposed contract.** A Core-managed public ingress that terminates TLS
(ACME-issued or operator-supplied cert), routes an external hostname to a declared
`public` endpoint, and sets `HOSTY_PUBLIC_ORIGIN_{ENDPOINT_KEY}` automatically. The
manifest already declares the public endpoints (`ui`, `jellyfin`).

**How Media Server uses it.** No change to app code — it already reads
`HOSTY_PUBLIC_ORIGIN_UI` / `HOSTY_PUBLIC_ORIGIN_JELLYFIN`. The manual reverse proxy
disappears.

**Workaround.** Operator-run reverse proxy + manually set `HOSTY_PUBLIC_ORIGIN_*`.

**Acceptance criteria.**
- Each `public` endpoint gets a stable external HTTPS origin, injected as
  `HOSTY_PUBLIC_ORIGIN_*`.
- TLS auto-provisioned and renewed.
- WebSocket/SSE pass-through preserved (SignalR on `ui`).
- HTTP `Range`/`206` streaming preserved and not whole-response buffered
  (`jellyfin`).

## 3. App-callable on-demand backup API — Implemented (2026-06-17)

**Status.** Implemented: `POST /api/internal/apps/{appId}/backups` (bearer
`HOSTY_APP_SERVICE_TOKEN`, optional `{ "note" }`) returns `201` completed / `200`
empty. The app calls it before applying EF Core migrations. There is still no
pre-backup quiesce hook (see #4), so the app must flush/checkpoint itself first.

**Problem (historical).** Before applying EF Core migrations on startup, the app wants a
recoverable snapshot. Today backups are only `manual`/`scheduled`/`pre-update`/
`pre-restore`/`pre-runtime-switch`, none app-initiated.

**Proposed contract.**

```text
POST {HOSTY_CORE_ORIGIN}/api/internal/apps/{appId}/backups
Authorization: Bearer <HOSTY_APP_SERVICE_TOKEN>
{ "reason": "pre-migration" }
→ 201 { "backupId": "...", "status": "completed" }   // or a poll/await handle
```

**How Media Server uses it.** On startup, if migrations are pending, call this,
await success, then run `Database.Migrate()`.

**Workaround.** None — apply migrations with no pre-migration backup, relying on
Hosty's `pre-update` backup (covers the update path, not migrations applied outside
an update).

**Acceptance criteria.**
- App can trigger a backup with its service token.
- Caller can await completion (sync result or pollable status).
- Resulting backup appears in the normal backup list and is restorable.
- Failures are reported distinctly so the app can refuse to migrate.

## 4. App-facing pre-backup quiesce/flush hook — High

**Problem.** A `manual`/`scheduled` backup copies the data directory while the app
is mid-write; the live SQLite file may be inconsistent at copy time. There is no
chance for the app to checkpoint/quiesce.

**Proposed contract.** A pre-backup lifecycle callback declared in the manifest and
invoked by Core before any backup:

```jsonc
"hooks": { "preBackup": { "service": "api", "path": "/internal/hooks/pre-backup" } }
```

Core calls it, waits for an ACK up to a bounded timeout, then proceeds.

**How Media Server uses it.** The handler runs `PRAGMA wal_checkpoint(TRUNCATE)` /
an online-backup snapshot and returns `200`.

**Workaround.** WAL mode + a periodic SQLite Online Backup snapshot file kept inside
the data directory, so any copy contains a known-good database.

**Acceptance criteria.**
- Hook invoked before `manual`/`scheduled`/`pre-*` backups.
- Bounded timeout; backup proceeds on ACK or timeout.
- Documented request/response contract and authentication.

## 5. Operator notification/alert API — Implemented (2026-06-17)

**Status.** Implemented: `POST /api/internal/apps/{appId}/notifications`
(`target`, `audience`, `level` ∈ info/success/warning/error, `title`, `body`,
`link`, `dedupeKey`; apps may not target the `host-admin` audience). Replaces the
in-app-banner-only workaround.

**Problem (historical).** The app needs to surface actionable conditions to the operator even
when they are not currently in the app: migration failure ("restore app data"),
a magnet that will not fit free space, a catalog gone offline.

**Proposed contract.**

```text
POST {HOSTY_CORE_ORIGIN}/api/internal/apps/{appId}/notifications
Authorization: Bearer <HOSTY_APP_SERVICE_TOKEN>
{ "level": "error", "title": "...", "body": "...", "dedupeKey": "...", "actionPath": "/activity" }
```

Shown in the Shell notification surface; `dedupeKey` collapses repeats and supports
resolve/clear.

**How Media Server uses it.** Posts alerts on the conditions above and resolves them
when the condition clears.

**Workaround.** In-app UI banners only — visible only while the operator is in the
app.

**Acceptance criteria.**
- App posts with its service token.
- Levels `info`/`warn`/`error`; dedupe + resolve.
- Surfaced in the Shell; optional deep-link back into the app
  (`ui.entrypoint.path` + `actionPath`).

## 6. User-directory change push/webhooks — High

**Problem.** The app maps Hosty users to internal users and issues Jellyfin tokens.
When a user is unassigned, disabled, or changes email, tokens should be revoked
promptly; today the app only learns this by polling the scoped directory and
revalidating at login/issuance.

**Proposed contract.** A subscription to directory-change events for the app, with
Core POSTing signed events to a declared endpoint:

```jsonc
"hooks": { "directoryEvents": { "service": "api", "path": "/internal/hooks/directory" } }
// events: user.assigned | user.unassigned | user.disabled | user.updated{email}
```

**How Media Server uses it.** On `unassigned`/`disabled`, revoke that user's Jellyfin
tokens and disable the `AppUser`; on `updated`, re-link by unique email.

**Workaround.** Poll directory + revalidate only at login/issuance → tokens stay
valid until the next validation point.

**Acceptance criteria.**
- Signed events authenticated as Core.
- At-least-once delivery with a poll/reconcile fallback for missed events.
- Covers assign / unassign / disable / email-change.

## 7. Reusable native-client auth primitive — Medium

**Problem.** Clients that cannot perform the app-code flow (Infuse) force every app
to hand-roll credential storage, opaque tokens, brute-force protection, and lockout.

**Proposed contract.** A Hosty primitive for native-client pairing bound to a Hosty
user — e.g. short-lived pairing codes or per-device tokens issued by Core, with
built-in rate limiting and temporary/permanent lockout — that the app exchanges for
its own session.

**How Media Server uses it.** Delegate PIN/lockout to the primitive instead of
maintaining argon2id PINs and failure counters locally.

**Workaround.** App-owned credential + token store with local argon2id hashing,
rate limiting, and temporary/permanent lockout (already specced in
[Security](security.md)).

**Acceptance criteria.**
- Per-user/per-device credentials with revocation.
- Configurable lockout (temporary + permanent) shared across apps.
- Tokens hashed at rest.
- *Confidence note:* may belong in the app rather than the platform; raise only if
  the pattern recurs across apps.

## 8. Raw L4 (TCP/UDP) port allocation and forwarding declaration — Implemented (2026-06-17)

**Status.** Implemented in `docker-host` as a minimal opt-in per-port extension
(`expose: host` + `transport: ["tcp", "udp"]` on a manifest port); `expose: host`
requires a pinned `hostPort`. Core publishes `0.0.0.0:host:container/proto` and
injects `HOSTY_PORT_{KEY}` once. Router port-forwarding stays the operator's
responsibility (no Core-managed UPnP). The currently installed `0.4.0` release
predates this merge, so docker delivery needs a Core build that includes it.

> **Update (2026-06-24).** media-server no longer consumes this primitive: the
> torrent engine was extracted into the standalone `torrent-engine` app, which now
> owns the raw listen port (behind its VPN). The capability is still required by the
> platform — it is just the `torrent-engine` app, not media-server, that declares the
> pinned raw port. See [Torrent engine app](../ideas/torrent-engine-app.md).

**Problem.** The torrent engine needs a stable raw listen port for peer
connectivity and DHT, ideally with router port mapping. Hosty only manages and
proxies HTTP endpoints.

**Proposed contract.**

```jsonc
"rawPorts": {
  "torrent": { "service": "api", "protocol": ["tcp", "udp"], "public": true, "portMapping": "upnp" }
}
```

Core allocates/pins the port, injects `HOSTY_RAWPORT_TORRENT`, and optionally
requests host-level UPnP/NAT-PMP mapping.

**How Media Server uses it.** Binds the injected port; relies on Core for mapping
instead of app-side UPnP.

**Workaround (pre-extension).** A fixed app-setting listen port + operator
port-forward + app-side UPnP/NAT-PMP.

**Acceptance criteria.**
- Stable raw port injected and persistent across restart.
- Optional host-managed port mapping.
- Documented as a non-HTTP listener, distinct from proxied endpoints.

## 9. First-class embedded-app session for cross-site iframes — Medium

**Problem.** In the cross-site Shell iframe, browser privacy controls (Safari ITP,
third-party cookie deprecation) can block the app-origin cookie, forcing each app to
build a bearer-header fallback — fragile and duplicated per app.

**Proposed contract.** A documented standard Hosty session mechanism for embedded
apps: baked-in partitioned-cookie (CHIPS) issuance, or a standard Shell↔app token
handshake (e.g. `postMessage`) that yields a reliable session without per-app cookie
hacks.

**How Media Server uses it.** Uses the standard mechanism instead of the bespoke
cookie-when-allowed + in-memory bearer fallback.

**Workaround.** HttpOnly cookie when browser policy allows + in-memory/`sessionStorage`
bearer-token fallback (already designed).

**Acceptance criteria.**
- Works under Safari ITP and with third-party cookies disabled.
- No top-level redirects or popups.
- Documented and consistent across apps.

## 10. Restore-time external-mount path remapping — Medium

**Problem.** Restoring app data on a different host means catalog root paths differ,
so every catalog goes offline until reconfigured.

**Proposed contract.** On restore (especially cross-host), Core detects the
declared external mounts (see #1) and prompts the operator to remap them to new host
paths before the app starts, then injects the corrected binds.

**How Media Server uses it.** Starts with corrected roots; no offline storm.

**Workaround.** App marks unreachable roots Offline; operator re-points paths and
rescans (see [Catalogs](catalogs.md)).

**Acceptance criteria.**
- Restore flow surfaces declared external mounts for remapping.
- App receives corrected paths at first start after restore.
- Depends on #1.

## 11. LAN service advertisement / UDP discovery — Low

**Problem.** Jellyfin clients can auto-discover servers on `7359/udp`, which does
not map onto Core port assignment.

**Proposed contract.** A Core mechanism to advertise a discoverable service on the
LAN (a broadcast responder) tied to a public endpoint, or permission for the app to
bind the discovery UDP port.

**How Media Server uses it.** Enables Infuse auto-discovery of the `jellyfin`
endpoint instead of manual URL entry.

**Workaround.** Manual server URL entry in Infuse.

**Acceptance criteria.**
- Optional, off by default.
- Advertises the `jellyfin` endpoint's external origin on the LAN.

## 12. Secret rotation surface + change notification — Low

**Problem.** Secret settings (e.g. `TMDB_API_KEY`) have no rotation flow or change
event, so the app cannot react to a rotated secret without a restart.

**Proposed contract.** A Core change event/webhook when a setting (including a
secret) changes, optionally with a rotation UI.

**How Media Server uses it.** Reloads the affected secret live on change.

**Workaround.** Re-read settings on restart.

**Acceptance criteria.**
- Change event delivered to the app (reusing the hook pattern of #6).
- Secret values remain injected securely and redacted from logs.

## 13. Host resource/disk info and quotas — Low

**Problem.** The app can only see the catalog volume's own free space; it cannot
reason about overall host disk/CPU or per-app quotas.

**Proposed contract.** A read-only Core endpoint or injected env exposing host
resource info, and/or a per-app quota declaration in the manifest.

**How Media Server uses it.** Capacity planning and surfacing warnings beyond a
single catalog volume.

**Workaround.** Read catalog-volume free space directly.

**Acceptance criteria.**
- Read-only host disk/CPU info available to the app.
- Optional quota declaration honored by Core.

## 14. Intra-app service-to-service URL discovery — Planned (2026-06-17)

**Status.** Implemented in Hosty Core (`docker-host`, working tree): `dependsOn` now drives
both startup ordering **and** intra-app discovery. Core injects `HOSTY_SERVICE_{KEY}_URL` with
the depended-on sibling's **internal** base URL — distinct from the cross-app
`HOSTY_DEPENDENCY_{KEY}_URL` namespace. Entries accept the string form (`"api"`) or the object
form (`{ "service": "api", "port": "internal" }`); the target port is the named port, else the
sibling's first non-`public` port. Under `docker`, siblings join a per-app user network and
resolve by service-name DNS at the container port (e.g. `http://api:3000`, internal port not
host-published); under `dev`/`localCommand`, over loopback at the assigned port. Media Server's
`web` reads `HOSTY_SERVICE_API_URL` and proxies REST + SignalR to `api`'s internal port — no
public exposure of the management API, no app-side port pinning, no `host.docker.internal`
detour. Unblocks the `docker` M4 delivery target.

**Problem (historical).** Media Server is one app with two services: `web` (the Next.js BFF)
must reach `api`'s **internal** (non-public) port to proxy REST and SignalR. Core
already has two dependency mechanisms, and they are easy to conflate — but
**neither** wires this intra-app hop:

- **Service-level `dependsOn`** (`services[].dependsOn`) governs **startup order
  only.** Core topologically sorts services so a dependency boots first; it
  injects no environment variable. `web`'s `dependsOn: ["api"]` guarantees `api`
  starts before `web` and nothing more.
- **App-level `dependencies`** (the manifest-root array) is the **only** source of
  `HOSTY_DEPENDENCY_{KEY}_URL`. Core resolves each entry against a *different
  installed app* by `appId` and returns that app's **public endpoint** URL. It is
  cross-app, not intra-app, and resolves only to declared endpoints — the internal
  `api` port is intentionally not an endpoint.

Why the `web → api` hop has no channel today:
- `HOSTY_PORT_{KEY}` is injected only into the service that *owns* the port, so
  `web` never receives `api`'s port.
- App containers run on the default Docker bridge with no shared user network, so
  there is no service-name DNS between siblings (only `host.docker.internal` is
  mapped).

So there is no Core-provided way for `web` to learn `api`'s internal address. This
is the same gap tracked as Open Risk #2 in the
[implementation plan](implementation-plan.md). It blocks a clean `web → api` proxy;
the `docker` profile (the v1 delivery target) cannot ship it without the security
regression below.

**Proposed contract.** Keep the two concerns **separate** — do *not* overload the
cross-app `dependencies` array (different lifecycle: `appId`, versioning,
optional/required, resolves to *public* endpoints). Instead reuse the declaration
the app already makes for ordering: when service B lists service A in `dependsOn`,
Core *additionally* injects A's **internal** base URL into B under a distinct env
name. One declaration drives both ordering and discovery; the namespaces never
collide.

```jsonc
// manifest — same declaration, richer effect
{ "key": "web", "dependsOn": ["api"] }
// optional explicit port form when a service exposes several ports:
{ "key": "web", "dependsOn": [{ "service": "api", "port": "internal" }] }
```

```text
# injected into web at runtime (distinct prefix, NOT HOSTY_DEPENDENCY_*)
HOSTY_SERVICE_API_URL=http://<api-internal-host>:<port>
```

- Distinct prefix `HOSTY_SERVICE_{KEY}_URL` keeps the intra-app and cross-app
  (`HOSTY_DEPENDENCY_{KEY}_URL`) namespaces legible and collision-free.
- Resolves to the depended-on service's internal port (its first non-public port,
  or a named port) on a network the dependent service can actually reach: under
  `docker`, a shared per-app user network with service-name DNS; under `dev`, the
  assigned loopback host/port.
- `dependsOn` keeps its existing ordering guarantee unchanged — URL injection is
  purely additive.

**How Media Server uses it.** `web` reads `HOSTY_SERVICE_API_URL` and proxies REST
and SignalR to `api`'s internal port. No public exposure of the management API, no
app-side port pinning, no `host.docker.internal` detour.

**Workaround.** Pin `api`'s internal port (manifest `localPort`/`hostPort` or a
`HOSTY_PORT_INTERNAL` setting) and have `web` build the URL itself. Under `dev` this
reaches `api` on `127.0.0.1:{pinned}`. Under `docker` it is insufficient unless the
internal port is also marked `expose: host` (reachable via
`host.docker.internal:{pinned}`) — which publishes the internal management API on
all host interfaces, a security regression.

**Acceptance criteria.**
- A service that `dependsOn` a sibling receives the sibling's internal base URL
  under a stable, documented env var, distinct from `HOSTY_DEPENDENCY_*`.
- The URL targets the sibling's internal (non-public) port and is reachable from
  the dependent service under both `docker` and `dev`.
- No need to expose internal ports publicly or pin ports app-side.
- Cross-app `dependencies` semantics are unchanged; the two mechanisms remain
  documented as separate concerns (ordering + intra-app discovery vs cross-app
  endpoint resolution).
