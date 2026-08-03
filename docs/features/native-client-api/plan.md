# Native Client API — plan

Status: Draft
Created: 2026-08-02
Updated: 2026-08-02

> Part of the [Apple client](../apple-client/plan.md) epic. This feature ends where
> the client can **browse**: pairing, server discovery, delta sync, the item
> projection, and the read-only surfaces around them. Making it **play** —
> resolution, track preferences and playback sessions — is
> [`native-playback`](../native-playback/plan.md); packaging is `remux-streaming`.

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

### The public binding carries an allowlist

Sharing one route table with the internal binding is convenient and, left alone,
publishes everything. So the public binding serves exactly three things:

1. the Jellyfin surface,
2. `/native/v1`,
3. signed media and image URLs.

Anything else answers **404 on the public binding** while remaining available on
the internal one. A 404 rather than a 401, because the existence of an
administration surface is not something an unauthenticated caller needs confirmed.

This is a positive list, not a denylist: a route added later is unpublished until
someone says otherwise, which is the safe direction for a mistake to fall.

## Authentication: Hosty's device flow, not one of our own

The Jellyfin surface authenticates with `MediaAccessCredential` — a username and a
6–8 digit PIN typed into a client. On an Apple TV remote that is miserable, and a
short numeric secret on a public endpoint is only safe because of the lockout
machinery in [security.md](../security.md).

**Core already solves this**, and this app writes no authentication code at all.
Hosty's [access tokens](https://github.com/alex-de-haas/docker-host/blob/main/docs/features/access-tokens/feature.md)
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

- **Re-minting an app grant needs the Core access token**, and Core has no scopes —
  that credential carries its holder's full role at Core, not just at this app. So
  where it is allowed to live is a decision, not a detail:

  | Platform | Keeps the Core token | Consequence |
  | --- | --- | --- |
  | macOS | yes, in the Keychain | silent renewal; it is also the operator machine |
  | tvOS, iOS, iPadOS | **no** | re-pair when the 30-day grant lapses |

  A television is the worst place to leave a credential that can install apps, and
  it is also the device whose owner is most likely to be an administrator. Re-
  pairing monthly on a remote is a real cost, accepted deliberately, and it is
  removed entirely if Core grows [request
  7a](../hosty-platform-requests/feature.md#7a-device-flow-that-names-the-app--high) —
  which then becomes a UX improvement rather than a prerequisite.
- **Core must be reachable by the client**, which the Jellyfin surface never
  required. The client is configured with one URL — this app's — and discovers
  where to pair from `GET /native/v1/server/public`, which **must be anonymous**:
  a client that has never paired holds no token, so putting `CorePublicOrigin`
  behind the token would mean needing a token to find out where tokens come from.
  The Jellyfin surface already splits exactly this way (`/System/Info/Public`
  anonymous, `/System/Info` authenticated).
- **`/api/*` is already served on the public port** (there is no per-port route
  filtering), so today the whole internal surface — administration included — is
  reachable from outside by accident, held shut only by Host identity. An earlier
  draft proposed documenting that as deliberate. That was backwards: this feature
  **adds an allowlist** on the public binding instead, and everything not on it
  answers 404 there — see above.

### The one credential this app still owns

`AVPlayer` cannot be relied on to attach an `Authorization` header to media and
image requests — least of all to the ranged requests it issues itself. So stream
and image URLs carry a **short-lived signed URL token** minted by this app, exactly
as the Jellyfin surface uses `api_key=` for the same reason. It is derived from an
authenticated session, never a Core credential, and redacted in logs.

Scoping it to an item is not enough. A single playback issues many ranged requests
over hours, so the token must be bound to the **user, the media source (not just
the title), the methods it may be used with, and the lifetime of the playback
session** — and it must not be able to expire *between* two `Range` requests of the
same file, which is the failure a naive short expiry produces.

## Target behavior

### Server description, in two halves

`GET /native/v1/server/public` — **anonymous**, and deliberately thin: server
name, the surface version, the app id, and `CorePublicOrigin` (which
`HostyOptions` already holds). This is the bootstrap a never-paired client reads
to learn where to run the device flow. It is served on a public endpoint to
unauthenticated callers, so it carries nothing about the library, the users, or
which optional integrations are configured — only what a client needs to begin.

`GET /native/v1/server` — authenticated, and the full answer: the **capabilities
this instance actually has**, including whether a transcode engine is attached,
whether packaging is available, and whether recommendations and Trakt are
configured. The client hides what the server cannot do rather than failing on
use; the macOS operator surfaces key off the same answer.

### Delta sync

`GET /native/v1/sync?cursor=…` returns changed items, removals, and a new cursor.
The client holds a full local mirror and browses from it, so a screen costs no
round-trip — the single biggest UX difference from a Jellyfin client, which
re-queries per screen.

It is fed by a **monotonic change log**, not by timestamps:

```text
ChangeLog(Sequence INTEGER PK AUTOINCREMENT, EntityType, EntityId, Kind, OccurredAt)
```

A row is appended in the **same `SaveChanges` as the mutation it describes**, so a
change and its notification commit together or not at all.

This replaces an earlier design that paginated on `MediaItem.UpdatedAt` and kept a
separate deletion log. Both are gone, for the same reason: `UpdatedAt` is an
invariant maintained by discipline, and any future write path that forgets to bump
it hides that row from every client forever, silently and permanently. A log that
is written by the same transaction cannot be forgotten by a later contributor.

It also subsumes the deletion problem rather than special-casing it.
[Tombstones](../library-item-tombstones/feature.md) keep a row only when the item
carries user signal, so a purged item leaves no trace to poll — but a purge is just
an event with `Kind = delete`, and the client learns of it like anything else.

The cursor is opaque and carries three things: the **schema version**, the
**snapshot high-watermark**, and the **last sequence** consumed. From that:

- pages are **idempotent** — replaying a cursor yields the same page, so a client
  that dies mid-sync simply asks again;
- a cursor older than the log's retention (**30 days**) is answered with
  `resetRequired`, and the client re-snapshots rather than silently missing
  changes;
- the initial snapshot is **bounded and paginated**, so a large library does not
  hand a new client an unbounded first response.

User data changes far more often than items and is just another `EntityType` in
the same log, so it rides the same cursor without dragging item pages behind it.

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

### Playback is a separate feature

Resolution, track preferences and playback sessions were drafted here and have
**moved to [`native-playback`](../native-playback/plan.md)**. This feature ends
where the client can browse; that one makes it play. Splitting them is what keeps
either reviewable.

### The client-facing surfaces get thin native routes

Recommendations, the release calendar, reminders, people, watch history and the
realtime stream already exist under `/api` behind Host identity — which is exactly
the identity a native client presents — so an earlier draft proposed simply letting
the client call them.

That is rejected. It would leave a large part of the client's contract outside the
OpenAPI document, publish an internal BFF shape as a public one, and make every
future change to a web-facing route a potential client break. Each of these gets a
**thin `/native/v1` route** projecting from the same service the web route uses:
about six read-only endpoints plus the stream.

### Operator surfaces stay internal

Torrents, conversions, the ingest review queue and catalog administration stay on
`/api` and are **deliberately not published**. The macOS client reaches them the
way the web UI does, and only where the server is reachable on the local network.

This is the difference between an operator feature being *available to the desktop
app* and being *exposed on the internet*, and it is the reason the allowlist above
exists.

## Deliverables

One PR.

### Phase 1 — transport and identity

Authentication is the platform's, so this phase is mostly *not* writing code —
and what remains is the URL-token half that Core cannot cover.

- [ ] **`/native/v1` route group** with the version prefix and its own JSON
      options, beside `MapJellyfinEndpoints`, on the `Hosty` scheme.
- [ ] **`GET /native/v1/server/public`** (anonymous) with `CorePublicOrigin`, and
      **`GET /native/v1/server`** (authenticated) with the capability answers, so
      one URL configures the client and pairing can start from a cold install.
- [ ] **Signed URL tokens** for media and image URLs: minted from an
      authenticated request, short-lived, scoped to one item, redacted in logs,
      and refused on any route outside that set.
- [ ] **Public-binding allowlist** — Jellyfin, `/native/v1` and signed media/image
      only; everything else 404s there. Documented in
      [security.md](../security.md), with a test that fails when a new route group
      reaches the public binding without being added deliberately.
- [ ] **Unit tests**: URL-token minting, expiry, scope, and rejection on a
      non-media route; and that `/server/public` answers without a token while
      `/server` does not, with the public half carrying none of the capability
      fields.

### Phase 2 — sync and items

- [ ] **`ChangeLog` table + migration**, appended in the same `SaveChanges` as the
      mutation it describes, for items, user data and deletes.
- [ ] **`GET /native/v1/sync`**: opaque cursor of schema version + snapshot
      watermark + last sequence, idempotent pages, bounded initial snapshot, and
      `resetRequired` past the 30-day retention.
- [ ] **Retention pruning** for the log.
- [ ] **Item projection** from `LibraryReadService` with the fields listed above.
- [ ] **Sidecar delivery endpoint** resolving external paths through
      `ICatalogPathSandbox`, serving audio and subtitle sidecars alike.
- [ ] **Image URLs** in the DTO, served by the existing image service with ETag
      and cache headers.
- [ ] **Unit tests**: cursor round-trips and idempotent replay, a purged item
      reaching the client, `resetRequired` past retention, a mutation and its log
      row committing atomically, sandbox containment on sidecar delivery, and the
      projection's parity with what the web detail page shows.

### Phase 3 — the client-facing surfaces

- [ ] **Thin `/native/v1` routes** for recommendations, the release calendar,
      reminders, people and watch history, projecting from the same services the
      web routes use.
- [ ] **Realtime stream** on `/native/v1`, verified for a caller that is not the
      web BFF — it has only ever been consumed through the proxy.
- [ ] **Unit tests**: each route's parity with its web counterpart, and that no
      operator route is reachable on the public binding.

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
- [ ] **Version bump** — new functionality, so a minor. `manifest.json` is the
      source of truth and reads `0.47.0` today, making the target `0.48.0`; read
      it again when the work lands, since releases in between move it.

## Open questions

- **How much of the local mirror is worth it.** Full mirror (instant, offline
  browsing, more client complexity) versus cache-on-demand. The plan assumes full;
  the sync contract supports either, so it can be settled in the client.
- **Sidecar audio delivery is new ground.** Subtitles have a client convention;
  external audio does not, because no existing client can use it. What the client
  does with the URL depends on the packaging answer from the
  [spike](../apple-client/plan.md#phase-0--the-playback-spike) — folded into the
  repackaged output as an extra track, or fetched separately and played in sync.
  Until that is known, the API only promises the file is fetchable.
- **Admin operations, without scopes to narrow them.** Operator routes are off the
  public binding, so the exposure is bounded by the network rather than by the
  token. What remains is that an administrator's token, used from any device on the
  local network, can reach them — Core has no scopes to say otherwise, and
  narrowing it in the client is presentation, not authorization.
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
5. Confirm the public binding: `/native/v1` and the Jellyfin surface answer there,
   while an operator route (`/api/torrents`) 404s on it and still works on the
   internal one.
6. Confirm Infuse still browses, plays, and syncs state throughout — the Jellyfin
   surface must not regress.
