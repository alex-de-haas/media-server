# Native Client API

Created: 2026-08-04
Updated: 2026-09-10

## Description

`/native/v1` is the first-party HTTP surface Media Server's own clients read. It
carries what the domain actually holds — catalogs, named editions, sidecar tracks
with fetchable URLs, people, recommendations, the release calendar, the watch
diary — rather than what a Jellyfin DTO can express.

The [Jellyfin surface](../jellyfin-compatibility/feature.md) is untouched and
supported. Both read the same services and write through the same ones, so a
title's watched state is identical whichever client set it.

This feature ends where a client can **browse**. Playback negotiation, track
preferences and playback sessions are [native-playback](../native-playback/feature.md).

## Where it is served

The surface lives on the existing route table. Kestrel binds `api` to both the
internal and the public port and there is no per-port route filtering, so
`/native/v1` is reachable on the public endpoint with no manifest change and no
second public origin.

### The public binding carries an allowlist

Sharing one route table publishes everything by default, which meant the whole
internal surface — catalog administration included — was reachable from outside,
held shut only by Host identity. The public binding now serves exactly:

1. the Jellyfin surface,
2. `/native/v1`,
3. the signed media and image URLs under it.

Anything else answers **404 there** while staying available on the internal
binding. A 404 rather than a 401, because the existence of an administration
surface is not something an unauthenticated caller needs confirmed.

The check is endpoint **metadata** (`AllowPublic()`), not a path prefix, so a
route group added later is unpublished until somebody marks it deliberately — the
safe direction for the mistake to fall. Which port a request arrived on is read
from `HostyOptions.PublicBindPort`; under `docker` the injected `HOSTY_PORT_*` is
the published *host* port rather than what Kestrel listens on, so the container
ports come from the image instead.

## Authentication

The app writes no authentication code. Hosty Core's device authorization flow
issues a credential, the app-identity exchange turns it into a token audience-
scoped to this app, and the SDK's existing `HostyAuthenticationHandler` validates
it. Assignment is enforced at issuance and re-checked on every request;
revocation lives in Shell.

`GET /native/v1/server/public` is the one anonymous route: a client that has never
paired holds no token, so putting the Core origin behind the token would mean
needing a token to discover where tokens come from. It carries the server name,
the app id, the surface version and `CorePublicOrigin`, and nothing about the
library, the users, or which integrations are configured.

`GET /native/v1/server` is authenticated and reports what the instance can
actually do, so a client hides what the server cannot do rather than failing on
use.

### Signed URL tokens

`AVPlayer` does not attach an `Authorization` header to the ranged requests it
issues itself, so media URLs carry a signed token instead — the same reason the
Jellyfin surface accepts `api_key=`.

A token is bound to the user, the **media source** (not merely the title), and the
methods it may be spent on, and lasts long enough to outlive a whole film with
pauses: one that expires between two `Range` requests of one file is a broken
token. The HMAC key is generated on first use and persisted under the app data
directory, so a restart does not interrupt a viewer mid-film.

## Crew credits

Item details expose an optional `crew` array projected from stored person credits.
Each entry carries the credit ID, provider identity, name, job, department, and
public portrait URL. Cast credits are excluded. Existing `directors` and `creators`
name arrays remain available for older clients and incomplete enrichment.

## Delta sync

`GET /native/v1/sync?cursor=…` feeds a client's local mirror, so browsing costs no
round-trip. Library cards include optional `videoFormats`: distinct HDR/Dolby Vision
labels across a movie's video sources, without profiles. SDR, unknown formats,
audio streams, and embedded cover images do not contribute labels. This is a
source-format summary, not a device compatibility result; older responses omit
it. Formats are projected for the page without requesting individual item details.

A sync begins with a bounded keyset snapshot of the published library and then
rides the change log from the sequence captured **before** that snapshot started.
Capturing the watermark first is what makes the hand-off lossless: anything that
changes while the snapshot is still paging sits behind it and is replayed.

### The change log

```text
ChangeLog(Sequence, EntityType, EntityId, AppUserId, Kind, OccurredAt)
```

Rows are appended by the same unit of work as the mutation they describe — from
the `DbContext`'s `SaveChanges`/`SaveChangesAsync` override for tracked writes,
and explicitly inside the transaction for the bulk paths that bypass the change
tracker. `LibraryDeleteService` is the important one: it tombstones and purges
through `ExecuteUpdate`/`ExecuteDelete`, and a purge is the case a client cannot
discover any other way, because unlike a tombstone it leaves nothing behind.

`Sequence` is SQLite `AUTOINCREMENT`, so retention pruning can never free a value
for reuse; a client holding a cursor past a pruned range would otherwise go
permanently blind.

Per-user rows carry `AppUserId`, so one user's playback never appears in
another's feed.

### Cursors, resets and retention

The cursor is opaque and carries the schema version, the snapshot watermark and
the last sequence consumed. Pages are idempotent, so a client that dies mid-sync
simply asks again.

A cursor pointing below what the log still holds is answered with
`resetRequired` and a fresh snapshot cursor, rather than a feed with a hole in it.
The pruner keeps a 30-day window and **always retains the newest row**, which is
what makes that check total: an empty log would otherwise be indistinguishable
from a fully pruned one.

An item that no longer resolves to a published item is reported as a removal,
whether it was purged, tombstoned or unpublished. The client does not need to know
which.

## Items and media

`GET /native/v1/items/{id}` returns the same detail projection the web page reads,
**embedded rather than restated** — forking it would let a client and a web page
drift about what a title contains — plus the URLs only this surface adds.

One token is minted per edition and covers the video and every sidecar of that
source: a viewer choosing an external dub reads two files as one playback.

Artwork is served **from this instance**, not from the metadata provider's CDN:
`GET /native/v1/items/{id}/images/{type}` reads the local cache through the same
service the Jellyfin surface uses, so a first request for an image nobody has
fetched yet fills the cache rather than 404ing. A client on the same network as the
server therefore needs no internet at all, and browsing a library stops being
visible to TMDb; the cost is our bandwidth for something a CDN does well, which is
why it is a deliberate choice. The URLs carry the asset's content-hash tag, so they
can be cached hard — new artwork means a new tag and therefore a new URL — and the
item DTO lists only the types the instance actually holds, so a client never asks
for one that cannot exist — each of them the one the shared [artwork
language](../artwork-language/feature.md) ranking selects, including a poster the
operator pinned. These are bearer-authenticated: only `AVPlayer`'s
self-issued ranged requests need a signed URL.

`GET|HEAD /native/v1/media/{mediaSourceId}` serves the file by byte range, and
`…/tracks/{streamId}` serves a sidecar dub or subtitle — a file no existing client
can play at all, and the thing the Jellyfin surface can only announce without a way
to fetch. Resolution refuses an unpublished or tombstoned item, an embedded track
(which has no file of its own), a track belonging to another source, and any path
that does not resolve inside the catalog root.

## The client-facing read surfaces

Recommendations, the watchlist, the release calendar, reminders, people, the watch
diary and the realtime stream each get a thin `/native/v1` route calling the same
service its web counterpart does. Pointing the client at `/api` instead would
leave a large part of its contract outside the OpenAPI document, publish an
internal BFF shape as a public one, and make every change to a web-facing route a
potential client break.

Operator surfaces — torrents, conversions, the ingest review queue, catalog
administration — stay on `/api` and are deliberately unpublished. The desktop
client reaches them the way the web UI does, over the local network.

## The contract

`dotnet build` in `src/api` regenerates the OpenAPI document for `/native/v1` into
`src/api/openapi/`, which is committed. CI diffs it, so a route changed without
refreshing the contract fails the build — the guarantee the generated Swift client
depends on. The internal `/api` surface is excluded deliberately: it is a BFF
contract, not a published one. The document is served at `/openapi/native.json`
off the public binding, since a client generator reads it at development time.

## Native tvOS visual design and collections

The native Apple TV client exposes Movies, Series, Collections, and Settings.
The visual layout and authenticated collection list, detail, and artwork routes
are described in [Apple client visual design](../apple-client-visual-design/feature.md).
Collection reads exclude removed movies from counts, members, and poster fallbacks.

## Episode browsing

The authenticated `/native/v1/items/{id}/episodes` route lists available episodes
with per-user progress and cached landscape artwork. Native series details filter
season summaries to those with visible episodes and media sources. See
[Apple series browsing](../apple-series-browsing/feature.md) for the contract and
visibility rules.

## Indexing progress

Shared media-source and external audio DTOs carry optional `indexing` snapshots. `/native/v1/events` publishes revisioned `indexingChanged` updates, with detail reconciliation after reconnect. See [Indexing progress](../indexing-progress/feature.md).

## Testing Expectations

Backend tests use xUnit and Imposter. Required coverage:

- URL tokens: round trip, expiry across a film-length window, wrong media source,
  disallowed method, tampered payload, another instance's key.
- The public-binding allowlist driven through a real pipeline — metadata lookup,
  port comparison and short-circuit together. A `TestServer` cannot express this:
  it has no real ports and reports `Connection.LocalPort` as 0.
- Bind-port resolution for both runtime profiles.
- The change log: an upsert on insert and edit, `AppUserId` on per-user rows, a
  `Delete` row from the purge path that bypasses the change tracker, and a
  sequence that never reuses a value after the log is pruned empty.
- Sync: snapshot then delta, an unpublished item arriving as a removal, one user's
  playback staying out of another's feed, `resetRequired` past retention and a
  usable cursor afterwards, pruning never removing the newest row, and an
  unreadable cursor starting over rather than failing.
- Media resolution: published sources served, tombstoned refused, embedded tracks
  refused, cross-source tracks refused, and containment against a path that climbs
  out of the catalog root.
- Item URLs built from the real detail projection, so the test breaks if the
  projection stops carrying what they are built from.
- Artwork URLs offered only for the types the instance holds, carrying the tag, and
  absent entirely for an item with no artwork; each role picked by the artwork
  ranking, and a pinned poster offered over the ranking's own answer.

### What only a running instance could show — production run, 2026-09-08

Four claims here key off which binding a request arrived on, or off a caller that
is not the web proxy. A `TestServer` has no real ports and no second binding, so
these were carried live instead, against Core 0.97.3 and `com.haas.media-server`
0.72.3 on the docker runtime.

**The binding split.** Off the published binding, `/native/v1/server/public` answers
200 while `/api/torrents` and `/api/library` answer 404 with an empty body; on the
host, the same `/api/torrents` against the loopback `api` binding answers 401. The
route is therefore alive internally and merely unpublished — the 404/401 pair is the
whole distinction, and nothing but the binding differs between them.

**Middleware ordering.** The ordering itself is settled by the registration in
`Program.cs` — `UseRouting()`, then `UsePublicSurfaceAllowlist(hosty)`, then
`UseAuthentication()` inside its `UseWhen` branch, then `UseAuthorization()` — so an
unpublished route is refused before authentication looks at a credential, and no
bearer for such a route is ever validated against Core. That is the claim, and it is
a property of the pipeline rather than of any one request.

What the live run adds is that the assembled pipeline behaves that way on a real
second binding: the three allowlisted native routes answered 401 while a bogus route
under the same prefix answered 404, and the app's log carried exactly three Hosty
authentication challenges — one per allowlisted route, none for either `/api`
request. Taken alone those observations would be weaker than they look, since an
unauthenticated request produces no challenge whichever side of the allowlist
authentication sits on; they corroborate the registration order rather than
establish it. What they do establish on their own is that the allowlist tells a real
native route from a bogus one under the same prefix, so it decides on the matched
route and therefore runs after routing.

**Route parity.** With an app identity token the native read surfaces answer with
data. Parity itself is structural rather than observed: each handler is
`Results.Ok(await service.X(...))` over the same service its `/api` twin calls, with
no second projection in between, so the two cannot drift without the shared service
moving under both.

**The realtime stream outside the proxy.** `/native/v1/events` and `/api/events` are
the same `SseEndpoints.StreamAsync` method, and the web client reaches it through
the BFF at `/api/proxy/api/events`. What had never been exercised was a caller that
is not that proxy: with an identity token the stream opens and holds, emitting its
`: connected` frame — written only after `notifier.Subscribe()` returns — and
subsequent keepalives. No domain event was witnessed on that particular stream, and
none is owed: delivery is the same subscription the proxied path exercises
continuously, and nothing on it is native-specific.
