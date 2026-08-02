# Native Client API — plan

Status: Draft
Created: 2026-08-02
Updated: 2026-08-02

> The server half of the [Apple client](../apple-client/plan.md) epic. This
> document covers the `/native/v1` surface only. The packaging path that makes MKV
> playable on AVPlayer is a separate feature (`remux-streaming`); this plan defines
> the contract it plugs into and ships without it.

## Goal

A first-party HTTP surface for our own clients, carrying what the domain actually
holds instead of what a Jellyfin DTO can express, and authenticated by a flow a TV
remote can complete.

The [Jellyfin surface](../jellyfin-compatibility/feature.md) is untouched. Both
surfaces read the same services and write through the same ones, so a title's
watched state is identical whichever client set it.

## Where it is served

`/native/v1/…` on the **existing route table**. `HostyKestrel.ConfigureUrls` binds
`api` to both the `internal` and `jellyfin` ports and there is no per-port route
filtering, so a new route group is reachable on the public endpoint with **no
manifest change and no second public origin** — which also keeps the deployment
one level under the zone, where TLS works.

The public port keeps its `jellyfin` key. Renaming it would break every operator's
`HOSTY_PUBLIC_ORIGIN_JELLYFIN` and reverse-proxy config for a cosmetic gain; the
key becomes a historical name for "the public client endpoint" and the docs say so.

Path versioning (`/v1`) rather than a header: additive changes only within a
version, and a client pinned to v1 keeps working when v2 appears.

## Authentication: Hosty's device flow, not one of our own

The Jellyfin surface authenticates with `MediaAccessCredential` — a username and a
6–8 digit PIN typed into a client. On an Apple TV remote that is miserable, and a
short numeric secret on a public endpoint is only safe because of the lockout
machinery in [security.md](../security.md).

**Core already solves this**, and this app writes no authentication code at all.
Hosty's [access tokens](../../../../docker-host/docs/features/access-tokens/feature.md)
feature ships a device authorization flow, and the app-identity exchange composes
on top of it:

1. `POST {core}/api/auth/device/code` → `deviceCode`, an eight-character
   `userCode` drawn from an alphabet with no lookalikes, and a `verificationUri`.
2. The user approves it in Shell → Settings → **Access tokens**, seeing the
   device's label. Polling `POST {core}/api/auth/device/token` then yields a Core
   access token (`Kind = device`).
3. The client exchanges it for an app identity token:
   `POST {core}/api/auth/apps/authorize` (bearer = the access token, `appId` =
   `com.haas.media-server`) → `POST {core}/api/auth/apps/token`.
4. That token goes to this app as `Authorization: Bearer …`, where the SDK's
   existing `HostyAuthenticationHandler` accepts it and revalidates it against
   Core.

Nothing is typed into the TV, no PIN exists to brute-force, and every property the
custom design would have had to build is already there:

- **A bearer-presented Core session is deliberately CSRF-exempt** — the code says
  so in as many words, for native clients — so step 3 works from a device with no
  browser.
- **Assignment is enforced at issuance and re-checked online on every request.**
  `AppIdentityService` refuses a user not assigned to the app, and revalidation
  re-checks disabled/unassigned/role-downgrade. The
  `DirectoryReconcileService` work the custom design needed does not exist here.
- **The app identity token is audience-scoped to this app.** A token on the
  television cannot install apps, read another app's secrets, or manage users —
  the SDK rejects a token whose `AppId` is not ours.
- **Revocation is one place for every device**, in Shell, and it cascades to app
  grants and closes the credential's open event stream.
- **Lifetimes are the platform's**: the app grant is 7 days idle / 30 days
  absolute, the Core access token 90 days idle with no absolute expiry. The client
  re-runs step 3 when the grant lapses and only re-pairs if the access token
  itself has gone.

What this costs, stated plainly:

- **The client must keep the Core access token** to re-mint app grants, and Core
  has no scopes — that credential carries its holder's full role at Core, not just
  at this app. It lives in the Keychain, and Shell's revoke is the recovery path
  for a lost device. Narrowing it for real needs scopes in Core; a client that
  presents itself as narrower is not an authorization boundary, and Core's own
  documentation says so.
- **Core must be reachable by the client**, which the Jellyfin surface never
  required. `GET /native/v1/server` therefore advertises `CorePublicOrigin`, which
  `HostyOptions` already holds, so the client is configured with one URL — this
  app's — and discovers where to pair.
- **`/api/*` is already served on the public port** (there is no per-port route
  filtering), so a native caller reaches it today by accident. This feature makes
  that deliberate and documents it, rather than leaving it as an artifact.

### The one credential this app still owns

`AVPlayer` cannot be relied on to attach an `Authorization` header to media and
image requests — least of all to HLS segment requests it issues itself. So stream,
segment, and image URLs carry a **short-lived, item-scoped URL token** minted by
this app and signed by it, exactly as the Jellyfin surface uses `api_key=` for the
same reason. It is derived from an authenticated session, never long-lived, never
a Core credential, and redacted in logs.

## Target behavior

### Server description

`GET /native/v1/server` — server name, version, the surface version, and the
**capabilities this instance actually has**: whether a transcode engine is
attached, whether packaging is available, whether recommendations and Trakt are
configured. The client hides what the server cannot do rather than failing on
use; the macOS operator surfaces key off the same answer.

### Delta sync

`GET /native/v1/sync?cursor=…` returns changed items, removals, and a new cursor.
The client holds a full local mirror and browses from it, so a screen costs no
round-trip — the single biggest UX difference from a Jellyfin client, which
re-queries per screen.

Two problems this has to solve honestly:

- **Purges are invisible.** [Tombstones](../library-item-tombstones/feature.md)
  keep a row only when the item carries user signal; an untouched item is deleted
  outright and a delta feed would never mention it. So sync needs a small
  **deletion log** written by the delete paths (`LibraryDeleteService`,
  `CatalogService.DeleteAsync`), pruned on a retention window, plus a
  **full-resync fallback**: a cursor older than the retention answers `full: true`
  with the complete id set, and the client drops whatever is not in it.
- **`UpdatedAt` must be trustworthy.** The feed is only as correct as the
  watermark, so the write paths need an audit — a mutation that forgets to bump
  it is a row the client never sees again — plus an index on `(UpdatedAt, Id)`
  for the keyset pagination.

User data changes far more often than items, so it carries its own watermark
inside the same opaque cursor rather than dragging item pages behind it.

### Items

The item DTO **projects from `LibraryReadService`**, the same service the web UI
reads, extended rather than forked. Web and client disagreeing about what a detail
page contains is a bug, not a platform difference.

What it must carry beyond the Jellyfin projection:

- the **catalog**, as a first-class field;
- **named editions** — `MediaSource.VersionName` as a label, not a position in an
  array, plus the user's default-version pin;
- **sidecar tracks with delivery URLs** — both audio and subtitle external streams
  from [external track sidecars](../external-track-sidecars/feature.md). The
  subtitle half is what [external subtitle
  delivery](../external-subtitle-delivery/plan.md) parked; that plan's endpoint is
  Jellyfin-shaped, this one is ours, and both can serve the same file through the
  catalog sandbox;
- **people** with the stable provider identity the person page already uses;
- **collection membership**, chapters where the probe recorded them, and **probe
  provenance** (see [probe providers](../media-probe-providers/feature.md)) — a
  thin stream list should be legible as "the header reader answered this", not as
  a broken file.

### Playback resolution

`POST /native/v1/playback/resolve` takes the client's **capability profile**
(containers, video and audio codecs, HDR formats, channel layouts, passthrough)
and answers per media source with one of:

- `directPlay` — a URL served by byte range, the existing direct-stream path;
- `package` — an HLS/fMP4 URL, filled in by `remux-streaming`;
- `unsupported` — with a machine-readable reason, so the client can say "this
  copy's only audio track is DTS" instead of failing silently.

This replaces the Jellyfin surface's `EnableDirectPlay`/`EnableDirectStream`
flags, which are parsed and then ignored because that surface has only one answer.
Until packaging lands, `package` is simply never returned; the contract does not
change when it does.

### Track preferences

A per-user preference, scoped globally, per series, or per item: preferred audio
language, preferred subtitle language, forced-only, and "prefer original audio".

Stored as **intent, never stream indexes** — an index means nothing across two
editions of the same film, and the client resolves intent against whatever the
source actually has. Synced through the same sync feed, so choosing the Russian
dub for a show on the Apple TV holds on the iPhone.

### Playback sessions

Start / progress / stop, carrying the media source, the selected audio and
subtitle streams (including external ones), and the device.

They write through **`UserDataService`**, the same path the Jellyfin surface uses
— no second writer. That is what keeps the watched threshold, the resume rules,
the season/series aggregates, `PlaybackHistoryEntries`, and the Trakt outbox
identical no matter which client played the file.

### Everything else is reached, not rebuilt

Recommendations, watchlist, reminders, people, watch history and the SSE stream
already exist under `/api`, behind Host identity — which is exactly the identity a
native client now presents. They need no policy change at all; the work is
confirming each one behaves for a caller that is not the web BFF.

## Deliverables

One PR.

### Phase 1 — transport and identity

Authentication is the platform's, so this phase is mostly *not* writing code —
and what remains is the URL-token half that Core cannot cover.

- [ ] **`/native/v1` route group** with the version prefix and its own JSON
      options, beside `MapJellyfinEndpoints`, on the `Hosty` scheme.
- [ ] **`GET /native/v1/server`** with the capability answers and
      `CorePublicOrigin`, so one URL configures the client.
- [ ] **Signed URL tokens** for media, segment, and image URLs: minted from an
      authenticated request, short-lived, scoped to one item, redacted in logs,
      and refused on any route outside that set.
- [ ] **Public-port intent** — the `/api` surface being reachable on the public
      endpoint stops being an accident: documented in
      [security.md](../security.md), with a test pinning which route groups are
      expected there.
- [ ] **Unit tests**: URL-token minting, expiry, scope, and rejection on a
      non-media route.

### Phase 2 — sync and items

- [ ] **Deletion log** written by the delete paths, with retention and pruning.
- [ ] **`UpdatedAt` audit** across the write paths, plus the `(UpdatedAt, Id)`
      index.
- [ ] **`GET /native/v1/sync`**: opaque composite cursor, keyset pagination,
      removals, and the `full: true` fallback for a cursor past retention.
- [ ] **Item projection** from `LibraryReadService` with the fields listed above.
- [ ] **Sidecar delivery endpoint** resolving external paths through
      `ICatalogPathSandbox`, serving audio and subtitle sidecars alike.
- [ ] **Image URLs** in the DTO, served by the existing image service with ETag
      and cache headers.
- [ ] **Unit tests**: cursor round-trips, a purge reaching the client, the
      retention fallback, sandbox containment on sidecar delivery, and the
      projection's parity with what the web detail page shows.

### Phase 3 — playback

- [ ] **`POST /playback/resolve`** with the capability profile and the three
      answers; `package` unreachable until `remux-streaming` lands.
- [ ] **Preference entity + endpoints**, resolved as intent against a source.
- [ ] **Session endpoints** writing through `UserDataService`.
- [ ] **Reachability check** over the recommendation, watchlist, reminder, person,
      watch-history and SSE endpoints for a native caller — no policy change
      expected, since it is the same Host identity, but the SSE stream in
      particular has only ever been consumed through the BFF proxy.
- [ ] **Unit tests**: resolution across profiles (including the DTS-only source),
      preference resolution against differing editions, and session writes
      producing the same user data and history rows as the Jellyfin path.

### Phase 4 — contract

- [ ] **OpenAPI document** for `/native/v1`, generated and checked in, and the
      generation step wired so it cannot drift from the routes.
- [ ] **Swift client generation** verified against it (the client itself belongs
      to `apple-client-core`; this is only the proof that the spec generates).

### Closing the plan

- [ ] **`feature.md`** for this feature; update
      [jellyfin-compatibility](../jellyfin-compatibility/feature.md) to say the
      native surface exists beside it, and
      [security.md](../security.md) with the Core device flow, the URL tokens, and
      what is now deliberately reachable on the public port.
- [ ] **Index** — `node scripts/docs-index.mjs --fix`.
- [ ] **Version bump** — new functionality, so a minor: `0.46.0` → `0.47.0` as of
      today. The [Jellyfin people plan](../jellyfin-compatibility/plan.md) claims
      the same number; whichever lands second takes the next one.

## Open questions

- **Where the Core access token lives, and whether it is kept at all.** Keeping it
  means a full-role Core credential on a television, in exchange for silent
  re-minting every 30 days. Discarding it after the first exchange means re-pairing
  monthly on a device with a remote for a keyboard. The plan assumes it is kept, in
  the Keychain — and the question goes away entirely if Core grows a device flow
  that names the app, filed as [platform request
  7a](../hosty-platform-requests.md#7a-device-flow-that-names-the-app--high). That
  request is not a blocker: the two-step chain works today, and the client's
  pairing code changes by one field if it lands.
- **How much of the local mirror is worth it.** Full mirror (instant, offline
  browsing, more client complexity) versus cache-on-demand. The plan assumes full;
  the sync contract supports either, so it can be settled in the client.
- **Sidecar audio delivery is new ground.** Subtitles have a client convention;
  external audio does not, because no existing client can use it. What the client
  does with the URL depends on the packaging answer from the
  [spike](../apple-client/plan.md#phase-0--the-playback-spike) — folded into the
  HLS output as an extra rendition, or fetched separately and played in sync.
  Until that is known, the API only promises the file is fetchable.
- **Admin operations, without scopes to narrow them.** The macOS client wants
  torrents and conversions; they already exist under `/api`, gated on the admin
  role. An admin's Apple TV therefore holds a token that can reach them too, and
  Core has no scopes to say otherwise — narrowing it in the client is presentation,
  not authorization. Living with it is defensible (the token is audience-scoped to
  this app, and revocable in one place); a per-device restriction of our own is the
  alternative, and it only becomes a real boundary if this app binds devices
  itself, which is the machinery reusing Core just removed.
- **Chapters.** The probe records them "where available", and the header reader
  path may not. Worth checking on real data before the DTO promises them.

## Verification steps

1. `dotnet test` for the API test project.
2. Pair end to end against a Core-managed dev runtime: request a device code,
   approve it in Shell's Access tokens tab, collect the credential, exchange it
   for an app identity token, and call `/native/v1/server` with it. Then unassign
   the user in Core and confirm the next request fails — and revoke the credential
   in Shell and confirm the app grant dies with it.
3. Confirm the app identity token is rejected by the Jellyfin surface, and a
   Jellyfin token by `/native/v1`.
4. Sync from an empty cursor, mutate the library (edit, delete a watched item,
   delete an untouched one, delete a catalog), sync again, and confirm the client
   view converges — including the purge, which is the case tombstones do not
   cover.
5. Resolve playback for an MP4 (direct), an MKV (unsupported until packaging), and
   a DTS-only source (unsupported with a reason).
6. Play through the native session endpoints and confirm the resulting
   `UserItemData` and `PlaybackHistoryEntry` rows are indistinguishable from the
   ones Infuse produces for the same play.
7. Confirm Infuse still browses, plays, and syncs state throughout — the Jellyfin
   surface must not regress.
